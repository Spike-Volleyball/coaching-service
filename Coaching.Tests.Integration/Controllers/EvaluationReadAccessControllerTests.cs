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
    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _evaluatorId = Guid.NewGuid();

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

    /// <summary>A club's session run by <see cref="_coachId"/>, with one group <see cref="_evaluatorId"/> scores.</summary>
    private async Task<(EvaluationSession Session, EvaluationGroup Group)> SeedSessionAsync()
    {
        var session = new EvaluationSession
        {
            ClubId = _clubId,
            CoachUserId = _coachId,
            Title = "Autumn trials",
            Status = EvaluationSessionStatus.Draft,
        };
        var group = new EvaluationGroup { SessionId = session.Id, Name = "Court one", EvaluatorUserId = _evaluatorId, Order = 0 };
        await SeedAsync(session, group);
        return (session, group);
    }

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

    // ---- GET /v1/evaluation-sessions/{id} ----

    [Test]
    public async Task GetSession_Anonymously_Returns401WhetherOrNotItExists()
    {
        // Arrange
        var (session, _) = await SeedSessionAsync();

        // Act
        var existing = await _client.GetAsync($"/v1/evaluation-sessions/{session.Id}");
        var missing = await _client.GetAsync($"/v1/evaluation-sessions/{Guid.NewGuid()}");

        // Assert
        existing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetSession_AsItsCoachAnEvaluatorOrTheClubsStaff_Returns200()
    {
        // Arrange
        var (session, _) = await SeedSessionAsync();

        foreach (var reader in new[] { _coachId, _evaluatorId, _staffId })
        {
            SetAuth(reader);

            // Act
            var response = await _client.GetAsync($"/v1/evaluation-sessions/{session.Id}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"reader {reader} may read the session");
        }
    }

    [Test]
    public async Task GetSession_WithoutStanding_AnswersExactlyLikeAMissingSession()
    {
        // Arrange
        var (session, _) = await SeedSessionAsync();
        SetAuth(_strangerId);

        // Act & Assert
        await ShouldAnswerLikeAMissingOne(
            $"/v1/evaluation-sessions/{session.Id}", session.Id, id => $"/v1/evaluation-sessions/{id}");
    }

    // ---- GET /v1/clubs/{clubId}/evaluation-sessions ----

    [Test]
    public async Task GetClubSessions_Anonymously_Returns401()
    {
        // Arrange
        await SeedSessionAsync();

        // Act
        var response = await _client.GetAsync($"/v1/clubs/{_clubId}/evaluation-sessions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetClubSessions_AsTheClubsStaff_ListsThem()
    {
        // Arrange
        var (session, _) = await SeedSessionAsync();
        SetAuth(_staffId);

        // Act
        var ids = await IdsIn(await _client.GetAsync($"/v1/clubs/{_clubId}/evaluation-sessions"));

        // Assert
        ids.Should().Equal(session.Id);
    }

    [Test]
    public async Task GetClubSessions_WithoutStanding_ListsNothing()
    {
        // Arrange
        await SeedSessionAsync();
        SetAuth(_strangerId);

        // Act
        var ids = await IdsIn(await _client.GetAsync($"/v1/clubs/{_clubId}/evaluation-sessions"));

        // Assert
        ids.Should().BeEmpty();
    }

    // ---- a session's progress and scores ----

    [Test]
    public async Task GetProgress_AsItsCoach_Returns200()
    {
        // Arrange
        var (session, _) = await SeedSessionAsync();
        SetAuth(_coachId);

        // Act
        var response = await _client.GetAsync($"/v1/evaluation-sessions/{session.Id}/progress");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task GetProgress_WithoutStanding_AnswersExactlyLikeAMissingSession()
    {
        // Arrange
        var (session, _) = await SeedSessionAsync();
        SetAuth(_strangerId);

        // Act & Assert
        await ShouldAnswerLikeAMissingOne(
            $"/v1/evaluation-sessions/{session.Id}/progress", session.Id, id => $"/v1/evaluation-sessions/{id}/progress");
    }

    [Test]
    public async Task GetScores_AsAnEvaluator_Returns200()
    {
        // Arrange
        var (session, _) = await SeedSessionAsync();
        SetAuth(_evaluatorId);

        // Act
        var response = await _client.GetAsync($"/v1/evaluation-sessions/{session.Id}/scores");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task GetScores_WithoutStanding_AnswersExactlyLikeAMissingSession()
    {
        // Arrange
        var (session, _) = await SeedSessionAsync();
        SetAuth(_strangerId);

        // Act & Assert
        await ShouldAnswerLikeAMissingOne(
            $"/v1/evaluation-sessions/{session.Id}/scores", session.Id, id => $"/v1/evaluation-sessions/{id}/scores");
    }

    [Test]
    public async Task GetGroupExerciseScores_AsItsCoach_Returns200()
    {
        // Arrange
        var (session, group) = await SeedSessionAsync();
        SetAuth(_coachId);

        // Act
        var response = await _client.GetAsync(
            $"/v1/evaluation-sessions/{session.Id}/groups/{group.Id}/exercises/{Guid.NewGuid()}/scores");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task GetGroupExerciseScores_WithoutStanding_AnswersExactlyLikeAMissingSession()
    {
        // Arrange
        var (session, group) = await SeedSessionAsync();
        var exerciseId = Guid.NewGuid();
        SetAuth(_strangerId);

        // Act & Assert
        await ShouldAnswerLikeAMissingOne(
            $"/v1/evaluation-sessions/{session.Id}/groups/{group.Id}/exercises/{exerciseId}/scores",
            session.Id,
            id => $"/v1/evaluation-sessions/{id}/groups/{group.Id}/exercises/{exerciseId}/scores");
    }

    // ---- GET /v1/evaluation-exercises/{id} and a club's exercises ----

    [Test]
    public async Task GetExercise_OutsideAnyClub_OpensAnonymously()
    {
        // Arrange — the public library lists these to anyone, so one of them opens the same way.
        var exercise = new EvaluationExercise { Name = "Serve receive", ClubId = null, CreatedByUserId = _authorId };
        await SeedAsync(exercise);

        // Act
        var response = await _client.GetAsync($"/v1/evaluation-exercises/{exercise.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetExercise_OfAClub_Anonymously_Returns401WhetherOrNotItExists()
    {
        // Arrange
        var exercise = new EvaluationExercise { Name = "Serve receive", ClubId = _clubId, CreatedByUserId = _authorId };
        await SeedAsync(exercise);

        // Act
        var existing = await _client.GetAsync($"/v1/evaluation-exercises/{exercise.Id}");
        var missing = await _client.GetAsync($"/v1/evaluation-exercises/{Guid.NewGuid()}");

        // Assert
        existing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetExercise_OfAClub_AsItsStaff_Returns200()
    {
        // Arrange
        var exercise = new EvaluationExercise { Name = "Serve receive", ClubId = _clubId, CreatedByUserId = _authorId };
        await SeedAsync(exercise);
        SetAuth(_staffId);

        // Act
        var response = await _client.GetAsync($"/v1/evaluation-exercises/{exercise.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetExercise_OfAClub_WithoutStanding_AnswersExactlyLikeAMissingExercise()
    {
        // Arrange
        var exercise = new EvaluationExercise { Name = "Serve receive", ClubId = _clubId, CreatedByUserId = _authorId };
        await SeedAsync(exercise);
        SetAuth(_strangerId);

        // Act & Assert
        await ShouldAnswerLikeAMissingOne(
            $"/v1/evaluation-exercises/{exercise.Id}", exercise.Id, id => $"/v1/evaluation-exercises/{id}");
    }

    [Test]
    public async Task GetClubExercises_ListsThemForStaffOnly()
    {
        // Arrange
        var exercise = new EvaluationExercise { Name = "Serve receive", ClubId = _clubId, CreatedByUserId = _authorId };
        await SeedAsync(exercise);
        var path = $"/v1/clubs/{_clubId}/evaluation-exercises";

        // Act
        var anonymous = await _client.GetAsync(path);
        SetAuth(_staffId);
        var staff = await IdsIn(await _client.GetAsync(path));
        SetAuth(_strangerId);
        var stranger = await IdsIn(await _client.GetAsync(path));

        // Assert
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        staff.Should().Equal(exercise.Id);
        stranger.Should().BeEmpty();
    }

    // ---- GET /v1/clubs/{clubId}/thresholds ----

    [Test]
    public async Task GetClubThresholds_ListsThemForStaffOnly()
    {
        // Arrange
        var threshold = new EvaluationThreshold { ClubId = _clubId, Skill = VolleyballSkill.Passing, MinScore = 6, CreatedByUserId = _authorId };
        await SeedAsync(threshold);
        var path = $"/v1/clubs/{_clubId}/thresholds";

        // Act
        var anonymous = await _client.GetAsync(path);
        SetAuth(_staffId);
        var staff = await IdsIn(await _client.GetAsync(path));
        SetAuth(_strangerId);
        var stranger = await IdsIn(await _client.GetAsync(path));

        // Assert
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        staff.Should().Equal(threshold.Id);
        stranger.Should().BeEmpty();
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
