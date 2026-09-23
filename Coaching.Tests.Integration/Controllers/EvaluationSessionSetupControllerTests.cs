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

    /// <summary>
    /// A started session: one exercise with one metric, two players each with the evaluation the
    /// start creates, and two groups, one player each, with an evaluator apiece.
    /// </summary>
    private async Task<RunningSession> SeedRunningSessionAsync()
    {
        var exercise = new EvaluationExercise { Name = "Serve receive", CreatedByUserId = _coachId };
        var metric = new EvaluationMetric { ExerciseId = exercise.Id, Name = "Accuracy", MaxPoints = 10, Order = 1 };
        var plan = new EvaluationPlan { ClubId = _clubId, CreatedByUserId = _coachId, Name = "Autumn" };
        var item = new EvaluationPlanItem { PlanId = plan.Id, ExerciseId = exercise.Id, Order = 1 };
        var session = Session(EvaluationSessionStatus.Running);
        session.EvaluationPlanId = plan.Id;

        var run = new RunningSession(session.Id, exercise.Id, metric.Id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var entities = new List<object> { exercise, metric, plan, item, session };
        foreach (var (player, evaluator, order) in new[] { (run.FirstPlayer, run.FirstEvaluator, 0), (run.SecondPlayer, run.SecondEvaluator, 1) })
        {
            var participant = new EvaluationParticipant { EvaluationSessionId = session.Id, PlayerId = player };
            var group = new EvaluationGroup { SessionId = session.Id, Name = $"Court {order + 1}", EvaluatorUserId = evaluator, Order = order };
            entities.Add(participant);
            entities.Add(new PlayerEvaluation { EvaluationParticipantId = participant.Id, PlayerId = player, EvaluatedByUserId = _coachId, SessionId = session.Id });
            entities.Add(group);
            entities.Add(new EvaluationGroupPlayer { GroupId = group.Id, PlayerId = player });
        }

        await SeedAsync(entities.ToArray());
        return run;
    }

    private sealed record RunningSession(
        Guid SessionId, Guid ExerciseId, Guid MetricId,
        Guid FirstPlayer, Guid FirstEvaluator, Guid SecondPlayer, Guid SecondEvaluator);

    private Task<HttpResponseMessage> SubmitScoreAsync(RunningSession run, Guid playerId) =>
        _client.PostAsJsonAsync($"/v1/evaluation-sessions/{run.SessionId}/scores", new
        {
            playerId,
            exerciseId = run.ExerciseId,
            scores = new[] { new { metricId = run.MetricId, value = 8 } },
        });

    private async Task<List<Guid>> ScoredPlayersAsync(Guid sessionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        return await db.Set<PlayerExerciseScore>().AsNoTracking()
            .Where(s => s.SessionId == sessionId && s.Status == EvaluationScoreStatus.Scored)
            .Select(s => s.PlayerId)
            .ToListAsync();
    }

    private async Task<int> StoredSessionsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoachingDbContext>().Set<EvaluationSession>().CountAsync();
    }

    [Test]
    public async Task Create_WithoutTheRightToEvaluateInTheClub_Returns403AndStoresNothing()
    {
        // Arrange — anyone signed in could open a session in any club, and its list then showed it.
        var outsider = Guid.NewGuid();
        SetAuth(outsider);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/evaluation-sessions", new { clubId = _clubId, title = "Autumn trials" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredSessionsAsync()).Should().Be(0);
    }

    [Test]
    public async Task Create_BySomeoneWhoMayEvaluateInTheClub_Returns201WithADraft()
    {
        // Arrange
        _factory.ClubsGrpcClient.CanGiveFeedbackInClubAsync(_coachId, _clubId).Returns(true);
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/evaluation-sessions", new { clubId = _clubId, title = "Autumn trials" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("Draft");
        (await StoredSessionsAsync()).Should().Be(1);
    }

    [Test]
    public async Task Create_Anonymously_Returns401()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/v1/evaluation-sessions", new { clubId = _clubId, title = "Autumn trials" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task SubmitScores_ByAnEvaluatorForAPlayerOutsideTheirGroup_Returns403AndScoresNothing()
    {
        // Arrange
        var run = await SeedRunningSessionAsync();
        SetAuth(run.FirstEvaluator);

        // Act
        var response = await SubmitScoreAsync(run, run.SecondPlayer);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ScoredPlayersAsync(run.SessionId)).Should().BeEmpty();
    }

    [Test]
    public async Task SubmitScores_ByAnEvaluatorForThePlayerInTheirGroup_ScoresThem()
    {
        // Arrange
        var run = await SeedRunningSessionAsync();
        SetAuth(run.FirstEvaluator);

        // Act
        var response = await SubmitScoreAsync(run, run.FirstPlayer);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ScoredPlayersAsync(run.SessionId)).Should().Equal(run.FirstPlayer);
    }

    [Test]
    public async Task SubmitScores_ByTheSessionsCoach_ScoresAPlayerInAnyGroup()
    {
        // Arrange
        var run = await SeedRunningSessionAsync();
        SetAuth(_coachId);

        // Act
        var response = await SubmitScoreAsync(run, run.SecondPlayer);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ScoredPlayersAsync(run.SessionId)).Should().Equal(run.SecondPlayer);
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
