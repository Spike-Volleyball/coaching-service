using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using FluentAssertions;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;
using Shared.Services.Analytics;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Who may score whom in a running session: its coach scores anyone, and an evaluator scores the
/// players of the groups they evaluate — not every player in the session.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationScoringAccessTests : UnitTestBase
{
    private IEvaluationSessionRepository _sessions = null!;
    private IPlayerExerciseScoreRepository _scores = null!;
    private IPlayerEvaluationRepository _evaluations = null!;
    private IEvaluationPlanRepository _plans = null!;
    private EvaluationScoringService _sut = null!;

    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _firstEvaluatorId = Guid.NewGuid();
    private readonly Guid _secondEvaluatorId = Guid.NewGuid();
    private readonly Guid _firstGroupPlayer = Guid.NewGuid();
    private readonly Guid _secondGroupPlayer = Guid.NewGuid();
    private readonly Guid _exerciseId = Guid.NewGuid();
    private readonly Guid _metricId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _sessions = Substitute.For<IEvaluationSessionRepository>();
        _scores = Substitute.For<IPlayerExerciseScoreRepository>();
        _evaluations = Substitute.For<IPlayerEvaluationRepository>();
        _plans = Substitute.For<IEvaluationPlanRepository>();

        var plan = new EvaluationPlan
        {
            CreatedByUserId = _coachId,
            Items =
            [
                new EvaluationPlanItem
                {
                    ExerciseId = _exerciseId,
                    Exercise = new EvaluationExercise
                    {
                        Id = _exerciseId,
                        Name = "Serve receive",
                        Metrics = [new EvaluationMetric { Id = _metricId, ExerciseId = _exerciseId, Name = "Accuracy", MaxPoints = 10 }],
                    },
                },
            ],
        };
        _plans.GetByIdWithItemsAsync(plan.Id).Returns(plan);

        var session = new EvaluationSession
        {
            ClubId = Guid.NewGuid(),
            CoachUserId = _coachId,
            Title = "Autumn trials",
            Status = EvaluationSessionStatus.Running,
            EvaluationPlanId = plan.Id,
        };
        foreach (var player in new[] { _firstGroupPlayer, _secondGroupPlayer })
        {
            var participant = new EvaluationParticipant { EvaluationSessionId = session.Id, PlayerId = player };
            session.Participants.Add(participant);
            _evaluations.GetByParticipantIdAsync(participant.Id)
                .Returns(new PlayerEvaluation { EvaluationParticipantId = participant.Id, PlayerId = player, SessionId = session.Id });
        }
        session.Groups.Add(GroupOf(session.Id, _firstEvaluatorId, _firstGroupPlayer));
        session.Groups.Add(GroupOf(session.Id, _secondEvaluatorId, _secondGroupPlayer));
        _sessions.GetByIdWithParticipantsAsync(session.Id).Returns(session);
        SessionId = session.Id;

        var mapper = Substitute.For<IMapper>();
        mapper.Map<PlayerExerciseScoreDto>(Arg.Any<PlayerExerciseScore>()).Returns(new PlayerExerciseScoreDto());

        _sut = new EvaluationScoringService(
            _sessions,
            _scores,
            Substitute.For<IEvaluationGroupRepository>(),
            _plans,
            _evaluations,
            Substitute.For<IRepository<PlayerMetricScore>>(),
            Substitute.For<IScoreCalculationService>(),
            Substitute.For<IAnalyticsCapture>(),
            Substitute.For<IEvaluationAccess>(),
            mapper);
    }

    private Guid SessionId { get; set; }

    private static EvaluationGroup GroupOf(Guid sessionId, Guid evaluatorId, Guid playerId)
    {
        var group = new EvaluationGroup { SessionId = sessionId, Name = "Court", EvaluatorUserId = evaluatorId };
        group.Players.Add(new EvaluationGroupPlayer { GroupId = group.Id, PlayerId = playerId });
        return group;
    }

    private SubmitExerciseScoresDto ScoreFor(Guid playerId) => new()
    {
        PlayerId = playerId,
        ExerciseId = _exerciseId,
        Scores = [new MetricScoreValueDto { MetricId = _metricId, Value = 8 }],
    };

    [Test]
    public async Task SubmitExerciseScoresAsync_ByAnEvaluatorForAPlayerInTheirGroup_ScoresThem()
    {
        // Act
        await _sut.SubmitExerciseScoresAsync(SessionId, ScoreFor(_firstGroupPlayer), _firstEvaluatorId);

        // Assert
        _scores.Received(1).Add(Arg.Is<PlayerExerciseScore>(s => s.PlayerId == _firstGroupPlayer));
    }

    [Test]
    public async Task SubmitExerciseScoresAsync_ByAnEvaluatorForAPlayerInAnotherGroup_ThrowsForbiddenAndScoresNothing()
    {
        // Act
        var act = () => _sut.SubmitExerciseScoresAsync(SessionId, ScoreFor(_secondGroupPlayer), _firstEvaluatorId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _scores.DidNotReceive().Add(Arg.Any<PlayerExerciseScore>());
        await _scores.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task SubmitExerciseScoresAsync_ByTheSessionsCoach_ScoresAPlayerInAnyGroup()
    {
        // Act
        await _sut.SubmitExerciseScoresAsync(SessionId, ScoreFor(_secondGroupPlayer), _coachId);

        // Assert
        _scores.Received(1).Add(Arg.Is<PlayerExerciseScore>(s => s.PlayerId == _secondGroupPlayer));
    }

    [Test]
    public async Task SubmitExerciseScoresAsync_BySomeoneWhoEvaluatesNoGroup_ThrowsForbidden()
    {
        // Act
        var act = () => _sut.SubmitExerciseScoresAsync(SessionId, ScoreFor(_firstGroupPlayer), Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _scores.DidNotReceive().Add(Arg.Any<PlayerExerciseScore>());
    }
}
