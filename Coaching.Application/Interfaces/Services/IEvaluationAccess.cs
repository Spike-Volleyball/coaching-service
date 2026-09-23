using Coaching.Domain.Models.Evaluation;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// Who may read a club's evaluation material — the plans and exercises its players are assessed
/// against, and the sessions that apply them. It is coaching material rather than player-facing (a
/// player reads their own results through their evaluations), so the question is standing among the
/// people who run or coach the club: the one tactics boards ask, answered by
/// <see cref="IClubsGrpcClient.IsClubStaffAsync"/>, plus whoever the material itself names.
///
/// A reader who fails it is answered exactly as for something that is not there.
/// </summary>
public interface IEvaluationAccess
{
    /// <summary>Whether the user may see a club's evaluation material at all.</summary>
    Task<bool> MayReadClubAsync(Guid clubId, Guid userId);

    /// <summary>
    /// A plan's author may always read it; a club's plan is also its staff's. A personal plan (no
    /// club) is its author's alone.
    /// </summary>
    Task<bool> MayReadPlanAsync(EvaluationPlan plan, Guid userId);

    /// <summary>
    /// Whether the user may run evaluations in a club: open a session there, or evaluate one of its
    /// groups. Evaluating a player is appraising them, so this is the permission giving feedback in
    /// the club asks for (feedback.give, answered by
    /// <see cref="IClubsGrpcClient.CanGiveFeedbackInClubAsync"/>) rather than a new one.
    /// </summary>
    Task<bool> MayEvaluateInClubAsync(Guid clubId, Guid userId);

    /// <summary>
    /// Returns the session if the user may read it: its coach, an evaluator on one of its groups (who
    /// scores from the run screen, which reads the session and its scores), or its club's staff.
    /// Otherwise throws the not-found a missing session gives — and treats a deleted one as missing.
    /// </summary>
    Task<EvaluationSession> EnsureMayReadSessionAsync(EvaluationSession? session, Guid userId);

    /// <summary>The same answer as <see cref="EnsureMayReadSessionAsync"/>, as a yes or no: false for a missing or deleted session.</summary>
    Task<bool> MayReadSessionAsync(EvaluationSession? session, Guid userId);

    /// <summary>
    /// An exercise outside any club is the public library's, open to any signed-in reader; a
    /// club's is its author's and its staff's.
    /// </summary>
    Task<bool> MayReadExerciseAsync(EvaluationExercise exercise, Guid userId);
}
