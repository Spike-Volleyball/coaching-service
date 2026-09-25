using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shared.Enums;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Old clients ask can-create once per roster member, all at once. These pin what the requests
/// of one such burst share — one resolution of the scope for the caller — and what they never
/// share: another caller's standing, a failure, or a verdict that turns someone away as a
/// stranger to the roster.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FeedbackAuthorizationSharingTests : UnitTestBase
{
    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid OtherCoachId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly FeedbackScope ClubEvent = new(EventId, null, null, null);

    private IEventsGrpcClient _eventsClient = null!;
    private IClubsGrpcClient _clubsClient = null!;
    private FeedbackScopeFlights _flights = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _eventsClient = Substitute.For<IEventsGrpcClient>();
        _clubsClient = Substitute.For<IClubsGrpcClient>();
        _flights = new FeedbackScopeFlights(TimeProvider);

        _eventsClient.GetEventContextAsync(EventId).Returns(new EventContext("TrainingSession", "Club", ClubId));
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);
    }

    /// <summary>One request's service: scoped in the app, sharing only the singleton flights.</summary>
    private FeedbackAuthorizationService NewRequest() =>
        new(_eventsClient, _clubsClient, _flights, Substitute.For<ILogger<FeedbackAuthorizationService>>());

    [Test]
    public async Task GetEligibleRecipientsAsync_TwentyTwoConcurrentAsksFromOneCaller_ResolveTheScopeOnce()
    {
        // Arrange — the roster read is held open so every request is in flight together.
        var players = Enumerable.Range(0, 22).Select(_ => Guid.NewGuid()).ToArray();
        var roster = new TaskCompletionSource<IReadOnlySet<Guid>>();
        _eventsClient.GetEventParticipantIdsAsync(EventId).Returns(roster.Task);

        // Act
        var asks = players
            .Select(player => NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId))
            .ToList();
        roster.SetResult(players.ToHashSet());
        var answers = await Task.WhenAll(asks);

        // Assert
        for (var i = 0; i < players.Length; i++)
            answers[i].Should().Equal(players[i]);
        await _eventsClient.Received(1).GetEventContextAsync(EventId);
        await _eventsClient.Received(1).GetEventParticipantIdsAsync(EventId);
        await _clubsClient.Received(1).CanGiveFeedbackInClubAsync(CoachId, ClubId);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_TwoCallersAskingAtOnce_EachIsJudgedOnTheirOwnStanding()
    {
        // Arrange
        var player = Guid.NewGuid();
        var roster = new TaskCompletionSource<IReadOnlySet<Guid>>();
        _eventsClient.GetEventParticipantIdsAsync(EventId).Returns(roster.Task);
        _clubsClient.CanGiveFeedbackInClubAsync(OtherCoachId, ClubId).Returns(false);

        // Act
        var coachAsks = NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);
        var otherAsks = NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], OtherCoachId);
        roster.SetResult(new HashSet<Guid> { player });

        // Assert
        (await coachAsks).Should().Equal(player);
        (await otherAsks).Should().BeEmpty();
        await _clubsClient.Received(1).CanGiveFeedbackInClubAsync(CoachId, ClubId);
        await _clubsClient.Received(1).CanGiveFeedbackInClubAsync(OtherCoachId, ClubId);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_AskedAgainWithinTheWindow_ReusesTheLandedResolution()
    {
        // Arrange
        var player = Guid.NewGuid();
        _eventsClient.GetEventParticipantIdsAsync(EventId).Returns(new HashSet<Guid> { player });
        await NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);
        TimeProvider.Advance(FeedbackScopeFlights.AnswersFor - TimeSpan.FromSeconds(1));

        // Act
        var eligible = await NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);

        // Assert
        eligible.Should().Equal(player);
        await _clubsClient.Received(1).CanGiveFeedbackInClubAsync(CoachId, ClubId);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_AskedAgainAfterTheWindow_ResolvesAgain()
    {
        // Arrange — the caller lost their club role in between.
        var player = Guid.NewGuid();
        _eventsClient.GetEventParticipantIdsAsync(EventId).Returns(new HashSet<Guid> { player });
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true, false);
        await NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);
        TimeProvider.Advance(FeedbackScopeFlights.AnswersFor);

        // Act
        var eligible = await NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);

        // Assert
        eligible.Should().BeEmpty();
        await _clubsClient.Received(2).CanGiveFeedbackInClubAsync(CoachId, ClubId);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_SomeoneOffTheSharedRoster_IsJudgedOnAResolutionOfTheirOwn()
    {
        // Arrange — the roster the burst resolved is older than the newcomer's invitation.
        var player = Guid.NewGuid();
        var newcomer = Guid.NewGuid();
        _eventsClient.GetEventParticipantIdsAsync(EventId).Returns(
            new HashSet<Guid> { player }, new HashSet<Guid> { player, newcomer });
        await NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);

        // Act
        var eligible = await NewRequest().GetEligibleRecipientsAsync(ClubEvent, [newcomer], CoachId);

        // Assert
        eligible.Should().Equal(newcomer);
        await _eventsClient.Received(2).GetEventParticipantIdsAsync(EventId);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_TheSharedResolutionFailed_TheNextAskResolvesAgain()
    {
        // Arrange
        var player = Guid.NewGuid();
        _eventsClient.GetEventParticipantIdsAsync(EventId).Returns(
            _ => throw new RpcException(new Status(StatusCode.Unavailable, "events-service is down")),
            _ => new HashSet<Guid> { player });
        var failed = () => NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);
        await failed.Should().ThrowAsync<RpcException>();

        // Act
        var eligible = await NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);

        // Assert
        eligible.Should().Equal(player);
        await _eventsClient.Received(2).GetEventParticipantIdsAsync(EventId);
    }

    [Test]
    public async Task ValidateCreateAsync_RightAfterACanCreateAsk_ResolvesItsOwnScope()
    {
        // Arrange — a write authorises on facts of its own, never on a burst's.
        var player = Guid.NewGuid();
        _eventsClient.GetEventParticipantIdsAsync(EventId).Returns(new HashSet<Guid> { player });
        await NewRequest().GetEligibleRecipientsAsync(ClubEvent, [player], CoachId);

        // Act
        await NewRequest().ValidateCreateAsync(
            new CreateFeedbackDto { RecipientUserId = player, EventId = EventId }, CoachId);

        // Assert
        await _clubsClient.Received(2).CanGiveFeedbackInClubAsync(CoachId, ClubId);
    }
}
