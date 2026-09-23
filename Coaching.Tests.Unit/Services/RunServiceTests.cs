using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using FluentAssertions;
using MockQueryable;
using NSubstitute;
using Shared.Exceptions;
using Shared.Services.Analytics;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class RunServiceTests : UnitTestBase
{
    private ITrainingPlanRunRepository _runRepository = null!;
    private ITrainingPlanRepository _planRepository = null!;
    private IRunBroadcaster _broadcaster = null!;
    private IEventsGrpcClient _eventsGrpcClient = null!;
    private RunService _sut = null!;

    private static readonly Guid CreatorId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();
    private static readonly Guid Item1Id = Guid.NewGuid();
    private static readonly Guid Item2Id = Guid.NewGuid();
    private static readonly Guid Drill1Id = Guid.NewGuid();
    private static readonly Guid Drill2Id = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _runRepository = Substitute.For<ITrainingPlanRunRepository>();
        _planRepository = Substitute.For<ITrainingPlanRepository>();
        _broadcaster = Substitute.For<IRunBroadcaster>();
        _eventsGrpcClient = Substitute.For<IEventsGrpcClient>();
        _sut = new RunService(
            _runRepository,
            Substitute.For<ITrainingPlanRunItemRepository>(),
            Substitute.For<IRunStationRepository>(),
            _planRepository,
            _broadcaster,
            _eventsGrpcClient,
            TimeProvider,
            Substitute.For<IAnalyticsCapture>());
    }

    // Two-item instance plan created by CreatorId, attached to EventId.
    private TrainingPlan BuildPlan()
    {
        return new TrainingPlan
        {
            Id = PlanId,
            Name = "Test Plan",
            CreatedByUserId = CreatorId,
            PlanType = PlanType.Instance,
            EventId = EventId,
            Items = new List<PlanItem>
            {
                new() { Id = Item1Id, TemplateId = PlanId, DrillId = Drill1Id, Order = 1, Duration = 5 },
                new() { Id = Item2Id, TemplateId = PlanId, DrillId = Drill2Id, Order = 2, Duration = 10 }
            }
        };
    }

    private void StubPlanQuery(TrainingPlan plan)
    {
        var planMock = new List<TrainingPlan> { plan }.BuildMock();
        _planRepository.Query().Returns(planMock);
    }

    private void StubNoRun() =>
        _runRepository.GetByEventIdWithDetailsAsync(EventId).Returns((TrainingPlanRun?)null);

    private void StubExistingRun(TrainingPlanRun run)
    {
        _runRepository.GetByEventIdWithDetailsAsync(EventId).Returns(run);
        var runMock = new List<TrainingPlanRun> { run }.BuildMock();
        _runRepository.Query().Returns(runMock);
    }

    // ---------- Start ----------

    [Test]
    public async Task StartAsync_NoExistingRun_SnapshotsAllItemsAndSetsFirstCurrent()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        StubNoRun();

        // Act
        var result = await _sut.StartAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.PlanId.Should().Be(PlanId);
        result.EventId.Should().Be(EventId);
        result.StartedByUserId.Should().Be(CreatorId);
        result.CurrentItemId.Should().Be(Item1Id);
        result.CurrentItemStartedAt.Should().Be(Now);
        result.StartedAt.Should().Be(Now);
        result.ServerTime.Should().Be(Now);
        result.CanControl.Should().BeTrue();
        result.Items.Should().HaveCount(2);

        var first = result.Items.Single(i => i.PlanItemId == Item1Id);
        first.Order.Should().Be(1);
        first.DrillId.Should().Be(Drill1Id);
        first.PlannedDurationSeconds.Should().Be(300); // 5 * 60
        first.StartedAt.Should().Be(Now);

        var second = result.Items.Single(i => i.PlanItemId == Item2Id);
        second.PlannedDurationSeconds.Should().Be(600); // 10 * 60
        second.StartedAt.Should().BeNull();
    }

    [Test]
    public async Task StartAsync_AddsRunAndSavesOnce()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        StubNoRun();

        // Act
        await _sut.StartAsync(EventId, CreatorId);

        // Assert
        _runRepository.Received(1).Add(Arg.Any<TrainingPlanRun>());
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task StartAsync_BroadcastsRunUpdated()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        StubNoRun();

        // Act
        await _sut.StartAsync(EventId, CreatorId);

        // Assert
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Is<RunDto>(d => d.Status == RunStatus.Running));
    }

    [Test]
    public async Task StartAsync_ExistingRun_ResetsInPlaceAndDeletesOldItems()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var oldRun = new TrainingPlanRun
        {
            Id = Guid.NewGuid(),
            PlanId = PlanId,
            EventId = EventId,
            StartedByUserId = CreatorId,
            Status = RunStatus.Completed,
            StartedAtUtc = PastDate(1),
            CompletedAtUtc = PastDate(1),
            Items = new List<TrainingPlanRunItem>
            {
                new() { Id = Guid.NewGuid(), PlanItemId = Item1Id, DrillId = Drill1Id, Order = 1, PlannedDurationSeconds = 300 }
            }
        };
        StubExistingRun(oldRun);

        // Act
        var result = await _sut.StartAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Item1Id);
        result.CompletedAt.Should().BeNull();
        result.Items.Should().HaveCount(2);
        _runRepository.DidNotReceive().Add(Arg.Any<TrainingPlanRun>());
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task StartAsync_NotPlanCreator_ThrowsForbidden()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        StubNoRun();

        // Act
        var act = () => _sut.StartAsync(EventId, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task StartAsync_NoPlanForEvent_ThrowsNotFound()
    {
        // Arrange
        _planRepository.Query().Returns(new List<TrainingPlan>().BuildMock());

        // Act
        var act = () => _sut.StartAsync(EventId, CreatorId);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException>();
    }

    // ---------- Pause / Resume timer math ----------

    [Test]
    public async Task PauseAsync_CapturesElapsedAndClearsVirtualStart()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 0);
        StubExistingRun(run);
        AdvanceTime(TimeSpan.FromSeconds(90));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Paused);
        result.CurrentItemPausedElapsedSeconds.Should().Be(90);
        result.CurrentItemStartedAt.Should().BeNull();
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task PauseAsync_EventAdminWhoDidNotCreateThePlan_MayControlTheRun()
    {
        // Arrange — the lead coach of the event runs the session from the floor; the plan may
        // have been built by someone else entirely.
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 0);
        StubExistingRun(run);
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(true);
        AdvanceTime(TimeSpan.FromSeconds(30));

        // Act
        var result = await _sut.PauseAsync(EventId, OtherUserId);

        // Assert
        result.Status.Should().Be(RunStatus.Paused);
        result.CurrentItemPausedElapsedSeconds.Should().Be(30);
    }

    [Test]
    public async Task PauseAsync_NeitherCreatorNorEventAdmin_ThrowsForbidden()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        StubExistingRun(RunningRunOnFirstItem(startedSecondsAgo: 0));
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(false);

        // Act
        var act = () => _sut.PauseAsync(EventId, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task ResumeAsync_ReanchorsVirtualStartFromPausedElapsed()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = new TrainingPlanRun
        {
            Id = Guid.NewGuid(),
            PlanId = PlanId,
            EventId = EventId,
            StartedByUserId = CreatorId,
            Status = RunStatus.Paused,
            CurrentItemId = Item1Id,
            CurrentItemStartedAtUtc = null,
            CurrentItemPausedElapsedSeconds = 90,
            StartedAtUtc = PastDate(0, 1),
            Items = TwoRunItems()
        };
        StubExistingRun(run);

        // Act
        var result = await _sut.ResumeAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        // Virtual start is now - pausedElapsed, so elapsed reads as 90s at the current frozen time.
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-90));
        await _runRepository.Received(1).SaveChangesAsync();
    }

    // ---------- Advance ----------

    [Test]
    public async Task AdvanceAsync_FinalizesCurrentItemAndMovesToNext()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 0);
        StubExistingRun(run);
        AdvanceTime(TimeSpan.FromSeconds(120));

        // Act
        var result = await _sut.AdvanceAsync(EventId, Item1Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Item2Id);
        result.CurrentItemStartedAt.Should().Be(Now);

        var finalized = result.Items.Single(i => i.PlanItemId == Item1Id);
        finalized.ActualElapsedSeconds.Should().Be(120);
        finalized.CompletedAt.Should().Be(Now);

        var next = result.Items.Single(i => i.PlanItemId == Item2Id);
        next.StartedAt.Should().Be(Now);
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task AdvanceAsync_OnLastItem_CompletesRun()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnSecondItem();
        StubExistingRun(run);
        AdvanceTime(TimeSpan.FromSeconds(45));

        // Act
        var result = await _sut.AdvanceAsync(EventId, Item2Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Completed);
        result.CurrentItemId.Should().BeNull();
        result.CompletedAt.Should().Be(Now);
        result.Items.Single(i => i.PlanItemId == Item2Id).CompletedAt.Should().Be(Now);
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task AdvanceAsync_FromItemMismatch_ReturnsCurrentStateUnchanged()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 30);
        StubExistingRun(run);

        // Act — caller thinks it's on Item2 but the run is actually on Item1 (double-tap / stale).
        var result = await _sut.AdvanceAsync(EventId, Item2Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Item1Id);
        result.Status.Should().Be(RunStatus.Running);
        await _runRepository.DidNotReceive().SaveChangesAsync();
        await _broadcaster.DidNotReceive().BroadcastRunUpdatedAsync(Arg.Any<Guid>(), Arg.Any<RunDto>());
    }

    [Test]
    public async Task AdvanceAsync_NotPlanCreator_ThrowsForbidden()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 0);
        StubExistingRun(run);

        // Act
        var act = () => _sut.AdvanceAsync(EventId, Item1Id, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    // ---------- Complete ----------

    [Test]
    public async Task CompleteAsync_FinalizesCurrentItemAndSetsCompleted()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 0);
        StubExistingRun(run);
        AdvanceTime(TimeSpan.FromSeconds(75));

        // Act
        var result = await _sut.CompleteAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Completed);
        result.CurrentItemId.Should().BeNull();
        result.CompletedAt.Should().Be(Now);
        result.Items.Single(i => i.PlanItemId == Item1Id).ActualElapsedSeconds.Should().Be(75);
        await _runRepository.Received(1).SaveChangesAsync();
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Any<RunDto>());
    }

    // ---------- Who may watch ----------

    [Test]
    public async Task CanReadRunAsync_ThePlansCreator_MayWithoutAskingEventsService()
    {
        // Arrange
        StubPlanQuery(BuildPlan());

        // Act
        var mayRead = await _sut.CanReadRunAsync(EventId, CreatorId);

        // Assert
        mayRead.Should().BeTrue();
        await _eventsGrpcClient.DidNotReceive().IsEventParticipantAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    [TestCase(false, false, false)]
    public async Task CanReadRunAsync_SomeoneElse_MayWhenOnTheEventAsAParticipantOrAHost(bool participant, bool host, bool expected)
    {
        // Arrange
        StubPlanQuery(BuildPlan());
        _eventsGrpcClient.IsEventParticipantAsync(EventId, OtherUserId).Returns((participant, true));
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(host);

        // Act
        var mayRead = await _sut.CanReadRunAsync(EventId, OtherUserId);

        // Assert
        mayRead.Should().Be(expected);
    }

    [Test]
    public async Task CanReadRunAsync_ForAnEventThatDoesNotExist_IsFalse()
    {
        // Arrange
        StubPlanQuery(BuildPlan());
        _eventsGrpcClient.IsEventParticipantAsync(EventId, OtherUserId).Returns((false, false));

        // Act
        var mayRead = await _sut.CanReadRunAsync(EventId, OtherUserId);

        // Assert
        mayRead.Should().BeFalse();
    }

    // ---------- Get ----------

    [Test]
    public async Task GetByEventIdAsync_Creator_NoRunYet_ReturnsNullWithoutCallingEventsService()
    {
        // Arrange — a plan is attached (so "creator" is well-defined) but no run has started yet.
        var plan = BuildPlan();
        StubPlanQuery(plan);
        StubNoRun();

        // Act
        var result = await _sut.GetByEventIdAsync(EventId, CreatorId);

        // Assert
        result.Should().BeNull();
        await _eventsGrpcClient.DidNotReceive().IsEventParticipantAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task GetByEventIdAsync_UnrelatedUser_NoRunYet_ThrowsForbiddenSameAsWhenRunExists()
    {
        // Arrange — the regression this guards: an unauthorized caller must get the identical
        // response whether or not a run exists yet. Without gating before the run fetch, "no run"
        // returned 200 null while "run exists" returned 403, leaking the run's existence.
        var plan = BuildPlan();
        StubPlanQuery(plan);
        StubNoRun();
        _eventsGrpcClient.IsEventParticipantAsync(EventId, OtherUserId).Returns((false, true));
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(false);

        // Act
        var act = () => _sut.GetByEventIdAsync(EventId, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task GetByEventIdAsync_NoPlanAttached_ParticipantReturnsNull()
    {
        // Arrange — nothing has been created for this event at all yet; a legitimate participant
        // must still be able to observe "no run" rather than being denied.
        _planRepository.Query().Returns(new List<TrainingPlan>().BuildMock());
        StubNoRun();
        _eventsGrpcClient.IsEventParticipantAsync(EventId, OtherUserId).Returns((true, true));

        // Act
        var result = await _sut.GetByEventIdAsync(EventId, OtherUserId);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task GetByEventIdAsync_EventDoesNotExist_ThrowsNotFound()
    {
        // Arrange — mirrors TrainingPlanService.GetByEventIdAsync's eventExists-404 vs
        // not-participant-403 distinction.
        _planRepository.Query().Returns(new List<TrainingPlan>().BuildMock());
        _eventsGrpcClient.IsEventParticipantAsync(EventId, OtherUserId).Returns((false, false));

        // Act
        var act = () => _sut.GetByEventIdAsync(EventId, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException>();
    }

    [Test]
    public async Task GetByEventIdAsync_Creator_ReturnsDtoWithCanControlTrueWithoutCallingEventsService()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 10);
        StubExistingRun(run);

        // Act
        var result = await _sut.GetByEventIdAsync(EventId, CreatorId);

        // Assert
        result.Should().NotBeNull();
        result!.CanControl.Should().BeTrue();
        result.ServerTime.Should().Be(Now);
        await _eventsGrpcClient.DidNotReceive().IsEventParticipantAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
        await _eventsGrpcClient.DidNotReceive().IsEventAdminAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task GetByEventIdAsync_Participant_ReturnsDtoWithCanControlFalse()
    {
        // Arrange
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 10);
        StubExistingRun(run);
        _eventsGrpcClient.IsEventParticipantAsync(EventId, OtherUserId).Returns((true, true));

        // Act
        var result = await _sut.GetByEventIdAsync(EventId, OtherUserId);

        // Assert
        result.Should().NotBeNull();
        result!.CanControl.Should().BeFalse();
        result.ServerTime.Should().Be(Now);
    }

    [Test]
    public async Task GetByEventIdAsync_EventHostNonParticipant_ReturnsDtoWithCanControlFalse()
    {
        // Arrange — an organizer/co-organizer who isn't in the participant roster should still see the run.
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 10);
        StubExistingRun(run);
        _eventsGrpcClient.IsEventParticipantAsync(EventId, OtherUserId).Returns((false, true));
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(true);

        // Act
        var result = await _sut.GetByEventIdAsync(EventId, OtherUserId);

        // Assert
        result.Should().NotBeNull();
        result!.CanControl.Should().BeFalse();
    }

    [Test]
    public async Task GetByEventIdAsync_UnrelatedUser_ThrowsForbidden()
    {
        // Arrange — neither the plan creator, nor a participant, nor an event host.
        var plan = BuildPlan();
        StubPlanQuery(plan);
        var run = RunningRunOnFirstItem(startedSecondsAgo: 10);
        StubExistingRun(run);
        _eventsGrpcClient.IsEventParticipantAsync(EventId, OtherUserId).Returns((false, true));
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(false);

        // Act
        var act = () => _sut.GetByEventIdAsync(EventId, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    // ---------- Builders ----------

    private List<TrainingPlanRunItem> TwoRunItems() => new()
    {
        new() { Id = Guid.NewGuid(), PlanItemId = Item1Id, DrillId = Drill1Id, Order = 1, PlannedDurationSeconds = 300 },
        new() { Id = Guid.NewGuid(), PlanItemId = Item2Id, DrillId = Drill2Id, Order = 2, PlannedDurationSeconds = 600 }
    };

    private TrainingPlanRun RunningRunOnFirstItem(int startedSecondsAgo)
    {
        var items = TwoRunItems();
        items[0].StartedAtUtc = Now.AddSeconds(-startedSecondsAgo);
        return new TrainingPlanRun
        {
            Id = Guid.NewGuid(),
            PlanId = PlanId,
            EventId = EventId,
            StartedByUserId = CreatorId,
            Status = RunStatus.Running,
            CurrentItemId = Item1Id,
            CurrentItemStartedAtUtc = Now.AddSeconds(-startedSecondsAgo),
            StartedAtUtc = Now.AddSeconds(-startedSecondsAgo),
            Items = items
        };
    }

    private TrainingPlanRun RunningRunOnSecondItem()
    {
        var items = TwoRunItems();
        items[0].StartedAtUtc = PastDate(0, 1);
        items[0].CompletedAtUtc = PastDate(0, 1);
        items[0].ActualElapsedSeconds = 300;
        items[1].StartedAtUtc = Now;
        return new TrainingPlanRun
        {
            Id = Guid.NewGuid(),
            PlanId = PlanId,
            EventId = EventId,
            StartedByUserId = CreatorId,
            Status = RunStatus.Running,
            CurrentItemId = Item2Id,
            CurrentItemStartedAtUtc = Now,
            StartedAtUtc = PastDate(0, 1),
            Items = items
        };
    }
}
