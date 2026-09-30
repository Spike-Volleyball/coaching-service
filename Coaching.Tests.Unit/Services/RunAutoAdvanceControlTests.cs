using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Templates;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Coaching.Tests.Unit.Analytics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shared.Exceptions;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// A run set to move on by itself, as read and controlled. Nothing reads or acts on a step the run
/// has already left: each read and each control catches the run up first, and a control saves the
/// moves with its own write. Starting a run and the toggle itself set whether it moves on.
/// Steps of 5, 10 and 15 minutes.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RunAutoAdvanceControlTests : RunServiceTestBase
{
    // ---------- Reading ----------

    [Test]
    public async Task GetByEventIdAsync_AfterTheStepsTimeRanOut_ReadsTheRunOnTheNextStep()
    {
        // Arrange — 20 seconds past step 1's end, and nothing has written the move yet.
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));

        // Act
        var result = await _sut.GetByEventIdAsync(EventId, CreatorId);

        // Assert
        result!.AutoAdvance.Should().BeTrue();
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-20));
        result.Items.Single(i => i.PlanItemId == Step1Id).CompletedAt.Should().Be(Now.AddSeconds(-20));
        await AssertNothingWrittenAsync();
    }

    [Test]
    public async Task GetByEventIdAsync_WithAutoAdvanceOff_ReadsTheStepInOvertime()
    {
        // Arrange
        StubRun(RunningOn(Step1Id, elapsedSeconds: 320));

        // Act
        var result = await _sut.GetByEventIdAsync(EventId, CreatorId);

        // Assert
        result!.AutoAdvance.Should().BeFalse();
        result.CurrentItemId.Should().Be(Step1Id);
    }

    // ---------- Controls act on the step the run is on ----------

    [Test]
    public async Task AdvanceAsync_FromTheStepTheRunAlreadyMovedOnFrom_ReturnsTheRunAsItIsNow()
    {
        // Arrange — Next tapped on step 1 after its time ran out and the run had moved on.
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));

        // Act
        var result = await _sut.AdvanceAsync(EventId, Step1Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-20));
        result.CanControl.Should().BeTrue();
        await AssertNothingWrittenAsync();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task AdvanceAsync_FromTheStepTheRunMovedOnTo_MovesOnAndSavesBothMovesAtOnce()
    {
        // Arrange — the phone flipped to step 2 at 0:00, and Next was tapped 20 seconds later,
        // before anything had written the move.
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));

        // Act
        var result = await _sut.AdvanceAsync(EventId, Step2Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step3Id);
        result.CurrentItemStartedAt.Should().Be(Now);

        var step1 = result.Items.Single(i => i.PlanItemId == Step1Id);
        step1.ActualElapsedSeconds.Should().Be(300);
        step1.CompletedAt.Should().Be(Now.AddSeconds(-20));

        var step2 = result.Items.Single(i => i.PlanItemId == Step2Id);
        step2.ActualElapsedSeconds.Should().Be(20);
        step2.CompletedAt.Should().Be(Now);

        await _runRepository.Received(1).SaveChangesAsync();
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Is<RunDto>(d => d.CurrentItemId == Step3Id));
    }

    [Test]
    public async Task AdvanceAsync_FromTheStepTheRunMovedOnTo_CountsTheAutomaticStepThenTheTap()
    {
        // Arrange — an event admin taps Next; the automatic step is the session's, not theirs.
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(true);

        // Act
        await _sut.AdvanceAsync(EventId, Step2Id, OtherUserId);

        // Assert
        var steps = _analytics.CapturedEach(AnalyticsEventNames.PracticeRunStepChanged);
        steps.Should().HaveCount(2);

        steps[0].UserId.Should().Be(CreatorId, "the run was started by the plan's creator");
        steps[0].Properties["direction"].Should().Be(RunStepDirection.Auto);
        steps[0].Properties["from_order"].Should().Be(1);
        steps[0].Properties["to_order"].Should().Be(2);
        steps[0].Properties["elapsed_seconds"].Should().Be(300);
        steps[0].Properties["planned_seconds"].Should().Be(300);

        steps[1].UserId.Should().Be(OtherUserId);
        steps[1].Properties["direction"].Should().Be(RunStepDirection.Next);
        steps[1].Properties["from_order"].Should().Be(2);
        steps[1].Properties["elapsed_seconds"].Should().Be(20);
    }

    [Test]
    public async Task PauseAsync_AfterTheStepsTimeRanOut_PausesTheStepTheRunMovedOnTo()
    {
        // Arrange
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Paused);
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemPausedElapsedSeconds.Should().Be(20);
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task CompleteAsync_AQueuedTapFromBeforeTheRunMovedOn_EndsItNoEarlierThanTheMove()
    {
        // Arrange — step 1 ran out 20 seconds ago; End Session, queued 30 seconds ago on a phone
        // with no signal, reaches the server now. Nothing can have happened before the run's last
        // change, and moving on was one.
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));

        // Act
        var result = await _sut.CompleteAsync(EventId, CreatorId, occurredAt: Now.AddSeconds(-30));

        // Assert
        result.Status.Should().Be(RunStatus.Completed);
        result.CompletedAt.Should().Be(Now.AddSeconds(-20));
        result.Items.Single(i => i.PlanItemId == Step2Id).CompletedAt.Should().Be(Now.AddSeconds(-20));
    }

    [Test]
    public async Task GoToAsync_BackToAStepThatRanItsTimeOut_MovesOnFromItAgainAtOnce()
    {
        // Arrange — step 1 ran its five minutes out; a minute into step 2 the coach goes back to it.
        // It is entered again with no time left, so by the rule it is left the instant it is
        // entered, and step 2 — discarded by the step back — starts over.
        StubRun(AutoAdvancingOn(Step2Id, elapsedSeconds: 60));

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step1Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now);
        await _runRepository.Received(1).SaveChangesAsync();
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Is<RunDto>(d => d.CurrentItemId == Step2Id));
        _analytics.CapturedEach(AnalyticsEventNames.PracticeRunStepChanged)
            .Select(c => c.Properties["direction"])
            .Should().Equal(RunStepDirection.Previous, RunStepDirection.Auto);
    }

    [Test]
    public async Task PauseAsync_WhenAnotherPhonesWriteLandedFirst_ReturnsThatRunCaughtUp()
    {
        // Arrange — the other phone's write left the run on step 1, whose time has since run out.
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));
        _runRepository.SaveChangesAsync().ThrowsAsync(new DbUpdateConcurrencyException("The run changed since it was read."));
        _runRepository.GetByEventIdWithDetailsNoTrackingAsync(EventId).Returns(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step2Id);
        _analytics.CapturedNothing();
    }

    // ---------- The toggle ----------

    [Test]
    public async Task SetAutoAdvanceAsync_On_SavesItAndTellsEveryDevice()
    {
        // Arrange
        StubRun(RunningOn(Step1Id, elapsedSeconds: 60));

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: true, CreatorId);

        // Assert
        result.AutoAdvance.Should().BeTrue();
        result.CanControl.Should().BeTrue();
        result.CurrentItemId.Should().Be(Step1Id);
        await _runRepository.Received(1).SaveChangesAsync();
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Is<RunDto>(d => d.AutoAdvance));
    }

    [Test]
    public async Task SetAutoAdvanceAsync_Off_SavesItAndTellsEveryDevice()
    {
        // Arrange
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 60));

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: false, CreatorId);

        // Assert
        result.AutoAdvance.Should().BeFalse();
        await _runRepository.Received(1).SaveChangesAsync();
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Is<RunDto>(d => !d.AutoAdvance));
    }

    [Test]
    public async Task SetAutoAdvanceAsync_OffAfterTheStepsTimeRanOut_KeepsTheMoveThatHadHappened()
    {
        // Arrange — step 1 ran out 20 seconds before the coach switched auto-advance off.
        StubRun(AutoAdvancingOn(Step1Id, elapsedSeconds: 320));

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: false, CreatorId);

        // Assert
        result.AutoAdvance.Should().BeFalse();
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-20));
        await _runRepository.Received(1).SaveChangesAsync();
        _analytics.CapturedEach(AnalyticsEventNames.PracticeRunStepChanged)
            .Should().ContainSingle().Which.Properties["direction"].Should().Be(RunStepDirection.Auto);
    }

    [Test]
    public async Task SetAutoAdvanceAsync_OnWithTheStepInOvertime_MovesOnFromWhenItsTimeRanOut()
    {
        // Arrange — step 1 has run 100 seconds over when the coach switches auto-advance on. The
        // rule starts step 2 where step 1's time ran out.
        StubRun(RunningOn(Step1Id, elapsedSeconds: 400));

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: true, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-100));
        result.Items.Single(i => i.PlanItemId == Step1Id).ActualElapsedSeconds.Should().Be(300);
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [TestCase(RunStatus.Paused)]
    [TestCase(RunStatus.Completed)]
    public async Task SetAutoAdvanceAsync_OnARunThatIsNotRunning_SavesIt(RunStatus status)
    {
        // Arrange
        StubRun(status == RunStatus.Paused
            ? PausedOn(Step2Id, elapsedSeconds: 30)
            : CompletedOn(Step3Id, elapsedSeconds: 30));

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: true, CreatorId);

        // Assert
        result.AutoAdvance.Should().BeTrue();
        result.Status.Should().Be(status);
        await _runRepository.Received(1).SaveChangesAsync();
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Any<RunDto>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SetAutoAdvanceAsync_ToWhatItAlreadyIs_WritesNothing(bool enabled)
    {
        // Arrange — a second phone's tap, after the first already switched it.
        var run = RunningOn(Step1Id, elapsedSeconds: 60);
        run.AutoAdvance = enabled;
        StubRun(run);

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled, CreatorId);

        // Assert
        result.AutoAdvance.Should().Be(enabled);
        await AssertNothingWrittenAsync();
    }

    [Test]
    public async Task SetAutoAdvanceAsync_EventAdminWhoDidNotCreateThePlan_MaySetIt()
    {
        // Arrange
        StubRun(RunningOn(Step1Id, elapsedSeconds: 60));
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(true);

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: true, OtherUserId);

        // Assert
        result.AutoAdvance.Should().BeTrue();
    }

    [Test]
    public async Task SetAutoAdvanceAsync_NeitherCreatorNorEventAdmin_ThrowsForbidden()
    {
        // Arrange
        StubRun(RunningOn(Step1Id, elapsedSeconds: 60));

        // Act
        var act = () => _sut.SetAutoAdvanceAsync(EventId, enabled: true, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        await AssertNothingWrittenAsync();
    }

    [Test]
    public async Task SetAutoAdvanceAsync_NoRun_ThrowsNotFound()
    {
        // Arrange
        StubNoRun();

        // Act
        var act = () => _sut.SetAutoAdvanceAsync(EventId, enabled: true, CreatorId);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException>();
    }

    // ---------- Starting ----------

    [Test]
    public async Task StartAsync_WithAutoAdvance_StartsTheRunMovingOnByItself()
    {
        // Arrange
        StubNoRun();

        // Act
        var result = await _sut.StartAsync(EventId, CreatorId, autoAdvance: true);

        // Assert
        result.AutoAdvance.Should().BeTrue();
        _runRepository.Received(1).Add(Arg.Is<TrainingPlanRun>(r => r.AutoAdvance));
    }

    [Test]
    public async Task StartAsync_ANewRunNotSaidEitherWay_StartsWithoutIt()
    {
        // Arrange
        StubNoRun();

        // Act
        var result = await _sut.StartAsync(EventId, CreatorId);

        // Assert
        result.AutoAdvance.Should().BeFalse();
    }

    [Test]
    public async Task StartAsync_StartedOverNotSaidEitherWay_KeepsWhatTheRunHad()
    {
        // Arrange — Start Over from builds that know nothing of auto-advance.
        var run = CompletedOn(Step3Id, elapsedSeconds: 30);
        run.AutoAdvance = true;
        StubRun(run);

        // Act
        var result = await _sut.StartAsync(EventId, CreatorId);

        // Assert
        result.AutoAdvance.Should().BeTrue();
    }

    [Test]
    public async Task StartAsync_StartedOverWithAutoAdvanceOff_StartsWithoutIt()
    {
        // Arrange
        StubRun(AutoAdvancingOn(Step2Id, elapsedSeconds: 30));

        // Act
        var result = await _sut.StartAsync(EventId, CreatorId, restart: true, autoAdvance: false);

        // Assert
        result.AutoAdvance.Should().BeFalse();
        result.CurrentItemId.Should().Be(Step1Id);
    }

    private async Task AssertNothingWrittenAsync()
    {
        await _runRepository.DidNotReceive().SaveChangesAsync();
        await _broadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
    }
}
