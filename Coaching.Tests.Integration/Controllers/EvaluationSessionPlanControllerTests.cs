using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// Giving a session its plan through PUT /v1/evaluation-sessions/{id}. The plan must be one the
/// session may use — the coach's own, or one of the session's club the coach may read — and it is
/// fixed once the session starts, because the start builds a score for every player and exercise.
/// </summary>
[TestFixture]
[Category("Integration")]
public class EvaluationSessionPlanControllerTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private readonly Guid _clubId = Guid.NewGuid();
    private readonly Guid _otherClubId = Guid.NewGuid();
    private readonly Guid _coachId = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
        _factory.ClubsGrpcClient.IsClubStaffAsync(_coachId, _clubId).Returns(true);
        _factory.ClubsGrpcClient.IsClubStaffAsync(_coachId, _otherClubId).Returns(true);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
    }

    private async Task<EvaluationSession> SeedSessionAsync(EvaluationSessionStatus status = EvaluationSessionStatus.Draft, Guid? planId = null)
    {
        var session = new EvaluationSession { ClubId = _clubId, CoachUserId = _coachId, Title = "Autumn trials", Status = status, EvaluationPlanId = planId };
        await SeedAsync(session);
        return session;
    }

    private async Task<EvaluationPlan> SeedPlanAsync(Guid? clubId, Guid createdBy)
    {
        var plan = new EvaluationPlan { ClubId = clubId, CreatedByUserId = createdBy, Name = "Serve and pass" };
        await SeedAsync(plan);
        return plan;
    }

    private async Task SeedAsync(params object[] entities)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private async Task<EvaluationSession> StoredAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        return await db.Set<EvaluationSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId);
    }

    private Task<HttpResponseMessage> PutAsync(Guid sessionId, object body) =>
        _client.PutAsJsonAsync($"/v1/evaluation-sessions/{sessionId}", body);

    private static async Task<List<string>> FieldsNamedBy(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("field").GetString()!).ToList();
    }

    [Test]
    public async Task Update_WithTheCoachsOwnPlan_AttachesIt()
    {
        // Arrange
        var session = await SeedSessionAsync();
        var plan = await SeedPlanAsync(clubId: null, createdBy: _coachId);
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(session.Id, new { evaluationPlanId = plan.Id });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredAsync(session.Id)).EvaluationPlanId.Should().Be(plan.Id);
    }

    [Test]
    public async Task Update_WithAPlanOfTheSessionsClub_AttachesIt()
    {
        // Arrange
        var session = await SeedSessionAsync();
        var plan = await SeedPlanAsync(clubId: _clubId, createdBy: Guid.NewGuid());
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(session.Id, new { evaluationPlanId = plan.Id });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredAsync(session.Id)).EvaluationPlanId.Should().Be(plan.Id);
    }

    [Test]
    public async Task Update_WithAPlanTheSessionMayNotUse_AnswersExactlyLikeAMissingPlan()
    {
        // Arrange — someone else's personal plan, and another club's plan the coach can read.
        var session = await SeedSessionAsync();
        var personal = await SeedPlanAsync(clubId: null, createdBy: Guid.NewGuid());
        var otherClubs = await SeedPlanAsync(clubId: _otherClubId, createdBy: Guid.NewGuid());
        var missingId = Guid.NewGuid();
        SetAuth(_coachId);

        // Act
        var missing = await PutAsync(session.Id, new { evaluationPlanId = missingId });
        var missingBody = await missing.Content.ReadAsStringAsync();

        // Assert
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var plan in new[] { personal, otherClubs })
        {
            var refused = await PutAsync(session.Id, new { evaluationPlanId = plan.Id });
            refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await refused.Content.ReadAsStringAsync()).Should().Be(missingBody);
        }
        (await StoredAsync(session.Id)).EvaluationPlanId.Should().BeNull();
    }

    [Test]
    public async Task Update_ChangingThePlanOfARunningSession_Returns400NamingItAndKeepsThePlan()
    {
        // Arrange
        var current = await SeedPlanAsync(clubId: null, createdBy: _coachId);
        var next = await SeedPlanAsync(clubId: null, createdBy: _coachId);
        var session = await SeedSessionAsync(EvaluationSessionStatus.Running, current.Id);
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(session.Id, new { evaluationPlanId = next.Id });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldsNamedBy(response)).Should().Equal("evaluationPlanId");
        (await StoredAsync(session.Id)).EvaluationPlanId.Should().Be(current.Id);
    }

    [Test]
    public async Task Update_MovingTheStatus_Returns400NamingItAndKeepsTheStatus()
    {
        // Arrange
        var session = await SeedSessionAsync(EvaluationSessionStatus.Running);
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(session.Id, new { status = "Draft" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldsNamedBy(response)).Should().Equal("status");
        (await StoredAsync(session.Id)).Status.Should().Be(EvaluationSessionStatus.Running);
    }

    private void SetAuth(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
            new Claim(ClaimTypes.Email, "coach@test.com"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(
            issuer: CoachingApiFactory.JwtIssuer,
            audience: CoachingApiFactory.JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
