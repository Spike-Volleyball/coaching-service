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

    // ---------- A step that ran its full time starts over ----------

    [Test]
    public async Task GoToAsync_BackToAStepThatRanItsFullTime_PlaysItsFullTimeAgain()
    {
        // Arrange — step 1 ran its five minutes out; a minute into step 2 the coach goes back to it.
        // Resumed with no time left it would bounce straight forward again.
        StubRun(AutoAdvancingOn(Step2Id, elapsedSeconds: 60));

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step1Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step1Id);
        result.CurrentItemStartedAt.Should().Be(Now);
        result.CurrentItemPausedElapsedSeconds.Should().Be(0);
        result.Items.Single(i => i.PlanItemId == Step1Id).ActualElapsedSeconds.Should().Be(0);
        await _runRepository.Received(1).SaveChangesAsync();
        _analytics.CapturedEach(AnalyticsEventNames.PracticeRunStepChanged)
            .Should().ContainSingle().Which.Properties["direction"].Should().Be(RunStepDirection.Previous);
    }

    [Test]
    public async Task GoToAsync_BackToAStepLeftEarly_ResumesItsRecordedTime()
    {
        // Arrange — Next was tapped two minutes into step 1's five; 20 seconds later, Previous.
        var run = AutoAdvancingOn(Step2Id, elapsedSeconds: 20);
        Step(run, Step1Id).ActualElapsedSeconds = 120;
        StubRun(run);

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step1Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step1Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-120));
    }

    [Test]
    public async Task GoToAsync_WithAutoAdvanceOff_BackToAStepThatRanItsFullTime_ResumesItsRecordedTime()
    {
        // Arrange
        StubRun(RunningOn(Step2Id, elapsedSeconds: 60));

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step1Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step1Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-300));
    }

    [Test]
    public async Task AdvanceAsync_IntoAStepThatRanItsFullTime_PlaysItsFullTimeAgain()
    {
        // Arrange — back on step 1 after step 2 had run all ten minutes.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 30);
        Step(run, Step2Id).StartedAtUtc = Now.AddMinutes(-20);
        Step(run, Step2Id).ActualElapsedSeconds = 600;
        StubRun(run);

        // Act
        var result = await _sut.AdvanceAsync(EventId, Step1Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now);
        result.Items.Single(i => i.PlanItemId == Step2Id).ActualElapsedSeconds.Should().Be(0);
    }

    [Test]
    public async Task ReopenAsync_OnAStepThatRanItsFullTimeWithAStepAfter_PlaysItsFullTimeAgain()
    {
        // Arrange — End Session was tapped with step 2 100 seconds over its ten minutes.
        var run = CompletedOn(Step2Id, elapsedSeconds: 700);
        run.AutoAdvance = true;
        StubRun(run);

        // Act
        var result = await _sut.ReopenAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now);
    }

    [Test]
    public async Task ReopenAsync_OnTheLastStep_ResumesItsTimeIntoOvertime()
    {
        // Arrange — the session ran off the end of the plan 100 seconds over step 3's fifteen minutes.
        var run = CompletedOn(Step3Id, elapsedSeconds: 1000);
        run.AutoAdvance = true;
        StubRun(run);

        // Act
        var result = await _sut.ReopenAsync(EventId, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step3Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-1000));
    }

    // ---------- Arming never reaches back in time ----------

    [Test]
    public async Task ResumeAsync_PausedInOvertime_MovesOnAtTheResume()
    {
        // Arrange — paused 100 seconds past step 1's five minutes, with auto-advance on.
        var run = PausedOn(Step1Id, elapsedSeconds: 400);
        run.AutoAdvance = true;
        StubRun(run);

        // Act
        var result = await _sut.ResumeAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now);

        var left = result.Items.Single(i => i.PlanItemId == Step1Id);
        left.ActualElapsedSeconds.Should().Be(400);
        left.CompletedAt.Should().Be(Now);

        await _runRepository.Received(1).SaveChangesAsync();
        var step = _analytics.CapturedEach(AnalyticsEventNames.PracticeRunStepChanged).Should().ContainSingle().Subject;
        step.Properties["direction"].Should().Be(RunStepDirection.Auto);
        step.Properties["elapsed_seconds"].Should().Be(400);
    }

    [Test]
    public async Task ResumeAsync_AQueuedTapWhoseStepRanOutSince_ReturnsTheRunMovedOn()
    {
        // Arrange — paused five minutes ago 200 seconds into step 1's 300; the resume was tapped
        // three minutes ago on a phone with no signal, so step 1 ran out 80 seconds ago.
        var run = PausedOn(Step1Id, elapsedSeconds: 200);
        run.AutoAdvance = true;
        run.UpdatedAt = Now.AddMinutes(-5);
        StubRun(run);

        // Act
        var result = await _sut.ResumeAsync(EventId, CreatorId, occurredAt: Now.AddMinutes(-3));

        // Assert
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-80));
        result.Items.Single(i => i.PlanItemId == Step1Id).CompletedAt.Should().Be(Now.AddSeconds(-80));
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task ResumeAsync_PausedWithTimeLeft_ResumesTheStep()
    {
        // Arrange
        var run = PausedOn(Step1Id, elapsedSeconds: 200);
        run.AutoAdvance = true;
        StubRun(run);

        // Act
        var result = await _sut.ResumeAsync(EventId, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step1Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-200));
    }

    [Test]
    public async Task ResumeAsync_PausedInOvertimeOnTheLastStep_ResumesItRunningOver()
    {
        // Arrange
        var run = PausedOn(Step3Id, elapsedSeconds: 1000);
        run.AutoAdvance = true;
        StubRun(run);

        // Act
        var result = await _sut.ResumeAsync(EventId, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step3Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-1000));
    }

    [Test]
    public async Task ResumeAsync_WithAutoAdvanceOff_ResumesTheStepInOvertime()
    {
        // Arrange
        StubRun(PausedOn(Step1Id, elapsedSeconds: 400));

        // Act
        var result = await _sut.ResumeAsync(EventId, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step1Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-400));
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
    public async Task SetAutoAdvanceAsync_OnWithTheStepInOvertime_MovesOnNow()
    {
        // Arrange — step 1 has run 100 seconds over when the coach switches auto-advance on.
        // Moving on from when its time ran out would start step 2 with the overtime spent.
        StubRun(RunningOn(Step1Id, elapsedSeconds: 400));

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: true, CreatorId);

        // Assert
        result.AutoAdvance.Should().BeTrue();
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now);

        var left = result.Items.Single(i => i.PlanItemId == Step1Id);
        left.ActualElapsedSeconds.Should().Be(400);
        left.CompletedAt.Should().Be(Now);

        await _runRepository.Received(1).SaveChangesAsync();
        var step = _analytics.CapturedEach(AnalyticsEventNames.PracticeRunStepChanged).Should().ContainSingle().Subject;
        step.UserId.Should().Be(CreatorId);
        step.Properties["direction"].Should().Be(RunStepDirection.Auto);
        step.Properties["elapsed_seconds"].Should().Be(400);
    }

    [Test]
    public async Task SetAutoAdvanceAsync_OnWithTimeLeft_LeavesTheStepRunning()
    {
        // Arrange
        StubRun(RunningOn(Step1Id, elapsedSeconds: 200));

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: true, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step1Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-200));
    }

    [Test]
    public async Task SetAutoAdvanceAsync_OnWhilePausedInOvertime_LeavesItPausedOnTheStep()
    {
        // Arrange — it moves on when resumed, not while paused.
        StubRun(PausedOn(Step1Id, elapsedSeconds: 400));

        // Act
        var result = await _sut.SetAutoAdvanceAsync(EventId, enabled: true, CreatorId);

        // Assert
        result.AutoAdvance.Should().BeTrue();
        result.Status.Should().Be(RunStatus.Paused);
        result.CurrentItemId.Should().Be(Step1Id);
    }

    [Test]
    public async Task SetAutoAdvanceAsync_OnWhilePausedInOvertime_ThenResumed_MovesOnAtTheResumeTap()
    {
        // Arrange — paused 100 seconds past step 1's five minutes, by hand.
        StubRun(PausedOn(Step1Id, elapsedSeconds: 400));

        // Act — switched on while paused; resumed two minutes later, by a tap queued 30 seconds
        // before it reached the server
        var switchedOn = await _sut.SetAutoAdvanceAsync(EventId, enabled: true, CreatorId);
        AdvanceTime(TimeSpan.FromMinutes(2));
        var resumeTapped = Now.AddSeconds(-30);
        var resumed = await _sut.ResumeAsync(EventId, CreatorId, occurredAt: resumeTapped);

        // Assert — switching on only set it; the resume moved on, the next step running from the tap
        switchedOn.Status.Should().Be(RunStatus.Paused);
        switchedOn.CurrentItemId.Should().Be(Step1Id);
        switchedOn.CurrentItemPausedElapsedSeconds.Should().Be(400);

        resumed.Status.Should().Be(RunStatus.Running);
        resumed.CurrentItemId.Should().Be(Step2Id);
        resumed.CurrentItemStartedAt.Should().Be(resumeTapped);

        var left = resumed.Items.Single(i => i.PlanItemId == Step1Id);
        left.ActualElapsedSeconds.Should().Be(400);
        left.CompletedAt.Should().Be(resumeTapped);
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
