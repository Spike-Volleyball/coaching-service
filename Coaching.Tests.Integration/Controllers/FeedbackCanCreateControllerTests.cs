using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Coaching.Application.Interfaces.Services;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Shared.Enums;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// The question the clients ask before they draw a "Give feedback" button. A team's coach holds
/// their coaching role on the team, not on the club row, so the endpoint has to be askable about
/// a team or a group — asking about the club alone is what hid the button (SPI-5906).
///
/// A roster screen asks it about everyone at once through the batch route, which must answer
/// exactly as the single route would for each person.
/// </summary>
[TestFixture]
[Category("Integration")]
public class FeedbackCanCreateControllerTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private record CanCreateResponse(bool CanCreate);

    private record CanCreateBatchResponse(List<Guid> EligibleRecipientIds);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _factory.DisposeAsync();

    [TearDown]
    public void TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _factory.ClubsGrpcClient.ClearSubstitute();
    }

    [TestCase(ContextType.Team)]
    [TestCase(ContextType.Group)]
    public async Task CanCreate_CoachOfTheUnitAskingAboutItsPlayer_AnswersYes(ContextType contextType)
    {
        // Arrange
        var coachId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        _factory.ClubsGrpcClient.CanGiveFeedbackInUnitAsync(coachId, contextType, unitId).Returns(true);
        _factory.ClubsGrpcClient.GetUnitMemberIdsAsync(contextType, unitId).Returns(Roster(playerId));
        _factory.ClubsGrpcClient.ResolveClubIdAsync(contextType, unitId).Returns(Guid.NewGuid());
        SetAuth(coachId);

        // Act
        var response = await GetCanCreateAsync(
            $"recipientUserId={playerId}&contextType={contextType}&contextId={unitId}");

        // Assert
        response!.CanCreate.Should().BeTrue();
    }

    [Test]
    public async Task CanCreate_PlayerAskingAboutATeammate_AnswersNo()
    {
        // Arrange
        var playerId = Guid.NewGuid();
        var teammateId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _factory.ClubsGrpcClient.GetUnitMemberIdsAsync(ContextType.Team, teamId).Returns(Roster(teammateId));
        _factory.ClubsGrpcClient.ResolveClubIdAsync(ContextType.Team, teamId).Returns(Guid.NewGuid());
        SetAuth(playerId);

        // Act
        var response = await GetCanCreateAsync(
            $"recipientUserId={teammateId}&contextType=Team&contextId={teamId}");

        // Assert
        response!.CanCreate.Should().BeFalse();
    }

    [Test]
    public async Task CanCreate_UnitNamedButRecipientPlaysElsewhere_AnswersNo()
    {
        // Arrange
        var coachId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _factory.ClubsGrpcClient.CanGiveFeedbackInUnitAsync(coachId, ContextType.Team, teamId).Returns(true);
        _factory.ClubsGrpcClient.GetUnitMemberIdsAsync(ContextType.Team, teamId).Returns(Roster());
        _factory.ClubsGrpcClient.ResolveClubIdAsync(ContextType.Team, teamId).Returns(Guid.NewGuid());
        SetAuth(coachId);

        // Act
        var response = await GetCanCreateAsync(
            $"recipientUserId={strangerId}&contextType=Team&contextId={teamId}");

        // Assert
        response!.CanCreate.Should().BeFalse();
    }

    [Test]
    public async Task CanCreate_ClubAskedWithoutAUnit_StillAnswersFromTheClubRoles()
    {
        // Arrange
        var coachId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var clubId = Guid.NewGuid();
        _factory.ClubsGrpcClient.CanGiveFeedbackInClubAsync(coachId, clubId).Returns(true);
        _factory.ClubsGrpcClient.GetClubMemberIdsAsync(clubId).Returns(Roster(playerId));
        SetAuth(coachId);

        // Act
        var response = await GetCanCreateAsync($"recipientUserId={playerId}&clubId={clubId}");

        // Assert
        response!.CanCreate.Should().BeTrue();
    }

    [Test]
    public async Task CanCreateBatch_CoachAskingAboutTheWholeRoster_ListsItsMembersAndNotTheStranger()
    {
        // Arrange
        var coachId = Guid.NewGuid();
        var playerA = Guid.NewGuid();
        var playerB = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var clubId = Guid.NewGuid();
        _factory.ClubsGrpcClient.CanGiveFeedbackInClubAsync(coachId, clubId).Returns(true);
        _factory.ClubsGrpcClient.GetClubMemberIdsAsync(clubId).Returns(Roster(playerA, playerB, coachId));
        SetAuth(coachId);

        // Act
        var response = await _client.GetAsync(
            $"/v1/feedback/can-create/batch?recipientUserIds={playerA}&recipientUserIds={strangerId}" +
            $"&recipientUserIds={playerB}&recipientUserIds={coachId}&clubId={clubId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CanCreateBatchResponse>();
        body!.EligibleRecipientIds.Should().BeEquivalentTo([playerA, playerB]);
        await _factory.ClubsGrpcClient.Received(1).GetClubMemberIdsAsync(clubId);
        await _factory.ClubsGrpcClient.Received(1).CanGiveFeedbackInClubAsync(coachId, clubId);
    }

    [Test]
    public async Task CanCreateBatch_MoreThanTheCap_AnswersValidationError()
    {
        // Arrange
        var query = string.Join("&", Enumerable
            .Range(0, IFeedbackAuthorizationService.MaxRecipientsPerBatch + 1)
            .Select(_ => $"recipientUserIds={Guid.NewGuid()}"));
        SetAuth(Guid.NewGuid());

        // Act
        var response = await _client.GetAsync($"/v1/feedback/can-create/batch?{query}&clubId={Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("VALIDATION_ERROR").And.Contain("recipientUserIds");
    }

    [Test]
    public async Task CanCreateBatch_Anonymous_AnswersUnauthorized()
    {
        // Act
        var response = await _client.GetAsync(
            $"/v1/feedback/can-create/batch?recipientUserIds={Guid.NewGuid()}&clubId={Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<CanCreateResponse?> GetCanCreateAsync(string query)
    {
        var response = await _client.GetAsync($"/v1/feedback/can-create?{query}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CanCreateResponse>();
    }

    private static IReadOnlySet<Guid> Roster(params Guid[] userIds) => userIds.ToHashSet();

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
