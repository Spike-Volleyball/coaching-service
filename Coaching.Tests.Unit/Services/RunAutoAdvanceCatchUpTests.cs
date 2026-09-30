using Coaching.Application.Services;
using Coaching.Domain.Enums;
using FluentAssertions;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Auto-advance's one rule. A locked phone runs no code when a drill's time is up, so the server
/// moves the run on — at the moment the time was up, not whenever it next looks, so a run it catches
/// up late reads as if it had moved on on time. The last step never finishes by itself, and a step
/// with no planned time stops it. Steps of 5, 10 and 15 minutes.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RunAutoAdvanceCatchUpTests : RunServiceTestBase
{
    [Test]
    public void CatchUp_WhenTheStepsTimeIsUp_FinishesItAtItsPlannedEnd()
    {
        // Arrange — 20 seconds past the end of step 1's five minutes.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 320);

        // Act
        RunAutoAdvance.CatchUp(run, Now);

        // Assert
        var finished = Step(run, Step1Id);
        finished.ActualElapsedSeconds.Should().Be(300);
        finished.CompletedAtUtc.Should().Be(Now.AddSeconds(-20));
    }

    [Test]
    public void CatchUp_WhenTheStepsTimeIsUp_StartsTheNextStepWhenTheTimeWasUp()
    {
        // Arrange
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 320);
        var due = Now.AddSeconds(-20);

        // Act
        RunAutoAdvance.CatchUp(run, Now);

        // Assert
        run.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(Step2Id);
        run.CurrentItemStartedAtUtc.Should().Be(due, "the next step's clock starts where the last one's ran out, not at the tick");
        run.CurrentItemPausedElapsedSeconds.Should().Be(0);
        Step(run, Step2Id).StartedAtUtc.Should().Be(due);
    }

    [Test]
    public void CatchUp_WhenTheStepsTimeIsUp_ReturnsTheStepItMovedThrough()
    {
        // Arrange
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 320);

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().ContainSingle();
        moved[0].From.Should().BeSameAs(Step(run, Step1Id));
        moved[0].To.Should().BeSameAs(Step(run, Step2Id));
        moved[0].At.Should().Be(Now.AddSeconds(-20));
    }

    [Test]
    public void CatchUp_OnTheSecondTheTimeIsUp_MovesOn()
    {
        // Arrange
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 300);

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().ContainSingle().Which.At.Should().Be(Now);
        run.CurrentItemId.Should().Be(Step2Id);
    }

    [Test]
    public void CatchUp_UnderASecondBeforeTheTimeIsUp_LeavesTheStepRunning()
    {
        // Arrange
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 300);
        run.CurrentItemStartedAtUtc = Now.AddMilliseconds(-299_900);

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().BeEmpty();
        run.CurrentItemId.Should().Be(Step1Id);
        Step(run, Step1Id).CompletedAtUtc.Should().BeNull();
    }

    [Test]
    public void CatchUp_AfterSeveralStepsRanOut_MovesThroughEachAtItsOwnEnd()
    {
        // Arrange — nobody looked for a while: step 1's five minutes and step 2's ten both ran
        // out, and step 3 has been going 30 seconds.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 300 + 600 + 30);
        var step1Due = Now.AddSeconds(-630);
        var step2Due = Now.AddSeconds(-30);

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Select(m => (m.From.PlanItemId, m.To.PlanItemId, m.At)).Should().Equal(
            (Step1Id, Step2Id, step1Due),
            (Step2Id, Step3Id, step2Due));

        var step2 = Step(run, Step2Id);
        step2.StartedAtUtc.Should().Be(step1Due);
        step2.ActualElapsedSeconds.Should().Be(600);
        step2.CompletedAtUtc.Should().Be(step2Due);

        run.CurrentItemId.Should().Be(Step3Id);
        run.CurrentItemStartedAtUtc.Should().Be(step2Due);
    }

    [Test]
    public void CatchUp_OnTheLastStep_NeverFinishesIt()
    {
        // Arrange — step 3's fifteen minutes ran out long ago.
        var run = AutoAdvancingOn(Step3Id, elapsedSeconds: 2000);
        var started = run.CurrentItemStartedAtUtc;

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().BeEmpty();
        run.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(Step3Id);
        run.CurrentItemStartedAtUtc.Should().Be(started);
        Step(run, Step3Id).CompletedAtUtc.Should().BeNull();
    }

    [Test]
    public void CatchUp_IntoTheLastStep_LeavesItRunningIntoOvertime()
    {
        // Arrange — step 2 ran out, then so did step 3, 100 seconds ago.
        var run = AutoAdvancingOn(Step2Id, elapsedSeconds: 600 + 900 + 100);

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().ContainSingle().Which.To.PlanItemId.Should().Be(Step3Id);
        run.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(Step3Id);
        run.CurrentItemStartedAtUtc.Should().Be(Now.AddSeconds(-1000));
    }

    [Test]
    public void CatchUp_OnAStepWithNoPlannedTime_StopsThere()
    {
        // Arrange
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 5000);
        Step(run, Step1Id).PlannedDurationSeconds = 0;

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().BeEmpty();
        run.CurrentItemId.Should().Be(Step1Id);
    }

    [Test]
    public void CatchUp_IntoAStepWithNoPlannedTime_StopsOnIt()
    {
        // Arrange — step 1 ran out long enough ago for step 2 to have run out too, had it a time.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 5000);
        Step(run, Step2Id).PlannedDurationSeconds = 0;

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().ContainSingle().Which.To.PlanItemId.Should().Be(Step2Id);
        run.CurrentItemId.Should().Be(Step2Id);
    }

    [Test]
    public void CatchUp_WhilePaused_LeavesTheRunAlone()
    {
        // Arrange — paused past step 1's end.
        var run = PausedOn(Step1Id, elapsedSeconds: 400);
        run.AutoAdvance = true;

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().BeEmpty();
        run.Status.Should().Be(RunStatus.Paused);
        run.CurrentItemId.Should().Be(Step1Id);
        run.CurrentItemPausedElapsedSeconds.Should().Be(400);
    }

    [Test]
    public void CatchUp_WithAutoAdvanceOff_LeavesTheStepInOvertime()
    {
        // Arrange
        var run = RunningOn(Step1Id, elapsedSeconds: 400);

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().BeEmpty();
        run.CurrentItemId.Should().Be(Step1Id);
        Step(run, Step1Id).CompletedAtUtc.Should().BeNull();
    }

    [Test]
    public void CatchUp_OnAFinishedRun_LeavesItAlone()
    {
        // Arrange
        var run = CompletedOn(Step2Id, elapsedSeconds: 700);
        run.AutoAdvance = true;

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Should().BeEmpty();
        run.Status.Should().Be(RunStatus.Completed);
        run.CurrentItemId.Should().BeNull();
    }

    [Test]
    public void CatchUp_IntoAStepPlayedBefore_ResumesItsRecordedTime()
    {
        // Arrange — step 2 had been played 200 seconds before the coach went back to step 1,
        // whose time has now run out.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 310);
        var step2 = Step(run, Step2Id);
        var firstEntered = Now.AddMinutes(-20);
        step2.StartedAtUtc = firstEntered;
        step2.ActualElapsedSeconds = 200;
        step2.CompletedAtUtc = Now.AddMinutes(-15);
        var due = Now.AddSeconds(-10);

        // Act
        RunAutoAdvance.CatchUp(run, Now);

        // Assert
        run.CurrentItemId.Should().Be(Step2Id);
        run.CurrentItemStartedAtUtc.Should().Be(due.AddSeconds(-200));
        run.CurrentItemPausedElapsedSeconds.Should().Be(200);
        step2.StartedAtUtc.Should().Be(firstEntered, "a step keeps the moment it was first entered");
        step2.CompletedAtUtc.Should().BeNull();
    }

    [Test]
    public void CatchUp_IntoAStepPlayedToItsEndBefore_MovesStraightThroughIt()
    {
        // Arrange — step 2 had been played all ten minutes before the coach went back to step 1.
        // Entered again, it resumes with no time left, so its time is up the instant it starts.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 310);
        var step2 = Step(run, Step2Id);
        step2.StartedAtUtc = Now.AddMinutes(-20);
        step2.ActualElapsedSeconds = 600;
        step2.CompletedAtUtc = Now.AddMinutes(-10);
        var due = Now.AddSeconds(-10);

        // Act
        var moved = RunAutoAdvance.CatchUp(run, Now);

        // Assert
        moved.Select(m => (m.From.PlanItemId, m.To.PlanItemId, m.At)).Should().Equal(
            (Step1Id, Step2Id, due),
            (Step2Id, Step3Id, due));
        run.CurrentItemId.Should().Be(Step3Id);
        run.CurrentItemStartedAtUtc.Should().Be(due);
    }
}
