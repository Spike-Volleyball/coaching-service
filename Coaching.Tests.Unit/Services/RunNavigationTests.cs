using Coaching.Application.DTOs.Templates;
using Coaching.Domain.Enums;
using FluentAssertions;
using NSubstitute;
using Shared.Exceptions;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Moving through a session: Next, Previous, a jump, and back into a session ended by mistake.
/// On 09-28 Next tapped while paused left the next step paused, so its clock never ran again;
/// there was no way back to a step, and none back into a session once it was ended.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RunNavigationTests : RunServiceTestBase
{
    // ---------- Next ----------

    [Test]
    public async Task AdvanceAsync_WhilePaused_EntersTheNextStepRunning()
    {
        // Arrange — paused 90 seconds into step 1, three minutes ago.
        StubRun(PausedOn(Step1Id, elapsedSeconds: 90));
        AdvanceTime(TimeSpan.FromMinutes(3));

        // Act
        var result = await _sut.AdvanceAsync(EventId, Step1Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now);

        var left = result.Items.Single(i => i.PlanItemId == Step1Id);
        left.ActualElapsedSeconds.Should().Be(90, "the minutes spent paused were not spent on the drill");
        left.CompletedAt.Should().Be(Now);
    }

    [Test]
    public async Task AdvanceAsync_IntoAStepAlreadyPlayed_GoesToItAndResumesItsRecordedTime()
    {
        // Arrange — the coach went back to step 1 after playing 200 seconds of step 2.
        var run = RunningOn(Step1Id, elapsedSeconds: 30);
        var step2 = Step(run, Step2Id);
        var firstEntered = Now.AddMinutes(-10);
        step2.StartedAtUtc = firstEntered;
        step2.ActualElapsedSeconds = 200;
        step2.CompletedAtUtc = Now.AddMinutes(-1);
        StubRun(run);

        // Act
        var result = await _sut.AdvanceAsync(EventId, Step1Id, CreatorId);

        // Assert — the next step by order, not the first one never finished
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-200));

        var entered = result.Items.Single(i => i.PlanItemId == Step2Id);
        entered.CompletedAt.Should().BeNull();
        entered.StartedAt.Should().Be(firstEntered);
    }

    [Test]
    public async Task AdvanceAsync_FromTheLastStep_EndsTheStepAndTheRunAtTheSameInstant()
    {
        // Arrange — Resume session finds the step it reopens by that instant.
        StubRun(RunningOn(Step3Id, elapsedSeconds: 45));

        // Act
        var result = await _sut.AdvanceAsync(EventId, Step3Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Completed);
        result.Items.Single(i => i.PlanItemId == Step3Id).CompletedAt.Should().Be(result.CompletedAt);
    }

    // ---------- Previous and jumps ----------

    [Test]
    public async Task GoToAsync_ThePreviousStep_DiscardsTheStrayVisitAndResumesThePreviousStep()
    {
        // Arrange — Next was tapped too early; 20 seconds into step 2 the coach goes back.
        var run = RunningOn(Step2Id, elapsedSeconds: 20);
        var step1FirstEntered = Step(run, Step1Id).StartedAtUtc;
        StubRun(run);

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step1Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step1Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-300), "step 1 had been played for its 5 minutes");

        var resumed = result.Items.Single(i => i.PlanItemId == Step1Id);
        resumed.CompletedAt.Should().BeNull();
        resumed.StartedAt.Should().Be(step1FirstEntered);

        var discarded = result.Items.Single(i => i.PlanItemId == Step2Id);
        discarded.ActualElapsedSeconds.Should().Be(0);
        discarded.StartedAt.Should().BeNull();
        discarded.CompletedAt.Should().BeNull();

        await _runRepository.Received(1).SaveChangesAsync();
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Any<RunDto>());
    }

    [Test]
    public async Task GoToAsync_AStepAhead_FinishesTheStepLeftAndEntersTheTargetFresh()
    {
        // Arrange
        StubRun(RunningOn(Step1Id, elapsedSeconds: 60));

        // Act
        var result = await _sut.GoToAsync(EventId, Step1Id, Step3Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step3Id);
        result.CurrentItemStartedAt.Should().Be(Now);

        var left = result.Items.Single(i => i.PlanItemId == Step1Id);
        left.ActualElapsedSeconds.Should().Be(60);
        left.CompletedAt.Should().Be(Now);

        var skipped = result.Items.Single(i => i.PlanItemId == Step2Id);
        skipped.StartedAt.Should().BeNull();
        skipped.CompletedAt.Should().BeNull();

        result.Items.Single(i => i.PlanItemId == Step3Id).StartedAt.Should().Be(Now);
    }

    [Test]
    public async Task GoToAsync_WhilePaused_LeavesTheRunRunning()
    {
        // Arrange
        StubRun(PausedOn(Step2Id, elapsedSeconds: 45));

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step1Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step1Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-300));
    }

    [Test]
    public async Task GoToAsync_FromAStepThatIsNoLongerCurrent_ReturnsTheRunUnchanged()
    {
        // Arrange — a second tap, or a queued one, from a screen that has moved on.
        StubRun(RunningOn(Step2Id, elapsedSeconds: 20));

        // Act
        var result = await _sut.GoToAsync(EventId, Step1Id, Step3Id, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step2Id);
        result.CanControl.Should().BeTrue();
        await AssertNothingChangedAsync();
    }

    [Test]
    public async Task GoToAsync_ToAStepThatIsNotInTheRun_ReturnsTheRunUnchanged()
    {
        // Arrange
        StubRun(RunningOn(Step2Id, elapsedSeconds: 20));

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Guid.NewGuid(), CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step2Id);
        await AssertNothingChangedAsync();
    }

    [Test]
    public async Task GoToAsync_ToTheCurrentStep_ReturnsTheRunUnchanged()
    {
        // Arrange
        StubRun(PausedOn(Step2Id, elapsedSeconds: 20));

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step2Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Paused);
        await AssertNothingChangedAsync();
    }

    [Test]
    public async Task GoToAsync_OnAFinishedRun_ReturnsItUnchanged()
    {
        // Arrange — the way back into a finished session is Resume session, not a step.
        StubRun(CompletedOn(Step3Id, elapsedSeconds: 45));

        // Act
        var result = await _sut.GoToAsync(EventId, Step3Id, Step2Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Completed);
        await AssertNothingChangedAsync();
    }

    [Test]
    public async Task GoToAsync_EventAdminWhoDidNotCreateThePlan_MayGoBack()
    {
        // Arrange
        StubRun(RunningOn(Step2Id, elapsedSeconds: 20));
        _eventsGrpcClient.IsEventAdminAsync(EventId, OtherUserId).Returns(true);

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step1Id, OtherUserId);

        // Assert
        result.CurrentItemId.Should().Be(Step1Id);
    }

    [Test]
    public async Task GoToAsync_NeitherCreatorNorEventAdmin_ThrowsForbidden()
    {
        // Arrange
        StubRun(RunningOn(Step2Id, elapsedSeconds: 20));

        // Act
        var act = () => _sut.GoToAsync(EventId, Step2Id, Step1Id, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        await AssertNothingChangedAsync();
    }

    // ---------- Resume session ----------

    [Test]
    public async Task ReopenAsync_AfterNextOnTheLastStep_ResumesTheLastStepWhereItStopped()
    {
        // Arrange — the session ran off the end of the plan 45 seconds into step 3, two minutes ago.
        StubRun(CompletedOn(Step3Id, elapsedSeconds: 45));
        AdvanceTime(TimeSpan.FromMinutes(2));

        // Act
        var result = await _sut.ReopenAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CompletedAt.Should().BeNull();
        result.CurrentItemId.Should().Be(Step3Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-45));
        result.Items.Single(i => i.PlanItemId == Step3Id).CompletedAt.Should().BeNull();

        await _runRepository.Received(1).SaveChangesAsync();
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Any<RunDto>());
    }

    [Test]
    public async Task ReopenAsync_AfterEndSessionMidway_ResumesTheStepThatWasCurrent()
    {
        // Arrange — End Session tapped by mistake 100 seconds into step 2.
        StubRun(CompletedOn(Step2Id, elapsedSeconds: 100));

        // Act
        var result = await _sut.ReopenAsync(EventId, CreatorId);

        // Assert
        result.CurrentItemId.Should().Be(Step2Id);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-100));
        result.Items.Single(i => i.PlanItemId == Step1Id).CompletedAt.Should().NotBeNull();
        result.Items.Single(i => i.PlanItemId == Step3Id).StartedAt.Should().BeNull();
    }

    [Test]
    public async Task ReopenAsync_WhenNoStepEndedWithTheRun_ResumesTheLastStepThatStarted()
    {
        // Arrange — a run ended before this release stamped its step and itself with two clock
        // reads, so the two need not match.
        var run = CompletedOn(Step2Id, elapsedSeconds: 100);
        Step(run, Step2Id).CompletedAtUtc = Now.AddTicks(-7);
        StubRun(run);

        // Act
        var result = await _sut.ReopenAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step2Id);
    }

    [TestCase(RunStatus.Running)]
    [TestCase(RunStatus.Paused)]
    public async Task ReopenAsync_ARunThatIsNotFinished_ReturnsItUnchanged(RunStatus status)
    {
        // Arrange — a second tap of Resume session, after the first already reopened it.
        StubRun(status == RunStatus.Running
            ? RunningOn(Step2Id, elapsedSeconds: 20)
            : PausedOn(Step2Id, elapsedSeconds: 20));

        // Act
        var result = await _sut.ReopenAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(status);
        result.CurrentItemId.Should().Be(Step2Id);
        await AssertNothingChangedAsync();
    }

    [Test]
    public async Task ReopenAsync_NeitherCreatorNorEventAdmin_ThrowsForbidden()
    {
        // Arrange
        StubRun(CompletedOn(Step3Id, elapsedSeconds: 45));

        // Act
        var act = () => _sut.ReopenAsync(EventId, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        await AssertNothingChangedAsync();
    }

    [Test]
    public async Task ReopenAsync_NoRun_ThrowsNotFound()
    {
        // Arrange
        StubNoRun();

        // Act
        var act = () => _sut.ReopenAsync(EventId, CreatorId);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException>();
    }

    private async Task AssertNothingChangedAsync()
    {
        await _runRepository.DidNotReceive().SaveChangesAsync();
        await _broadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
    }
}
