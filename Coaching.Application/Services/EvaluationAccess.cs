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

    public async Task<bool> MayReadPlanAsync(EvaluationPlan plan, Guid userId) =>
        plan.CreatedByUserId == userId
        || (plan.ClubId is { } clubId && await MayReadClubAsync(clubId, userId));

    public async Task<EvaluationSession> EnsureMayReadSessionAsync(EvaluationSession? session, Guid userId)
    {
        if (session == null || session.IsDeleted || !await MayReadSessionAsync(session, userId))
            throw new EntityNotFoundException(SessionNotFound);
        return session;
    }

    public async Task<bool> MayReadExerciseAsync(EvaluationExercise exercise, Guid? userId) =>
        exercise.ClubId is not { } clubId
        || (userId is { } reader && (exercise.CreatedByUserId == reader || await MayReadClubAsync(clubId, reader)));

    private async Task<bool> MayReadSessionAsync(EvaluationSession session, Guid userId) =>
        session.CoachUserId == userId
        || await groups.IsEvaluatorAsync(session.Id, userId)
        || await MayReadClubAsync(session.ClubId, userId);
}
