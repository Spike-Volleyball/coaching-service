using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;

namespace Coaching.Application.Services;

/// <summary>
/// How a run moves between its steps, whoever moves it: a coach's tap or auto-advance. A step is
/// found by its plan item id, the same id as the run's CurrentItemId.
/// </summary>
public static class RunSteps
{
    public static List<TrainingPlanRunItem> InOrder(TrainingPlanRun run) =>
        run.Items.OrderBy(i => i.Order).ToList();

    /// <summary>
    /// Makes <paramref name="step"/> current and running from the time it already has, so a step
    /// played before picks up where it stopped and a fresh one starts at zero. It keeps the moment
    /// it was first entered.
    /// </summary>
    public static void Enter(TrainingPlanRun run, TrainingPlanRunItem step, DateTime at)
    {
        step.StartedAtUtc ??= at;
        step.CompletedAtUtc = null;

        run.Status = RunStatus.Running;
        run.CurrentItemId = step.PlanItemId;
        run.CurrentItemStartedAtUtc = at.AddSeconds(-step.ActualElapsedSeconds);
        run.CurrentItemPausedElapsedSeconds = step.ActualElapsedSeconds;
    }

    /// <summary>Leaves the current step forward, keeping the time it was played.</summary>
    public static void Finish(TrainingPlanRunItem step, int playedSeconds, DateTime at)
    {
        step.ActualElapsedSeconds = playedSeconds;
        step.CompletedAtUtc = at;
    }

    /// <summary>
    /// Leaves the current step backward: going back says the visit was a mistake, so the step
    /// returns to never having been played.
    /// </summary>
    public static void Discard(TrainingPlanRunItem step)
    {
        step.ActualElapsedSeconds = 0;
        step.StartedAtUtc = null;
        step.CompletedAtUtc = null;
    }

    public static void End(TrainingPlanRun run, DateTime at)
    {
        run.Status = RunStatus.Completed;
        run.CurrentItemId = null;
        run.CurrentItemStartedAtUtc = null;
        run.CompletedAtUtc = at;
    }

    public static int ElapsedSeconds(TrainingPlanRun run, DateTime at)
    {
        if (run.Status == RunStatus.Paused || run.CurrentItemStartedAtUtc == null)
            return run.CurrentItemPausedElapsedSeconds;

        var elapsed = (at - run.CurrentItemStartedAtUtc.Value).TotalSeconds;
        return elapsed < 0 ? 0 : (int)elapsed;
    }
}
