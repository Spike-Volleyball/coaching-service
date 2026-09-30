using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;

namespace Coaching.Application.Services;

/// <summary>
/// Auto-advance's one rule. A locked phone runs no code when a step's time is up, so the server
/// moves the run on: the sweep does it for runs nobody is reading, and every read and control of a
/// run does it first, so none reads or acts on a step the run has already left. The clients run
/// the same rule to flip their screens at 0:00; the server's copy is the one that counts.
/// </summary>
public static class RunAutoAdvance
{
    /// <summary>
    /// Moves <paramref name="run"/> on through every step whose planned time was up by
    /// <paramref name="now"/>. Each finishes at its planned end, played for exactly its planned
    /// time, and the next starts at that instant rather than now — so a run caught up late, even
    /// across several steps, reads as if it had moved on on time. A step entered again resumes the
    /// time it already has. Only a Running run set to auto-advance moves; a step with no planned
    /// time stops it, and the last step never finishes by itself: it runs into overtime for a
    /// coach to finish.
    /// </summary>
    /// <returns>The steps moved through, in order; empty when the run was not due.</returns>
    public static IReadOnlyList<AutoAdvancedStep> CatchUp(TrainingPlanRun run, DateTime now)
    {
        if (!run.AutoAdvance)
            return [];

        var steps = RunSteps.InOrder(run);
        var current = steps.FindIndex(s => s.PlanItemId == run.CurrentItemId);
        var moved = new List<AutoAdvancedStep>();

        // By position, never by looking the current step up again, so each pass is a step further
        // on and the loop ends with the plan whatever the run's rows hold.
        while (current >= 0 && current < steps.Count - 1
               && run is { Status: RunStatus.Running, CurrentItemStartedAtUtc: { } started })
        {
            var step = steps[current];
            var planned = step.PlannedDurationSeconds;
            if (planned <= 0 || RunSteps.ElapsedSeconds(run, now) < planned)
                break;

            var due = started.AddSeconds(planned);
            var next = steps[++current];
            RunSteps.Finish(step, planned, due);
            RunSteps.Enter(run, next, due);
            moved.Add(new AutoAdvancedStep(step, next, due));
        }

        return moved;
    }
}

/// <summary>A step auto-advance moved through: <paramref name="From"/> finished, and <paramref name="To"/> entered, at <paramref name="At"/>.</summary>
public sealed record AutoAdvancedStep(TrainingPlanRunItem From, TrainingPlanRunItem To, DateTime At);
