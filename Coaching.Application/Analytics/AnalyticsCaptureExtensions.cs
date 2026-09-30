using Coaching.Domain.Models.Templates;
using Shared.Services.Analytics;

namespace Coaching.Application.Analytics;

/// <summary>
/// The coaching events that are emitted from more than one call site. Every one of them belongs
/// after the save that made the fact true, never inside the transaction: a publish after the last
/// SaveChangesAsync rides the outbox and is dropped (SPI-6098), and the fact being recorded is
/// that the row is now in the database.
/// </summary>
public static class AnalyticsCaptureExtensions
{
    /// <summary>
    /// A plan a coach can run, whether it was written standalone or straight onto an event.
    /// An event plan carries no club of its own — the club is the event's, and the join is
    /// <c>event_id</c> against events-service's own <c>event_created</c>.
    /// </summary>
    public static void CaptureTrainingPlanCreated(
        this IAnalyticsCapture analytics,
        TrainingPlan plan,
        Guid userId,
        int itemCount) =>
        analytics.Capture(userId, AnalyticsEventNames.TrainingPlanCreated, new Dictionary<string, object?>
        {
            ["plan_id"] = plan.Id,
            ["club_id"] = plan.ClubId,
            ["event_id"] = plan.EventId,
            ["from_template"] = plan.SourceTemplateId.HasValue,
            ["item_count"] = itemCount
        });

    /// <summary>
    /// The run reaching Completed, which the finish button does and advancing past the last item
    /// also does. Both paths come here so the practice that simply ran out of drills is counted.
    /// </summary>
    public static void CapturePracticeRunCompleted(
        this IAnalyticsCapture analytics,
        TrainingPlanRun run,
        Guid userId) =>
        analytics.Capture(userId, AnalyticsEventNames.PracticeRunCompleted, new Dictionary<string, object?>
        {
            ["event_id"] = run.EventId,
            ["plan_id"] = run.PlanId,
            ["duration_seconds"] = (int)((run.CompletedAtUtc - run.StartedAtUtc)?.TotalSeconds ?? 0),
            ["items_advanced"] = run.Items.Count(i => i.CompletedAtUtc != null)
        });

    /// <summary>
    /// The run moving from one step to another, by Next, Previous or a jump, with how long the step
    /// left was played against its plan. Running off the end of the plan is not a step change; that
    /// is <c>practice_run_completed</c>.
    /// </summary>
    public static void CapturePracticeRunStepChanged(
        this IAnalyticsCapture analytics,
        TrainingPlanRun run,
        TrainingPlanRunItem from,
        TrainingPlanRunItem to,
        string direction,
        int elapsedSeconds,
        Guid userId) =>
        analytics.Capture(userId, AnalyticsEventNames.PracticeRunStepChanged, new Dictionary<string, object?>
        {
            ["event_id"] = run.EventId,
            ["plan_id"] = run.PlanId,
            ["direction"] = direction,
            ["from_order"] = from.Order,
            ["to_order"] = to.Order,
            ["elapsed_seconds"] = elapsedSeconds,
            ["planned_seconds"] = from.PlannedDurationSeconds
        });

    /// <summary>
    /// One event per score submission, never one per metric: the two scoring endpoints both post
    /// a player's whole answer to one exercise, and per-metric rows would count keystrokes.
    /// </summary>
    public static void CapturePlayerEvaluationScored(
        this IAnalyticsCapture analytics,
        Guid sessionId,
        Guid evaluationId,
        Guid userId,
        int metricCount) =>
        analytics.Capture(userId, AnalyticsEventNames.PlayerEvaluationScored, new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["evaluation_id"] = evaluationId,
            ["metric_count"] = metricCount
        });

    /// <summary>
    /// One player's result being shown to them or taken back, by either of the two routes that
    /// name an evaluation. The session-level sharing toggle is not this event: it sets two
    /// session-wide flags and names no evaluation.
    /// </summary>
    public static void CapturePlayerEvaluationShared(
        this IAnalyticsCapture analytics,
        Guid evaluationId,
        Guid sessionId,
        Guid userId,
        bool isShared) =>
        analytics.Capture(userId, AnalyticsEventNames.PlayerEvaluationShared, new Dictionary<string, object?>
        {
            ["evaluation_id"] = evaluationId,
            ["session_id"] = sessionId,
            ["is_shared"] = isShared
        });

    /// <summary>
    /// A drill kept, or let go. Both endpoints behind it are get-or-create, so this is called only
    /// where a row was really written — a repeat tap changes nothing and says nothing.
    /// </summary>
    public static void CaptureDrillSaved(
        this IAnalyticsCapture analytics,
        Guid drillId,
        Guid userId,
        string kind,
        bool isOn) =>
        analytics.Capture(userId, AnalyticsEventNames.DrillSaved, new Dictionary<string, object?>
        {
            ["drill_id"] = drillId,
            ["kind"] = kind,
            ["is_on"] = isOn
        });
}
