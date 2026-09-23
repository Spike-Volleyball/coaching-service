using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Models.Evaluation;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class EvaluationAccess(IClubsGrpcClient clubs, IEvaluationGroupRepository groups) : IEvaluationAccess
{
    private const string SessionNotFound = "Evaluation session not found";

    public Task<bool> MayReadClubAsync(Guid clubId, Guid userId) =>
        clubs.IsClubStaffAsync(userId, clubId);

    public Task<bool> MayEvaluateInClubAsync(Guid clubId, Guid userId) =>
        clubs.CanGiveFeedbackInClubAsync(userId, clubId);

    public async Task<bool> MayReadPlanAsync(EvaluationPlan plan, Guid userId) =>
        plan.CreatedByUserId == userId
        || (plan.ClubId is { } clubId && await MayReadClubAsync(clubId, userId));

    public async Task<EvaluationSession> EnsureMayReadSessionAsync(EvaluationSession? session, Guid userId) =>
        await MayReadSessionAsync(session, userId) ? session! : throw new EntityNotFoundException(SessionNotFound);

    public async Task<bool> MayReadSessionAsync(EvaluationSession? session, Guid userId) =>
        session is { IsDeleted: false }
        && (session.CoachUserId == userId
            || await groups.IsEvaluatorAsync(session.Id, userId)
            || await MayReadClubAsync(session.ClubId, userId));

    public async Task<bool> MayReadExerciseAsync(EvaluationExercise exercise, Guid userId) =>
        exercise.ClubId is not { } clubId
        || exercise.CreatedByUserId == userId
        || await MayReadClubAsync(clubId, userId);
}
