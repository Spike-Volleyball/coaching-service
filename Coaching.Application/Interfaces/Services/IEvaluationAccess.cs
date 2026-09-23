using Coaching.Domain.Models.Evaluation;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// Who may read a club's evaluation material — the plans its players are assessed against. It is
/// coaching material rather than player-facing (a player reads their own results through their
/// evaluations), so the question is standing among the people who run or coach the club: the one
/// tactics boards ask, answered by <see cref="IClubsGrpcClient.IsClubStaffAsync"/>.
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
}
