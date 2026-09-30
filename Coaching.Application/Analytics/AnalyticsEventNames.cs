namespace Coaching.Application.Analytics;

/// <summary>
/// The server-owned event names this service emits, from docs/analytics/events.md. They live here
/// rather than in shared's <c>AnalyticsEvents</c> only because the shared pin predates them — the
/// shipped string is the contract wherever the constant is declared, and is never renamed.
/// </summary>
public static class AnalyticsEventNames
{
    public const string TrainingPlanCreated = "training_plan_created";
    public const string PracticeRunStarted = "practice_run_started";
    public const string PracticeRunCompleted = "practice_run_completed";
    public const string PracticeRunStepChanged = "practice_run_step_changed";

    public const string EvaluationSessionCreated = "evaluation_session_created";
    public const string EvaluationSessionStarted = "evaluation_session_started";
    public const string EvaluationSessionCompleted = "evaluation_session_completed";
    public const string PlayerEvaluationScored = "player_evaluation_scored";
    public const string PlayerEvaluationShared = "player_evaluation_shared";

    public const string DrillCreated = "drill_created";
    public const string DrillImported = "drill_imported";
    public const string DrillUpdated = "drill_updated";
    public const string DrillSaved = "drill_saved";
}

/// <summary>
/// Which of the two ways a coach keeps a drill the <c>drill_saved</c> row is about. One event
/// with a discriminator rather than four names for like on, like off, bookmark on, bookmark off.
/// </summary>
public static class DrillSaveKind
{
    public const string Like = "like";
    public const string Bookmark = "bookmark";
}

/// <summary>
/// How a <c>practice_run_step_changed</c> moved: to the neighbouring step either way, or past it.
/// Counted by position in the run, so a gap in the plan's numbering is not a jump. Auto is the
/// run moving on by itself when a step's time was up, which nobody tapped.
/// </summary>
public static class RunStepDirection
{
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Jump = "jump";
    public const string Auto = "auto";
}
