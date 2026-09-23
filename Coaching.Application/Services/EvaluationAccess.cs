using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Models.Evaluation;

namespace Coaching.Application.Services;

public class EvaluationAccess(IClubsGrpcClient clubs) : IEvaluationAccess
{
    public Task<bool> MayReadClubAsync(Guid clubId, Guid userId) =>
        clubs.IsClubStaffAsync(userId, clubId);

    public async Task<bool> MayReadPlanAsync(EvaluationPlan plan, Guid userId) =>
        plan.CreatedByUserId == userId
        || (plan.ClubId is { } clubId && await MayReadClubAsync(clubId, userId));
}
