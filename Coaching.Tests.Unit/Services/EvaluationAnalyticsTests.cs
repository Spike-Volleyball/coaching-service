using AutoMapper;
using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using Coaching.Tests.Unit.Analytics;
using FluentAssertions;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;
using Shared.Services.Analytics;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// SPI-6282: journey 12's server-side events. One player evaluation exists in production, and
/// without these there is no way to tell whether coaches never reach the screen or reach it and
/// give up part-way through scoring.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationAnalyticsTests
{
    private IEvaluationSessionRepository _sessionRepository = null!;
    private IEvaluationParticipantRepository _participantRepository = null!;
    private IEvaluationPlanRepository _planRepository = null!;
    private IEvaluationGroupRepository _groupRepository = null!;
    private IPlayerEvaluationRepository _evaluationRepository = null!;
    private IPlayerExerciseScoreRepository _exerciseScoreRepository = null!;
    private IClubsGrpcClient _clubsGrpcClient = null!;
    private IScoreCalculationService _scoreCalculation = null!;
    private IAnalyticsCapture _analytics = null!;

    private EvaluationSessionService _sessions = null!;
    private EvaluationSessionLifecycleService _lifecycle = null!;
    private EvaluationScoringService _scoring = null!;
    private PlayerEvaluationService _evaluations = null!;

    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();
    private static readonly Guid ExerciseId = Guid.NewGuid();
    private static readonly Guid MetricId = Guid.NewGuid();
    private static readonly Guid PlayerId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _sessionRepository = Substitute.For<IEvaluationSessionRepository>();
        _participantRepository = Substitute.For<IEvaluationParticipantRepository>();
        _planRepository = Substitute.For<IEvaluationPlanRepository>();
        _groupRepository = Substitute.For<IEvaluationGroupRepository>();
        _evaluationRepository = Substitute.For<IPlayerEvaluationRepository>();
        _exerciseScoreRepository = Substitute.For<IPlayerExerciseScoreRepository>();
        _clubsGrpcClient = Substitute.For<IClubsGrpcClient>();
        _scoreCalculation = Substitute.For<IScoreCalculationService>();
        _analytics = Substitute.For<IAnalyticsCapture>();

        var noSkillPoints = Enum.GetValues<VolleyballSkill>().ToDictionary(skill => skill, _ => 0m);
        _scoreCalculation.CalculateSkillPoints(Arg.Any<PlayerEvaluation>(), Arg.Any<EvaluationPlan>())
            .Returns(noSkillPoints);
        _scoreCalculation.CalculateMaxSkillPoints(Arg.Any<EvaluationPlan>()).Returns(noSkillPoints);

        // The reads that follow every write map through AutoMapper; the events are asserted from
        // the capture, not from these.
        var mapper = Substitute.For<IMapper>();
        mapper.Map<EvaluationSessionDto>(Arg.Any<EvaluationSession>())
            .Returns(call => new EvaluationSessionDto { Id = call.Arg<EvaluationSession>().Id });
        mapper.Map<PlayerEvaluationDto>(Arg.Any<PlayerEvaluation>())
            .Returns(call => new PlayerEvaluationDto { Id = call.Arg<PlayerEvaluation>().Id });

        var access = Substitute.For<IEvaluationAccess>();
        access.MayEvaluateInClubAsync(ClubId, CoachId).Returns(true);
        _sessions = new EvaluationSessionService(
            _sessionRepository, _participantRepository, _planRepository,
            Substitute.For<IEventsGrpcClient>(), _clubsGrpcClient, _analytics, access, mapper);

        _lifecycle = new EvaluationSessionLifecycleService(
            _sessionRepository,
            _groupRepository,
            _participantRepository,
            _exerciseScoreRepository,
            _evaluationRepository,
            _planRepository,
            Substitute.For<IRepository<PlayerMetricScore>>(),
            Substitute.For<IRepository<PlayerSkillScore>>(),
            _clubsGrpcClient,
            _scoreCalculation,
            _analytics,
            Substitute.For<IEvaluationAccess>(),
            mapper);

        _scoring = new EvaluationScoringService(
            _sessionRepository,
            _exerciseScoreRepository,
            _groupRepository,
            _planRepository,
            _evaluationRepository,
            Substitute.For<IRepository<PlayerMetricScore>>(),
            _scoreCalculation,
            _analytics,
            Substitute.For<IEvaluationAccess>(),
            mapper);

        _evaluations = new PlayerEvaluationService(
            _evaluationRepository,
            Substitute.For<IRepository<PlayerMetricScore>>(),
            Substitute.For<IRepository<PlayerSkillScore>>(),
            _planRepository,
            _sessionRepository,
            _participantRepository,
            _clubsGrpcClient,
            _scoreCalculation,
            _analytics,
            mapper);
    }

    [Test]
    public async Task CreateAsync_WithASessionThatSaves_CapturesEvaluationSessionCreatedOnce()
    {
        // Arrange
        EvaluationSession? persisted = null;
        _sessionRepository.When(repository => repository.Add(Arg.Any<EvaluationSession>()))
            .Do(call => persisted = call.Arg<EvaluationSession>());
        _sessionRepository.GetByIdWithParticipantsAsync(Arg.Any<Guid>()).Returns(_ => persisted);
        _planRepository.GetByIdAsync(PlanId).Returns(Plan());

        // Act
        await _sessions.CreateAsync(new CreateEvaluationSessionDto
        {
            ClubId = ClubId,
            EventId = EventId,
            EvaluationPlanId = PlanId,
            Title = "Autumn trials"
        }, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.EvaluationSessionCreated, CoachId);
        properties["session_id"].Should().Be(persisted!.Id);
        properties["club_id"].Should().Be(ClubId);
        properties["event_id"].Should().Be(EventId);
        properties["has_plan"].Should().Be(true);
    }

    [Test]
    public async Task CreateAsync_WithNoTitle_CapturesNothing()
    {
        // Act
        var act = () => _sessions.CreateAsync(
            new CreateEvaluationSessionDto { ClubId = ClubId, Title = "  " }, CoachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task StartSessionAsync_WhenTheSessionStarts_CapturesTheRosterAndTheExercises()
    {
        // Arrange
        var session = Session(EvaluationSessionStatus.Draft);
        StubSession(session);
        var participants = TwoParticipants();
        StubParticipants(participants);
        StubGroupHolding(participants);
        _planRepository.GetByIdWithItemsAsync(PlanId).Returns(Plan());
        _evaluationRepository.GetByParticipantIdAsync(Arg.Any<Guid>()).Returns((PlayerEvaluation?)null);
        _exerciseScoreRepository.GetBySessionPlayerExerciseAsync(SessionId, Arg.Any<Guid>(), ExerciseId)
            .Returns((PlayerExerciseScore?)null);

        // Act
        await _lifecycle.StartSessionAsync(SessionId, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.EvaluationSessionStarted, CoachId);
        properties["session_id"].Should().Be(SessionId);
        properties["participant_count"].Should().Be(2);
        properties["exercise_count"].Should().Be(1);
    }

    [Test]
    public async Task StartSessionAsync_WithNobodyToEvaluate_CapturesNothing()
    {
        // Arrange
        StubSession(Session(EvaluationSessionStatus.Draft));
        _planRepository.GetByIdWithItemsAsync(PlanId).Returns(Plan());
        StubParticipants([]);

        // Act
        var act = () => _lifecycle.StartSessionAsync(SessionId, CoachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task CompleteSessionAsync_WhenTheResultsAreCalculated_CapturesWhatWasScored()
    {
        // Arrange
        var session = Session(EvaluationSessionStatus.Running);
        session.StartedAt = DateTime.UtcNow.AddMinutes(-30);
        StubSession(session);
        var participants = TwoParticipants();
        StubParticipants(participants);
        _planRepository.GetByIdWithItemsAsync(PlanId).Returns(Plan());
        _exerciseScoreRepository.GetBySessionIdAsync(SessionId).Returns(
        [
            ScoredCell(participants[0].PlayerId, EvaluationScoreStatus.Scored),
            ScoredCell(participants[1].PlayerId, EvaluationScoreStatus.Pending)
        ]);
        foreach (var participant in participants)
            _evaluationRepository.GetByParticipantIdAsync(participant.Id).Returns(Evaluation(participant));
        _evaluationRepository.GetByIdWithScoresAsync(Arg.Any<Guid>())
            .Returns(call => Evaluation(participants[0], call.Arg<Guid>()));

        // Act
        await _lifecycle.CompleteSessionAsync(SessionId, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.EvaluationSessionCompleted, CoachId);
        properties["session_id"].Should().Be(SessionId);
        properties["evaluated_count"].Should().Be(2);
        properties["scored_count"].Should().Be(1);
        // CompletedAt is stamped from the system clock by the service, so the elapsed time is
        // asserted as a window rather than an exact second.
        ((int)properties["duration_seconds"]!).Should().BeInRange(1795, 1810);
    }

    [Test]
    public async Task CompleteSessionAsync_WhenTheSessionNeverStarted_CapturesNothing()
    {
        // Arrange
        StubSession(Session(EvaluationSessionStatus.Draft));

        // Act
        var act = () => _lifecycle.CompleteSessionAsync(SessionId, CoachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task SubmitExerciseScoresAsync_WithOnePlayersAnswer_CapturesOneEventForTheSubmission()
    {
        // Arrange
        var session = Session(EvaluationSessionStatus.Running);
        var participant = Participant(PlayerId);
        session.Participants.Add(participant);
        StubSession(session);
        _groupRepository.GetBySessionIdAsync(SessionId).Returns([]);
        var evaluation = Evaluation(participant);
        _evaluationRepository.GetByParticipantIdAsync(participant.Id).Returns(evaluation);
        _planRepository.GetByIdWithItemsAsync(PlanId).Returns(Plan());
        _exerciseScoreRepository.GetBySessionPlayerExerciseAsync(SessionId, PlayerId, ExerciseId)
            .Returns((PlayerExerciseScore?)null, ScoredCell(PlayerId, EvaluationScoreStatus.Scored));

        var request = new SubmitExerciseScoresDto
        {
            PlayerId = PlayerId,
            ExerciseId = ExerciseId,
            Scores = [new MetricScoreValueDto { MetricId = MetricId, Value = 8 }]
        };

        // Act
        await _scoring.SubmitExerciseScoresAsync(SessionId, request, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PlayerEvaluationScored, CoachId);
        properties["session_id"].Should().Be(SessionId);
        properties["evaluation_id"].Should().Be(evaluation.Id);
        properties["metric_count"].Should().Be(1);
    }

    [Test]
    public async Task SubmitExerciseScoresAsync_WhenTheSessionIsNotRunning_CapturesNothing()
    {
        // Arrange
        StubSession(Session(EvaluationSessionStatus.Draft));

        // Act
        var act = () => _scoring.SubmitExerciseScoresAsync(
            SessionId, new SubmitExerciseScoresDto { PlayerId = PlayerId, ExerciseId = ExerciseId }, CoachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task RecordMetricScoresAsync_WithASubmission_CapturesOneEventForTheWholeSubmission()
    {
        // Arrange
        var participant = Participant(PlayerId);
        var evaluation = Evaluation(participant);
        _evaluationRepository.GetByIdWithScoresAsync(evaluation.Id).Returns(evaluation);
        _sessionRepository.GetByIdAsync(SessionId).Returns(Session(EvaluationSessionStatus.Running));
        _planRepository.GetByIdWithItemsAsync(PlanId).Returns(Plan());

        var request = new RecordMetricScoresDto
        {
            Scores = [new RecordMetricScoreDto { MetricId = MetricId, RawValue = 7 }]
        };

        // Act
        await _evaluations.RecordMetricScoresAsync(evaluation.Id, request, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PlayerEvaluationScored, CoachId);
        properties["session_id"].Should().Be(SessionId);
        properties["evaluation_id"].Should().Be(evaluation.Id);
        properties["metric_count"].Should().Be(1);
    }

    [Test]
    public async Task RecordMetricScoresAsync_WhenTheCallerIsNotTheSessionCoach_CapturesNothing()
    {
        // Arrange
        var participant = Participant(PlayerId);
        var evaluation = Evaluation(participant);
        _evaluationRepository.GetByIdWithScoresAsync(evaluation.Id).Returns(evaluation);
        _sessionRepository.GetByIdAsync(SessionId).Returns(Session(EvaluationSessionStatus.Running));

        // Act
        var act = () => _evaluations.RecordMetricScoresAsync(
            evaluation.Id, new RecordMetricScoresDto(), OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task ShareWithPlayerAsync_WhenTheResultIsShown_CapturesPlayerEvaluationShared()
    {
        // Arrange
        var participant = Participant(PlayerId);
        var evaluation = Evaluation(participant);
        _evaluationRepository.GetByIdWithScoresAsync(evaluation.Id).Returns(evaluation);
        _sessionRepository.GetByIdAsync(SessionId).Returns(Session(EvaluationSessionStatus.Completed));

        // Act
        await _evaluations.ShareWithPlayerAsync(evaluation.Id, share: true, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PlayerEvaluationShared, CoachId);
        properties["evaluation_id"].Should().Be(evaluation.Id);
        properties["session_id"].Should().Be(SessionId);
        properties["is_shared"].Should().Be(true);
    }

    [Test]
    public async Task UpdatePlayerSharingAsync_WhenTheResultIsTakenBack_CapturesPlayerEvaluationShared()
    {
        // Arrange
        var participant = Participant(PlayerId);
        var evaluation = Evaluation(participant);
        StubSession(Session(EvaluationSessionStatus.Completed));
        _evaluationRepository.GetByIdWithScoresAsync(evaluation.Id).Returns(evaluation);
        _participantRepository.GetByIdAsync(participant.Id).Returns(participant);

        // Act
        await _lifecycle.UpdatePlayerSharingAsync(
            SessionId, evaluation.Id, new UpdatePlayerSharingDto { SharedWithPlayer = false }, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PlayerEvaluationShared, CoachId);
        properties["evaluation_id"].Should().Be(evaluation.Id);
        properties["session_id"].Should().Be(SessionId);
        properties["is_shared"].Should().Be(false);
    }

    [Test]
    public async Task UpdatePlayerSharingAsync_WhenTheCallerIsNotTheSessionCoach_CapturesNothing()
    {
        // Arrange
        StubSession(Session(EvaluationSessionStatus.Completed));

        // Act
        var act = () => _lifecycle.UpdatePlayerSharingAsync(
            SessionId, Guid.NewGuid(), new UpdatePlayerSharingDto { SharedWithPlayer = true }, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _analytics.CapturedNothing();
    }

    private void StubSession(EvaluationSession session)
    {
        _sessionRepository.GetByIdWithParticipantsAsync(SessionId).Returns(session);
        _sessionRepository.GetByIdAsync(SessionId).Returns(session);
    }

    private void StubParticipants(List<EvaluationParticipant> participants) =>
        _participantRepository.GetBySessionIdAsync(SessionId).Returns(participants);

    private void StubGroupHolding(List<EvaluationParticipant> participants) =>
        _groupRepository.GetBySessionIdAsync(SessionId).Returns(
        [
            new EvaluationGroup
            {
                SessionId = SessionId,
                Name = "Court one",
                EvaluatorUserId = CoachId,
                Players = participants
                    .Select(participant => new EvaluationGroupPlayer { PlayerId = participant.PlayerId })
                    .ToList()
            }
        ]);

    private static EvaluationSession Session(EvaluationSessionStatus status) => new()
    {
        Id = SessionId,
        ClubId = ClubId,
        EventId = EventId,
        CoachUserId = CoachId,
        EvaluationPlanId = PlanId,
        Title = "Autumn trials",
        Status = status
    };

    private static EvaluationPlan Plan() => new()
    {
        Id = PlanId,
        ClubId = ClubId,
        CreatedByUserId = CoachId,
        Items =
        [
            new EvaluationPlanItem
            {
                PlanId = PlanId,
                ExerciseId = ExerciseId,
                Order = 1,
                Exercise = new EvaluationExercise
                {
                    Id = ExerciseId,
                    Name = "Serve receive",
                    CreatedByUserId = CoachId,
                    Metrics = [new EvaluationMetric { Id = MetricId, ExerciseId = ExerciseId, Name = "Accuracy", MaxPoints = 10 }]
                }
            }
        ]
    };

    private static List<EvaluationParticipant> TwoParticipants() =>
        [Participant(Guid.NewGuid()), Participant(Guid.NewGuid())];

    private static EvaluationParticipant Participant(Guid playerId) => new()
    {
        EvaluationSessionId = SessionId,
        PlayerId = playerId
    };

    private static PlayerEvaluation Evaluation(EvaluationParticipant participant, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        EvaluationParticipantId = participant.Id,
        PlayerId = participant.PlayerId,
        EvaluatedByUserId = CoachId,
        SessionId = SessionId,
        Participant = participant
    };

    private static PlayerExerciseScore ScoredCell(Guid playerId, EvaluationScoreStatus status) => new()
    {
        SessionId = SessionId,
        PlayerId = playerId,
        ExerciseId = ExerciseId,
        Status = status
    };
}
