using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Coaching.Domain.Models.Evaluation;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// Reading a club's evaluation material (SPI-6459). None of it is public: an anonymous caller is
/// asked to sign in whether or not the thing exists, a signed-in caller without standing gets the
/// same 404 a missing one gives — body and all — and a club's list shows them what a club with
/// nothing in it shows.
/// </summary>
[TestFixture]
[Category("Integration")]
public class EvaluationReadAccessControllerTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private readonly Guid _clubId = Guid.NewGuid();
    private readonly Guid _authorId = Guid.NewGuid();
    private readonly Guid _staffId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
        _factory.ClubsGrpcClient.IsClubStaffAsync(_staffId, _clubId).Returns(true);
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

    private async Task SeedAsync(params object[] entities)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private EvaluationPlan ClubPlan(string name = "Autumn trials") =>
        new() { ClubId = _clubId, CreatedByUserId = _authorId, Name = name };

    private static async Task<List<Guid>> IdsIn(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.ValueKind == JsonValueKind.Array
            ? body.RootElement
            : body.RootElement.GetProperty("items");
        return items.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
    }

    /// <summary>A refusal must read exactly like an id that was never there; only the id differs.</summary>
    private async Task ShouldAnswerLikeAMissingOne(string refusedPath, Guid refusedId, Func<Guid, string> pathFor)
    {
        var missingId = Guid.NewGuid();

        var refused = await _client.GetAsync(refusedPath);
        var missing = await _client.GetAsync(pathFor(missingId));

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var refusedBody = await refused.Content.ReadAsStringAsync();
        var missingBody = await missing.Content.ReadAsStringAsync();
        refusedBody.Replace(refusedId.ToString(), "{id}")
            .Should().Be(missingBody.Replace(missingId.ToString(), "{id}"));
        refusedBody.Should().Contain("ENTITY_NOT_FOUND");
    }

    // ---- GET /v1/evaluation-plans/{id} ----

    [Test]
    public async Task GetPlan_Anonymously_Returns401WhetherOrNotThePlanExists()
    {
        // Arrange
        var plan = ClubPlan();
        await SeedAsync(plan);

        // Act
        var existing = await _client.GetAsync($"/v1/evaluation-plans/{plan.Id}");
        var missing = await _client.GetAsync($"/v1/evaluation-plans/{Guid.NewGuid()}");

        // Assert
        existing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetPlan_AsItsAuthor_Returns200()
    {
        // Arrange
        var plan = ClubPlan();
        await SeedAsync(plan);
        SetAuth(_authorId);

        // Act
        var response = await _client.GetAsync($"/v1/evaluation-plans/{plan.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetPlan_AsStaffOfItsClub_Returns200()
    {
        // Arrange
        var plan = ClubPlan();
        await SeedAsync(plan);
        SetAuth(_staffId);

        // Act
        var response = await _client.GetAsync($"/v1/evaluation-plans/{plan.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetPlan_WithoutStandingInItsClub_AnswersExactlyLikeAMissingPlan()
    {
        // Arrange
        var plan = ClubPlan();
        await SeedAsync(plan);
        SetAuth(_strangerId);

        // Act & Assert
        await ShouldAnswerLikeAMissingOne($"/v1/evaluation-plans/{plan.Id}", plan.Id, id => $"/v1/evaluation-plans/{id}");
    }

    [Test]
    public async Task GetPlan_SomeoneElsesPersonalPlan_AnswersExactlyLikeAMissingPlan()
    {
        // Arrange
        var plan = new EvaluationPlan { ClubId = null, CreatedByUserId = _authorId, Name = "Mine" };
        await SeedAsync(plan);
        SetAuth(_staffId);

        // Act & Assert
        await ShouldAnswerLikeAMissingOne($"/v1/evaluation-plans/{plan.Id}", plan.Id, id => $"/v1/evaluation-plans/{id}");
    }

    // ---- GET /v1/clubs/{clubId}/evaluation-plans ----

    [Test]
    public async Task GetClubPlans_Anonymously_Returns401()
    {
        // Arrange
        await SeedAsync(ClubPlan());

        // Act
        var response = await _client.GetAsync($"/v1/clubs/{_clubId}/evaluation-plans");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetClubPlans_AsTheClubsStaff_ListsThem()
    {
        // Arrange
        var plan = ClubPlan();
        await SeedAsync(plan);
        SetAuth(_staffId);

        // Act
        var ids = await IdsIn(await _client.GetAsync($"/v1/clubs/{_clubId}/evaluation-plans"));

        // Assert
        ids.Should().Equal(plan.Id);
    }

    [Test]
    public async Task GetClubPlans_WithoutStanding_ListsNothing()
    {
        // Arrange
        await SeedAsync(ClubPlan());
        SetAuth(_strangerId);

        // Act
        var ids = await IdsIn(await _client.GetAsync($"/v1/clubs/{_clubId}/evaluation-plans"));

        // Assert
        ids.Should().BeEmpty();
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
