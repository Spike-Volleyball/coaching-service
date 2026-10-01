using Coaching.Domain.Enums;
using FluentAssertions;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// A tap queued on a phone with no signal in the gym reaches the server late. Its clock math uses
/// when it was tapped — but never a moment before the run's last change, and never more than
/// 15 minutes back, whatever the phone's clock says.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RunTapTimeTests : RunServiceTestBase
{
    [Test]
    public async Task PauseAsync_AQueuedTap_StopsTheClockWhenItWasTapped()
    {
        // Arrange — two minutes into step 1; the pause was tapped 30 seconds ago.
        StubRun(RunningOn(Step1Id, elapsedSeconds: 120));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId, occurredAt: Now.AddSeconds(-30));

        // Assert
        result.CurrentItemPausedElapsedSeconds.Should().Be(90);
    }

    [Test]
    public async Task PauseAsync_ATapCarryingItsOwnOffset_IsTheSameInstant()
    {
        // Arrange
        StubRun(RunningOn(Step1Id, elapsedSeconds: 120));
        var tapped = new DateTimeOffset(Now.AddSeconds(-30)).ToOffset(TimeSpan.FromHours(2));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId, occurredAt: tapped);

        // Assert
        result.CurrentItemPausedElapsedSeconds.Should().Be(90);
    }

    [Test]
    public async Task PauseAsync_ATapFromBeforeTheRunsLastChange_CountsFromThatChange()
    {
        // Arrange — the step was entered a minute ago; a pause claiming five minutes ago cannot
        // stop a clock that was not running yet.
        StubRun(RunningOn(Step1Id, elapsedSeconds: 60));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId, occurredAt: Now.AddMinutes(-5));

        // Assert
        result.CurrentItemPausedElapsedSeconds.Should().Be(0);
    }

    [Test]
    public async Task PauseAsync_ATapFromLongAgo_CountsFromFifteenMinutesAgo()
    {
        // Arrange — an hour into step 3 (the plan overran); the tap claims 40 minutes ago.
        StubRun(RunningOn(Step3Id, elapsedSeconds: 3600));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId, occurredAt: Now.AddMinutes(-40));

        // Assert
        result.CurrentItemPausedElapsedSeconds.Should().Be(3600 - 15 * 60);
    }

    [Test]
    public async Task PauseAsync_ATapFromTheFuture_CountsAsNow()
    {
        // Arrange — a phone whose clock runs ahead.
        StubRun(RunningOn(Step1Id, elapsedSeconds: 120));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId, occurredAt: Now.AddMinutes(2));

        // Assert
        result.CurrentItemPausedElapsedSeconds.Should().Be(120);
    }

    [Test]
    public async Task ResumeAsync_AQueuedTap_RestartsTheClockWhenItWasTapped()
    {
        // Arrange — paused at 90 seconds two minutes ago; resume was tapped 20 seconds ago.
        var run = PausedOn(Step1Id, elapsedSeconds: 90);
        run.UpdatedAt = Now.AddMinutes(-2);
        StubRun(run);

        // Act
        var result = await _sut.ResumeAsync(EventId, CreatorId, occurredAt: Now.AddSeconds(-20));

        // Assert — 90 seconds played before the pause, and 20 since the tap
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-110));
    }

    [Test]
    public async Task AdvanceAsync_AQueuedTap_EndsTheStepAndStartsTheNextWhenItWasTapped()
    {
        // Arrange
        StubRun(RunningOn(Step1Id, elapsedSeconds: 120));
        var tapped = Now.AddSeconds(-30);

        // Act
        var result = await _sut.AdvanceAsync(EventId, Step1Id, CreatorId, occurredAt: tapped);

        // Assert
        var left = result.Items.Single(i => i.PlanItemId == Step1Id);
        left.ActualElapsedSeconds.Should().Be(90);
        left.CompletedAt.Should().Be(tapped);
        result.CurrentItemStartedAt.Should().Be(tapped);
        result.Items.Single(i => i.PlanItemId == Step2Id).StartedAt.Should().Be(tapped);
    }

    [Test]
    public async Task GoToAsync_AQueuedTap_ResumesThePreviousStepFromWhenItWasTapped()
    {
        // Arrange — 50 seconds into step 2; Previous was tapped 10 seconds ago.
        StubRun(RunningOn(Step2Id, elapsedSeconds: 50));

        // Act
        var result = await _sut.GoToAsync(EventId, Step2Id, Step1Id, CreatorId, occurredAt: Now.AddSeconds(-10));

        // Assert — step 1's 300 seconds, and the 10 since the tap
        result.CurrentItemStartedAt.Should().Be(Now.AddSeconds(-310));
    }

    [Test]
    public async Task CompleteAsync_AQueuedTap_EndsTheRunWhenItWasTapped()
    {
        // Arrange
        StubRun(RunningOn(Step2Id, elapsedSeconds: 120));
        var tapped = Now.AddSeconds(-60);

        // Act
        var result = await _sut.CompleteAsync(EventId, CreatorId, occurredAt: tapped);

        // Assert
        result.CompletedAt.Should().Be(tapped);
        result.Items.Single(i => i.PlanItemId == Step2Id).ActualElapsedSeconds.Should().Be(60);
    }

    [Test]
    public async Task ReopenAsync_AQueuedTap_ResumesTheStepFromWhenItWasTapped()
    {
        // Arrange — ended 100 seconds into step 2, three minutes ago; reopened a minute ago.
        StubRun(CompletedOn(Step2Id, elapsedSeconds: 100));
        AdvanceTime(TimeSpan.FromMinutes(3));

        // Act
        var result = await _sut.ReopenAsync(EventId, CreatorId, occurredAt: Now.AddMinutes(-1));

        // Assert
        result.CurrentItemStartedAt.Should().Be(Now.AddMinutes(-1).AddSeconds(-100));
    }
}
