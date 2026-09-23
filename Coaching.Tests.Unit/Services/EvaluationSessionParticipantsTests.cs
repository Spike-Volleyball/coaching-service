using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using FluentAssertions;
using MockQueryable;
using NSubstitute;
using Shared.Exceptions;
using Shared.Services.Analytics;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Who is evaluated in a session. The start builds an evaluation and a score for every player, so
/// the roster is settled before it; a player comes from the event the session is run at, or the
/// club when it has none; and a player taken out of the session is taken out of their group.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationSessionParticipantsTests : UnitTestBase
{
    private IEvaluationSessionRepository _sessions = null!;
    private IEvaluationParticipantRepository _participants = null!;
    private IEventsGrpcClient _events = null!;
    private IClubsGrpcClient _clubs = null!;
    private EvaluationSessionService _sut = null!;

    private readonly Guid _clubId = Guid.NewGuid();
    private readonly Guid _eventId = Guid.NewGuid();
    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _onTheEvent = Guid.NewGuid();
    private readonly Guid _alsoOnTheEvent = Guid.NewGuid();
    private readonly Guid _stranger = Guid.NewGuid();

    private List<EvaluationParticipant> _rows = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _sessions = Substitute.For<IEvaluationSessionRepository>();
        _participants = Substitute.For<IEvaluationParticipantRepository>();
        _events = Substitute.For<IEventsGrpcClient>();
        _clubs = Substitute.For<IClubsGrpcClient>();
        _events.GetEventParticipantIdsAsync(_eventId).Returns(new HashSet<Guid> { _onTheEvent, _alsoOnTheEvent });
        _clubs.GetClubMemberIdsAsync(_clubId).Returns(new HashSet<Guid> { _onTheEvent });

        _rows = [];
        _participants.Query().Returns(_ => _rows.BuildMock());

        var mapper = Substitute.For<IMapper>();
        mapper.Map<EvaluationSessionDto>(Arg.Any<EvaluationSession>())
            .Returns(call => new EvaluationSessionDto { Id = call.Arg<EvaluationSession>().Id });

        _sut = new EvaluationSessionService(
            _sessions,
            _participants,
            Substitute.For<IEvaluationPlanRepository>(),
            _events,
            _clubs,
            Substitute.For<IAnalyticsCapture>(),
            Substitute.For<IEvaluationAccess>(),
            mapper);
    }

    private EvaluationSession Session(EvaluationSessionStatus status = EvaluationSessionStatus.Draft, bool atAnEvent = true)
    {
        var session = new EvaluationSession
        {
            ClubId = _clubId,
            EventId = atAnEvent ? _eventId : null,
            CoachUserId = _coachId,
            Title = "Autumn trials",
            Status = status,
        };
        _sessions.GetByIdWithParticipantsAsync(session.Id).Returns(session);
        return session;
    }

    private static AddParticipantsDto Adding(params Guid[] players) =>
        new() { PlayerIds = players.ToList(), Source = ParticipantSource.EventParticipant };

    [Test]
    public async Task AddParticipantsAsync_WithPlayersOfTheEvent_AddsEachOnce()
    {
        // Arrange
        var session = Session();

        // Act
        await _sut.AddParticipantsAsync(session.Id, Adding(_onTheEvent, _alsoOnTheEvent, _onTheEvent), _coachId);

        // Assert
        _participants.Received(2).Add(Arg.Any<EvaluationParticipant>());
        _participants.Received(1).Add(Arg.Is<EvaluationParticipant>(p => p.PlayerId == _onTheEvent && p.EvaluationSessionId == session.Id));
        await _participants.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task AddParticipantsAsync_WithSomeoneNotOnTheEvent_ThrowsNamingThemAndAddsNobody()
    {
        // Arrange
        var session = Session();

        // Act
        var act = () => _sut.AddParticipantsAsync(session.Id, Adding(_onTheEvent, _stranger), _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("playerIds[1]");
        _participants.DidNotReceive().Add(Arg.Any<EvaluationParticipant>());
        await _participants.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task AddParticipantsAsync_ToASessionWithNoEvent_AsksTheClubsRoster()
    {
        // Arrange — on the event but not in the club.
        var session = Session(atAnEvent: false);

        // Act
        var act = () => _sut.AddParticipantsAsync(session.Id, Adding(_alsoOnTheEvent), _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("playerIds[0]");
        await _events.DidNotReceive().GetEventParticipantIdsAsync(Arg.Any<Guid>());
    }

    [TestCase(EvaluationSessionStatus.Running)]
    [TestCase(EvaluationSessionStatus.Paused)]
    [TestCase(EvaluationSessionStatus.Completed)]
    public async Task AddParticipantsAsync_OnceStarted_ThrowsAndAddsNobody(EvaluationSessionStatus status)
    {
        // Arrange — the start built an evaluation and scores for everyone then in it, not for them.
        var session = Session(status);

        // Act
        var act = () => _sut.AddParticipantsAsync(session.Id, Adding(_onTheEvent), _coachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _participants.DidNotReceive().Add(Arg.Any<EvaluationParticipant>());
    }

    [Test]
    public async Task AddParticipantsAsync_APlayerRemovedEarlier_BringsTheirRowBack()
    {
        // Arrange — (session, player) is uniquely indexed and removal only soft-deletes.
        var session = Session();
        var removed = new EvaluationParticipant { EvaluationSessionId = session.Id, PlayerId = _onTheEvent, IsDeleted = true };
        _rows.Add(removed);

        // Act
        await _sut.AddParticipantsAsync(session.Id, Adding(_onTheEvent), _coachId);

        // Assert
        removed.IsDeleted.Should().BeFalse();
        _participants.DidNotReceive().Add(Arg.Any<EvaluationParticipant>());
        await _participants.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task RemoveParticipantAsync_TakesThePlayerOutOfTheirGroupToo()
    {
        // Arrange
        var session = Session();
        var participant = new EvaluationParticipant { EvaluationSessionId = session.Id, PlayerId = _onTheEvent };
        session.Participants.Add(participant);
        var group = new EvaluationGroup { SessionId = session.Id, Name = "Court one" };
        var seat = new EvaluationGroupPlayer { GroupId = group.Id, PlayerId = _onTheEvent };
        var neighbour = new EvaluationGroupPlayer { GroupId = group.Id, PlayerId = _alsoOnTheEvent };
        group.Players.Add(seat);
        group.Players.Add(neighbour);
        session.Groups.Add(group);

        // Act
        await _sut.RemoveParticipantAsync(session.Id, participant.Id, _coachId);

        // Assert
        participant.IsDeleted.Should().BeTrue();
        seat.IsDeleted.Should().BeTrue();
        neighbour.IsDeleted.Should().BeFalse();
        await _participants.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task RemoveParticipantAsync_OnceStarted_ThrowsAndKeepsThem()
    {
        // Arrange
        var session = Session(EvaluationSessionStatus.Running);
        var participant = new EvaluationParticipant { EvaluationSessionId = session.Id, PlayerId = _onTheEvent };
        session.Participants.Add(participant);

        // Act
        var act = () => _sut.RemoveParticipantAsync(session.Id, participant.Id, _coachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        participant.IsDeleted.Should().BeFalse();
    }
}
