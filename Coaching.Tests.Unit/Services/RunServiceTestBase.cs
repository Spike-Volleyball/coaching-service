using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using MockQueryable;
using NSubstitute;
using Shared.Services.Analytics;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// A <see cref="RunService"/> over substitute repositories, an event plan of three steps written by
/// <see cref="CreatorId"/>, and runs of that plan arranged at whatever point a test needs.
/// </summary>
public abstract class RunServiceTestBase : UnitTestBase
{
    protected static readonly Guid CreatorId = Guid.NewGuid();
    protected static readonly Guid OtherUserId = Guid.NewGuid();
    protected static readonly Guid EventId = Guid.NewGuid();
    protected static readonly Guid PlanId = Guid.NewGuid();
    protected static readonly Guid Step1Id = Guid.NewGuid();
    protected static readonly Guid Step2Id = Guid.NewGuid();
    protected static readonly Guid Step3Id = Guid.NewGuid();

    protected ITrainingPlanRunRepository _runRepository = null!;
    protected ITrainingPlanRepository _planRepository = null!;
    protected IRunBroadcaster _broadcaster = null!;
    protected IEventsGrpcClient _eventsGrpcClient = null!;
    protected IAnalyticsCapture _analytics = null!;
    protected RunService _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _runRepository = Substitute.For<ITrainingPlanRunRepository>();
        _planRepository = Substitute.For<ITrainingPlanRepository>();
        _broadcaster = Substitute.For<IRunBroadcaster>();
        _eventsGrpcClient = Substitute.For<IEventsGrpcClient>();
        _analytics = Substitute.For<IAnalyticsCapture>();
        _sut = new RunService(
            _runRepository,
            Substitute.For<ITrainingPlanRunItemRepository>(),
            Substitute.For<IRunStationRepository>(),
            _planRepository,
            _broadcaster,
            _eventsGrpcClient,
            TimeProvider,
            _analytics);

        _planRepository.Query().Returns(_ => new List<TrainingPlan> { Plan() }.BuildMock());
    }

    /// <summary>Three drills of 5, 10 and 15 minutes, in that order.</summary>
    protected static TrainingPlan Plan() => new()
    {
        Id = PlanId,
        Name = "Tuesday session",
        CreatedByUserId = CreatorId,
        PlanType = PlanType.Instance,
        EventId = EventId,
        Items =
        [
            new PlanItem { Id = Step1Id, TemplateId = PlanId, DrillId = Guid.NewGuid(), Order = 1, Duration = 5 },
            new PlanItem { Id = Step2Id, TemplateId = PlanId, DrillId = Guid.NewGuid(), Order = 2, Duration = 10 },
            new PlanItem { Id = Step3Id, TemplateId = PlanId, DrillId = Guid.NewGuid(), Order = 3, Duration = 15 }
        ]
    };

    /// <summary>
    /// A run <paramref name="elapsedSeconds"/> into <paramref name="stepId"/>, which it entered
    /// last — its latest change. Every earlier step was played for exactly its planned length.
    /// </summary>
    protected TrainingPlanRun RunningOn(Guid stepId, int elapsedSeconds)
    {
        var run = new TrainingPlanRun
        {
            PlanId = PlanId,
            EventId = EventId,
            StartedByUserId = CreatorId,
            StartedAtUtc = Now.AddHours(-1),
            Items = Plan().Items
                .Select(i => new TrainingPlanRunItem
                {
                    PlanItemId = i.Id,
                    DrillId = i.DrillId,
                    Order = i.Order,
                    PlannedDurationSeconds = i.Duration * 60
                })
                .ToList()
        };

        var current = Step(run, stepId);
        var played = run.StartedAtUtc;
        foreach (var earlier in run.Items.Where(i => i.Order < current.Order).OrderBy(i => i.Order))
        {
            earlier.StartedAtUtc = played;
            earlier.ActualElapsedSeconds = earlier.PlannedDurationSeconds;
            played = played.AddSeconds(earlier.PlannedDurationSeconds);
            earlier.CompletedAtUtc = played;
        }

        var entered = Now.AddSeconds(-elapsedSeconds);
        current.StartedAtUtc = entered;
        run.Status = RunStatus.Running;
        run.CurrentItemId = stepId;
        run.CurrentItemStartedAtUtc = entered;
        run.UpdatedAt = entered;
        return run;
    }

    /// <summary><see cref="RunningOn"/>, set to move on by itself when a step's time is up.</summary>
    protected TrainingPlanRun AutoAdvancingOn(Guid stepId, int elapsedSeconds)
    {
        var run = RunningOn(stepId, elapsedSeconds);
        run.AutoAdvance = true;
        return run;
    }

    /// <summary>Paused just now, <paramref name="elapsedSeconds"/> into <paramref name="stepId"/>.</summary>
    protected TrainingPlanRun PausedOn(Guid stepId, int elapsedSeconds)
    {
        var run = RunningOn(stepId, elapsedSeconds);
        run.Status = RunStatus.Paused;
        run.CurrentItemStartedAtUtc = null;
        run.CurrentItemPausedElapsedSeconds = elapsedSeconds;
        run.UpdatedAt = Now;
        return run;
    }

    /// <summary>
    /// Ended just now, <paramref name="elapsedSeconds"/> into <paramref name="stepId"/> — by End
    /// Session, or by Next on the last step.
    /// </summary>
    protected TrainingPlanRun CompletedOn(Guid stepId, int elapsedSeconds)
    {
        var run = RunningOn(stepId, elapsedSeconds);
        var current = Step(run, stepId);
        current.ActualElapsedSeconds = elapsedSeconds;
        current.CompletedAtUtc = Now;
        run.Status = RunStatus.Completed;
        run.CurrentItemId = null;
        run.CurrentItemStartedAtUtc = null;
        run.CompletedAtUtc = Now;
        run.UpdatedAt = Now;
        return run;
    }

    protected static TrainingPlanRunItem Step(TrainingPlanRun run, Guid stepId) =>
        run.Items.Single(i => i.PlanItemId == stepId);

    protected void StubRun(TrainingPlanRun run) =>
        _runRepository.GetByEventIdWithDetailsAsync(EventId).Returns(run);

    protected void StubNoRun() =>
        _runRepository.GetByEventIdWithDetailsAsync(EventId).Returns((TrainingPlanRun?)null);

    /// <summary>The run as the auto-advance sweep loads it, by its own id.</summary>
    protected void StubRunById(TrainingPlanRun run) =>
        _runRepository.GetWithDetailsAsync(run.Id).Returns(run);
}
