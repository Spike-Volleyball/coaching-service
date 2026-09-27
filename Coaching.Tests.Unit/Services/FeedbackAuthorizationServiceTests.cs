using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shared.Enums;
using Shared.Exceptions;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class FeedbackAuthorizationServiceTests : UnitTestBase
{
    private IEventsGrpcClient _eventsClient = null!;
    private IClubsGrpcClient _clubsClient = null!;
    private ILogger<FeedbackAuthorizationService> _logger = null!;
    private FeedbackAuthorizationService _sut = null!;

    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid PlayerId = Guid.NewGuid();
    private static readonly Guid StrangerId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    public enum Caller { EventAdmin, ClubCoach, UnitCoach, Nobody }

    public enum Recipient { Participant, NonParticipant, Self }

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _eventsClient = Substitute.For<IEventsGrpcClient>();
        _clubsClient = Substitute.For<IClubsGrpcClient>();
        _logger = Substitute.For<ILogger<FeedbackAuthorizationService>>();
        _sut = new FeedbackAuthorizationService(
            _eventsClient, _clubsClient, new FeedbackScopeFlights(TimeProvider), _logger);
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedClubEvent_CoachInClub_ReturnsClubId()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Club", ClubId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId)
            .Returns(true);

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().Be(ClubId);
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedClubEvent_NotCoach_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Club", ClubId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId)
            .Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*coaches*club*");
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedClubEvent_CoachOfAnotherClub_ThrowsForbidden()
    {
        // Arrange
        var otherClubId = Guid.NewGuid();
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Club", ClubId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, otherClubId).Returns(true);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*coaches*club*");
        await _clubsClient.Received(1).CanGiveFeedbackInClubAsync(CoachId, ClubId);
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedClubEvent_EventAdminWithNoClubRole_ThrowsForbidden()
    {
        // Arrange — a club event is judged by the club role alone; organising the session does
        // not make somebody the club's coach.
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Club", ClubId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(true);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*coaches*club*");
        await _eventsClient.DidNotReceiveWithAnyArgs().IsEventAdminAsync(default, default);
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedNonClub_EventAdmin_ReturnsNull()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", "None", null));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId)
            .Returns(true);

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().BeNull();
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedNonClub_NotAdmin_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", "None", null));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId)
            .Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*organizers*admins*");
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedGroupContext_FallsBackToEventAdmin()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        var groupId = Guid.NewGuid();
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Group", groupId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId)
            .Returns(true);

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().BeNull();
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedTeamContext_FallsBackToEventAdmin()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        var teamId = Guid.NewGuid();
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", "Team", teamId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId)
            .Returns(true);

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().BeNull();
    }

    [Test]
    public async Task ValidateCreateAsync_WrongEventType_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("CasualPlay", "Club", ClubId));

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*CasualPlay*");
        await _eventsClient.DidNotReceiveWithAnyArgs().GetEventParticipantIdsAsync(default, default!);
    }

    [Test]
    public async Task ValidateCreateAsync_RecipientNotParticipant_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Club", ClubId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster());

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*recipient*not a participant*");
    }

    [Test]
    public async Task ValidateCreateAsync_EventNotFound_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId).Returns((EventContext?)null);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*Event not found*");
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedClubEvent_IgnoresRequestClubId()
    {
        // Arrange
        var differentClubId = Guid.NewGuid();
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            EventId = EventId,
            ClubId = differentClubId
        };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Club", ClubId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId)
            .Returns(true);

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().Be(ClubId);
        await _clubsClient.Received(1).CanGiveFeedbackInClubAsync(CoachId, ClubId);
        await _clubsClient.DidNotReceive().CanGiveFeedbackInClubAsync(CoachId, differentClubId);
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedTeamEvent_TeamCoach_IsAdmittedWithoutBeingEventAdmin()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Team", TeamId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(false);
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(true);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedGroupEvent_GroupCoach_IsAdmittedWithoutBeingEventAdmin()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Evaluation", "Group", GroupId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(false);
        _clubsClient.ResolveClubIdAsync(ContextType.Group, GroupId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Group, GroupId).Returns(true);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedTeamEvent_CoachOfTheOwningClub_IsAdmitted()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", "Team", TeamId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(false);
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(false);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [TestCase(ContextType.Team)]
    [TestCase(ContextType.Group)]
    public async Task ValidateCreateAsync_EventLinkedUnitEvent_CoachOfAnotherClub_ThrowsForbidden(ContextType unitType)
    {
        // Arrange
        var unitId = Guid.NewGuid();
        var otherClubId = Guid.NewGuid();
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", unitType.ToString(), unitId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(false);
        _clubsClient.ResolveClubIdAsync(unitType, unitId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, unitType, unitId).Returns(false);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, otherClubId).Returns(true);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*organizers*admins*");
        await _clubsClient.Received(1).CanGiveFeedbackInClubAsync(CoachId, ClubId);
    }

    [TestCase(ContextType.Team)]
    [TestCase(ContextType.Group)]
    public async Task ValidateCreateAsync_EventLinkedUnitEvent_EventAdminWithNoRoleAnywhere_IsAdmitted(ContextType unitType)
    {
        // Arrange
        var unitId = Guid.NewGuid();
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", unitType.ToString(), unitId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(true);
        _clubsClient.ResolveClubIdAsync(unitType, unitId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, unitType, unitId).Returns(false);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().BeNull();
    }

    [Test]
    public async Task ValidateCreateAsync_EventLinkedTeamEvent_NeitherUnitCoachNorEventAdmin_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", "Team", TeamId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(false);
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(false);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithClub_CoachAndMember_ReturnsClubId()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, ClubId = ClubId };
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);
        _clubsClient.GetClubMemberIdsAsync(ClubId).Returns(Roster(PlayerId));

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().Be(ClubId);
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithClub_NotCoach_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, ClubId = ClubId };
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*coaches*standalone*");
        await _clubsClient.DidNotReceiveWithAnyArgs().GetClubMemberIdsAsync(default);
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithClub_RecipientNotMember_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, ClubId = ClubId };
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);
        _clubsClient.GetClubMemberIdsAsync(ClubId).Returns(Roster());

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*recipient*not a member*");
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithTeam_TeamCoachAndTeamMember_ReturnsOwningClubId()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            ContextType = ContextType.Team,
            ContextId = TeamId
        };
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(true);
        _clubsClient.GetUnitMemberIdsAsync(ContextType.Team, TeamId).Returns(Roster(PlayerId));

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().Be(ClubId);
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithGroup_GroupCoachAndGroupMember_ReturnsOwningClubId()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            ContextType = ContextType.Group,
            ContextId = GroupId
        };
        _clubsClient.ResolveClubIdAsync(ContextType.Group, GroupId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Group, GroupId).Returns(true);
        _clubsClient.GetUnitMemberIdsAsync(ContextType.Group, GroupId).Returns(Roster(PlayerId));

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().Be(ClubId);
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithTeam_CoachOfTheOwningClub_IsAdmitted()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            ContextType = ContextType.Team,
            ContextId = TeamId
        };
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(false);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);
        _clubsClient.GetUnitMemberIdsAsync(ContextType.Team, TeamId).Returns(Roster(PlayerId));

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().Be(ClubId);
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithTeam_PlayerOfThatTeam_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            ContextType = ContextType.Team,
            ContextId = TeamId
        };
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(false);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*coaches*team or group*");
        await _clubsClient.DidNotReceiveWithAnyArgs().GetUnitMemberIdsAsync(default, default);
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithTeam_RecipientNotInThatTeam_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            ContextType = ContextType.Team,
            ContextId = TeamId
        };
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(true);
        _clubsClient.GetUnitMemberIdsAsync(ContextType.Team, TeamId).Returns(Roster());

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*recipient*not a member*team or group*");
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithTeam_UnknownTeam_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            ContextType = ContextType.Team,
            ContextId = TeamId
        };
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns((Guid?)null);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(false);

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        await _clubsClient.DidNotReceive().CanGiveFeedbackInClubAsync(CoachId, Arg.Any<Guid>());
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithTeam_IgnoresRequestClubId()
    {
        // Arrange
        var spoofedClubId = Guid.NewGuid();
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            ClubId = spoofedClubId,
            ContextType = ContextType.Team,
            ContextId = TeamId
        };
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(true);
        _clubsClient.GetUnitMemberIdsAsync(ContextType.Team, TeamId).Returns(Roster(PlayerId));

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().Be(ClubId);
        await _clubsClient.DidNotReceive().CanGiveFeedbackInClubAsync(CoachId, spoofedClubId);
    }

    [Test]
    public async Task ValidateCreateAsync_StandaloneWithClubContextType_UsesTheClubRule()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = PlayerId,
            ClubId = ClubId,
            ContextType = ContextType.Club,
            ContextId = ClubId
        };
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);
        _clubsClient.GetClubMemberIdsAsync(ClubId).Returns(Roster(PlayerId));

        // Act
        var resolvedClubId = await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        resolvedClubId.Should().Be(ClubId);
        await _clubsClient.DidNotReceive()
            .CanGiveFeedbackInUnitAsync(CoachId, Arg.Any<ContextType>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task ValidateCreateAsync_NoEventNoClub_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId };

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*eventId or clubId*");
    }

    [Test]
    public async Task ValidateCreateAsync_SelfFeedback_ThrowsForbidden()
    {
        // Arrange
        var request = new CreateFeedbackDto { RecipientUserId = CoachId, ClubId = ClubId };

        // Act
        var act = () => _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>()
            .WithMessage("*yourself*");
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_Authorized_ListsTheRecipient()
    {
        // Arrange
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);
        _clubsClient.GetClubMemberIdsAsync(ClubId).Returns(Roster(PlayerId));

        // Act
        var eligible = await _sut.GetEligibleRecipientsAsync(ClubScope(), [PlayerId], CoachId);

        // Assert
        eligible.Should().Equal(PlayerId);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_Unauthorized_ListsNobody()
    {
        // Arrange
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var eligible = await _sut.GetEligibleRecipientsAsync(ClubScope(), [PlayerId], CoachId);

        // Assert
        eligible.Should().BeEmpty();
    }

    /// <summary>
    /// The batch is the single-recipient path, so the two must never disagree: every cell of
    /// caller × recipient on a team event is answered three ways — the throwing create path, a
    /// batch of one, and a batch of the whole roster — and all three must match the rule.
    /// </summary>
    [Test, Combinatorial]
    public async Task GetEligibleRecipientsAsync_TeamEvent_AgreesWithValidateCreateForEveryCallerAndRecipient(
        [Values] Caller caller, [Values] Recipient recipient)
    {
        // Arrange
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("TrainingSession", "Team", TeamId));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId, CoachId));
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(caller == Caller.EventAdmin);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(caller == Caller.ClubCoach);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(caller == Caller.UnitCoach);
        var recipientId = recipient switch
        {
            Recipient.Participant => PlayerId,
            Recipient.NonParticipant => StrangerId,
            _ => CoachId
        };
        var expected = caller != Caller.Nobody && recipient == Recipient.Participant;

        // Act
        var single = await CreateIsAllowedAsync(
            new CreateFeedbackDto { RecipientUserId = recipientId, EventId = EventId });
        var batchOfOne = await _sut.GetEligibleRecipientsAsync(EventScope(), [recipientId], CoachId);
        var wholeRoster = await _sut.GetEligibleRecipientsAsync(
            EventScope(), [PlayerId, StrangerId, CoachId], CoachId);

        // Assert
        single.Should().Be(expected);
        batchOfOne.Contains(recipientId).Should().Be(expected);
        wholeRoster.Contains(recipientId).Should().Be(expected);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_TwentyRecipientsOnAnEvent_FetchesTheRosterAndTheCallersStandingOnce()
    {
        // Arrange
        var participants = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var strangers = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", "None", null));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(participants.ToArray()));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(true);

        // Act
        var eligible = await _sut.GetEligibleRecipientsAsync(
            EventScope(), participants.Concat(strangers).ToList(), CoachId);

        // Assert
        eligible.Should().BeEquivalentTo(participants);
        await _eventsClient.Received(1).GetEventContextAsync(EventId);
        await _eventsClient.Received(1).GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>());
        await _eventsClient.Received(1).IsEventAdminAsync(EventId, CoachId);
    }

    [Test]
    public async Task ValidateCreateAsync_OnAnEvent_AsksTheRosterAboutTheRecipient()
    {
        // Arrange — naming the recipient lets a cached roster that predates their invitation be re-read.
        var request = new CreateFeedbackDto { RecipientUserId = PlayerId, EventId = EventId };
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", "None", null));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(true);

        // Act
        await _sut.ValidateCreateAsync(request, CoachId);

        // Assert
        await _eventsClient.Received(1).GetEventParticipantIdsAsync(
            EventId, Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { PlayerId })));
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_OnAnEvent_AsksTheRosterAboutEveryRecipient()
    {
        // Arrange
        var stranger = Guid.NewGuid();
        _eventsClient.GetEventContextAsync(EventId)
            .Returns(new EventContext("Match", "None", null));
        _eventsClient.GetEventParticipantIdsAsync(EventId, Arg.Any<IReadOnlyCollection<Guid>>()).Returns(Roster(PlayerId));
        _eventsClient.IsEventAdminAsync(EventId, CoachId).Returns(true);

        // Act
        await _sut.GetEligibleRecipientsAsync(EventScope(), [PlayerId, stranger], CoachId);

        // Assert
        await _eventsClient.Received(1).GetEventParticipantIdsAsync(
            EventId, Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { PlayerId, stranger })));
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_TwentyRecipientsInATeam_FetchesTheRosterAndTheCoachStandingOnce()
    {
        // Arrange
        var members = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var strangers = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(true);
        _clubsClient.GetUnitMemberIdsAsync(ContextType.Team, TeamId).Returns(Roster(members.ToArray()));

        // Act
        var eligible = await _sut.GetEligibleRecipientsAsync(
            TeamScope(), members.Concat(strangers).ToList(), CoachId);

        // Assert
        eligible.Should().BeEquivalentTo(members);
        await _clubsClient.Received(1).CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId);
        await _clubsClient.Received(1).ResolveClubIdAsync(ContextType.Team, TeamId);
        await _clubsClient.Received(1).GetUnitMemberIdsAsync(ContextType.Team, TeamId);
        await _clubsClient.DidNotReceiveWithAnyArgs().CanGiveFeedbackInClubAsync(default, default);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_CallerWhoMayNotGive_NeverFetchesTheRoster()
    {
        // Arrange
        _clubsClient.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubsClient.CanGiveFeedbackInUnitAsync(CoachId, ContextType.Team, TeamId).Returns(false);
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(false);

        // Act
        var eligible = await _sut.GetEligibleRecipientsAsync(TeamScope(), [PlayerId, StrangerId], CoachId);

        // Assert
        eligible.Should().BeEmpty();
        await _clubsClient.DidNotReceiveWithAnyArgs().GetUnitMemberIdsAsync(default, default);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_DuplicateRecipient_AnswersOnce()
    {
        // Arrange
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);
        _clubsClient.GetClubMemberIdsAsync(ClubId).Returns(Roster(PlayerId));

        // Act
        var eligible = await _sut.GetEligibleRecipientsAsync(ClubScope(), [PlayerId, PlayerId], CoachId);

        // Assert
        eligible.Should().Equal(PlayerId);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_NoRecipients_AsksNothingAndListsNobody()
    {
        // Act
        var eligible = await _sut.GetEligibleRecipientsAsync(EventScope(), [], CoachId);

        // Assert
        eligible.Should().BeEmpty();
        await _eventsClient.DidNotReceiveWithAnyArgs().GetEventContextAsync(default);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_MoreThanTheCap_ThrowsValidationBeforeAskingAnything()
    {
        // Arrange
        var recipients = Enumerable.Range(0, IFeedbackAuthorizationService.MaxRecipientsPerBatch + 1)
            .Select(_ => Guid.NewGuid())
            .ToList();

        // Act
        var act = () => _sut.GetEligibleRecipientsAsync(EventScope(), recipients, CoachId);

        // Assert
        var thrown = await act.Should().ThrowAsync<ValidationException>();
        thrown.Which.FieldErrors.Should().ContainSingle(e => e.Field == "recipientUserIds");
        await _eventsClient.DidNotReceiveWithAnyArgs().GetEventContextAsync(default);
    }

    [Test]
    public async Task GetEligibleRecipientsAsync_ExactlyTheCap_IsAnswered()
    {
        // Arrange
        var recipients = Enumerable.Range(0, IFeedbackAuthorizationService.MaxRecipientsPerBatch)
            .Select(_ => Guid.NewGuid())
            .ToList();
        _clubsClient.CanGiveFeedbackInClubAsync(CoachId, ClubId).Returns(true);
        _clubsClient.GetClubMemberIdsAsync(ClubId).Returns(Roster(recipients.ToArray()));

        // Act
        var eligible = await _sut.GetEligibleRecipientsAsync(ClubScope(), recipients, CoachId);

        // Assert
        eligible.Should().BeEquivalentTo(recipients);
    }

    private async Task<bool> CreateIsAllowedAsync(CreateFeedbackDto request)
    {
        try
        {
            await _sut.ValidateCreateAsync(request, CoachId);
            return true;
        }
        catch (ForbiddenException)
        {
            return false;
        }
    }

    private static FeedbackScope EventScope() => new(EventId, null, null, null);

    private static FeedbackScope TeamScope() => new(null, null, ContextType.Team, TeamId);

    private static FeedbackScope ClubScope() => new(null, ClubId, null, null);

    private static IReadOnlySet<Guid> Roster(params Guid[] userIds) => userIds.ToHashSet();
}
