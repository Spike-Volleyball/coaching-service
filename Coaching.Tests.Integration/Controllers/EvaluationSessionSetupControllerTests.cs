using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// Setting up an evaluation session end to end, as the web setup page does it: the session read
/// that page renders from, and the writes it makes along the way.
/// </summary>
[TestFixture]
[Category("Integration")]
public class EvaluationSessionSetupControllerTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private readonly Guid _clubId = Guid.NewGuid();
    private readonly Guid _coachId = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
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

    private EvaluationSession Session(EvaluationSessionStatus status = EvaluationSessionStatus.Draft) =>
        new() { ClubId = _clubId, CoachUserId = _coachId, Title = "Autumn trials", Status = status };

    private async Task<JsonElement> ReadSessionAsync(Guid sessionId)
    {
        var response = await _client.GetAsync($"/v1/evaluation-sessions/{sessionId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    [Test]
    public async Task GetSession_CarriesItsLiveGroupsInOrderWithTheirLivePlayers()
    {
        // Arrange — the setup page renders groups from this read; it used to arrive with none.
        var session = Session();
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        var second = new EvaluationGroup { SessionId = session.Id, Name = "Court two", Order = 1 };
        var first = new EvaluationGroup { SessionId = session.Id, Name = "Court one", Order = 0 };
        var deletedGroup = new EvaluationGroup { SessionId = session.Id, Name = "Court three", Order = 2, IsDeleted = true };
        await SeedAsync(
            session, second, first, deletedGroup,
            new EvaluationGroupPlayer { GroupId = first.Id, PlayerId = kept },
            new EvaluationGroupPlayer { GroupId = first.Id, PlayerId = dropped, IsDeleted = true });
        SetAuth(_coachId);

        // Act
        var read = await ReadSessionAsync(session.Id);

        // Assert
        var groups = read.GetProperty("groups").EnumerateArray().ToList();
        groups.Select(g => g.GetProperty("name").GetString()).Should().Equal("Court one", "Court two");
        groups[0].GetProperty("players").EnumerateArray().Select(p => p.GetProperty("playerId").GetGuid())
            .Should().Equal(kept);
        groups[1].GetProperty("players").GetArrayLength().Should().Be(0);
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
