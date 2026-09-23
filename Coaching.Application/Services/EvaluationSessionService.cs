using AutoMapper;
using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using Shared.Enums;
using Shared.Exceptions;
using Shared.Services.Analytics;

namespace Coaching.Application.Services;

public class EvaluationSessionService(
    IEvaluationSessionRepository sessionRepository,
    IEvaluationParticipantRepository participantRepository,
    IEvaluationPlanRepository planRepository,
    IAnalyticsCapture analytics,
    IEvaluationAccess access,
    IMapper mapper) : IEvaluationSessionService
{
    public async Task<EvaluationSessionDto> CreateAsync(CreateEvaluationSessionDto request, Guid coachUserId)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new BadRequestException("Session title is required", ErrorCodeEnum.ValidationError);

        if (!await access.MayEvaluateInClubAsync(request.ClubId, coachUserId))
            throw new ForbiddenException("Only this club's coaches can run evaluation sessions in it");

        if (request.EvaluationPlanId is { } planId)
            await EnsureSessionMayUsePlanAsync(planId, request.ClubId, coachUserId);

        var session = new EvaluationSession
        {
            ClubId = request.ClubId,
            EventId = request.EventId,
            CoachUserId = coachUserId,
            EvaluationPlanId = request.EvaluationPlanId,
            Title = request.Title,
            Description = request.Description,
            Status = EvaluationSessionStatus.Draft
        };

        sessionRepository.Add(session);
        await sessionRepository.SaveChangesAsync();

        // No participant count: players arrive on a later call, so every session is created empty.
        analytics.Capture(coachUserId, AnalyticsEventNames.EvaluationSessionCreated, new Dictionary<string, object?>
        {
            ["session_id"] = session.Id,
            ["club_id"] = session.ClubId,
            ["event_id"] = session.EventId,
            ["has_plan"] = session.EvaluationPlanId.HasValue
        });

        return await FindAsync(session.Id) ?? throw new Exception("Failed to retrieve created session");
    }

    public async Task<EvaluationSessionDto> GetByIdForUserAsync(Guid id, Guid userId)
    {
        var session = await access.EnsureMayReadSessionAsync(
            await sessionRepository.GetByIdWithParticipantsAsync(id), userId);
        return mapper.Map<EvaluationSessionDto>(session);
    }

    public async Task<IEnumerable<EvaluationSessionDto>> GetByClubIdAsync(Guid clubId, Guid userId, int page = 1, int pageSize = 20)
    {
        // Someone without standing sees what a club with no sessions shows.
        if (!await access.MayReadClubAsync(clubId, userId))
            return [];

        var sessions = await sessionRepository.GetByClubIdAsync(clubId, page, pageSize);
        return mapper.Map<IEnumerable<EvaluationSessionDto>>(sessions);
    }

    public async Task<IEnumerable<EvaluationSessionDto>> GetMySessionsAsync(Guid coachUserId, int page = 1, int pageSize = 20)
    {
        var sessions = await sessionRepository.GetByCoachUserIdAsync(coachUserId, page, pageSize);
        return mapper.Map<IEnumerable<EvaluationSessionDto>>(sessions);
    }

    public async Task<EvaluationSessionDto> UpdateAsync(Guid id, UpdateEvaluationSessionDto request, Guid userId)
    {
        var session = await sessionRepository.GetByIdAsync(id);
        if (session == null)
            throw new EntityNotFoundException("Evaluation session not found");

        if (session.CoachUserId != userId)
            throw new ForbiddenException("Only the session coach can update this session");

        // A session moves only through start, pause, resume and complete: those build the scores
        // the run screen fills and stamp the times. Setting a status here skipped all of that, and
        // walking a started session back to Draft was a way round the plan rule below.
        if (request.Status is { } status && status != session.Status)
            throw new ValidationException("status", "USE_SESSION_ACTIONS",
                "A session is started, paused, resumed and completed through its own actions");

        if (request.EvaluationPlanId is { } planId && planId != session.EvaluationPlanId)
        {
            // Starting builds a score for every player and every exercise of the plan, so a plan
            // swapped under a started session would leave those scores on exercises it lacks.
            if (session.Status != EvaluationSessionStatus.Draft)
                throw new ValidationException("evaluationPlanId", "SESSION_STARTED",
                    "A session's plan can only change before the session starts");

            await EnsureSessionMayUsePlanAsync(planId, session.ClubId, userId);
            session.EvaluationPlanId = planId;
        }

        if (request.Title != null) session.Title = request.Title;
        if (request.Description != null) session.Description = request.Description;

        sessionRepository.Update(session);
        await sessionRepository.SaveChangesAsync();

        return await FindAsync(id) ?? throw new Exception("Failed to retrieve session");
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var session = await sessionRepository.GetByIdAsync(id);
        if (session == null)
            throw new EntityNotFoundException("Evaluation session not found");

        if (session.CoachUserId != userId)
            throw new ForbiddenException("Only the session coach can delete this session");

        session.IsDeleted = true;
        sessionRepository.Update(session);
        await sessionRepository.SaveChangesAsync();
    }

    public async Task<EvaluationSessionDto> AddParticipantsAsync(Guid sessionId, AddParticipantsDto request, Guid userId)
    {
        var session = await sessionRepository.GetByIdWithParticipantsAsync(sessionId);
        if (session == null)
            throw new EntityNotFoundException("Evaluation session not found");

        if (session.CoachUserId != userId)
            throw new ForbiddenException("Only the session coach can add participants");

        foreach (var playerId in request.PlayerIds)
        {
            // Skip if already a participant
            var existing = await participantRepository.GetBySessionAndPlayerAsync(sessionId, playerId);
            if (existing != null) continue;

            var participant = new EvaluationParticipant
            {
                EvaluationSessionId = sessionId,
                PlayerId = playerId,
                Source = request.Source
            };
            participantRepository.Add(participant);
        }
        await participantRepository.SaveChangesAsync();

        return await FindAsync(sessionId) ?? throw new Exception("Failed to retrieve session");
    }

    public async Task<EvaluationSessionDto> RemoveParticipantAsync(Guid sessionId, Guid participantId, Guid userId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session == null)
            throw new EntityNotFoundException("Evaluation session not found");

        if (session.CoachUserId != userId)
            throw new ForbiddenException("Only the session coach can remove participants");

        var participant = await participantRepository.GetByIdAsync(participantId);
        if (participant == null || participant.EvaluationSessionId != sessionId)
            throw new EntityNotFoundException("Participant not found");

        participant.IsDeleted = true;
        participantRepository.Update(participant);
        await participantRepository.SaveChangesAsync();

        return await FindAsync(sessionId) ?? throw new Exception("Failed to retrieve session");
    }

    /// <summary>
    /// A session may run its coach's own plan, or one of its club's plans the coach may read. Any
    /// other plan — deleted, unreadable, another club's — answers as a missing one does, so the
    /// request never confirms that a plan id is real.
    /// </summary>
    private async Task EnsureSessionMayUsePlanAsync(Guid planId, Guid sessionClubId, Guid coachUserId)
    {
        var plan = await planRepository.GetByIdAsync(planId);
        var usable = plan is { IsDeleted: false }
            && (plan.CreatedByUserId == coachUserId
                || (plan.ClubId == sessionClubId && await access.MayReadPlanAsync(plan, coachUserId)));

        if (!usable)
            throw new EntityNotFoundException("Evaluation plan not found");
    }

    /// <summary>The session a write just touched, for its answer: the writer is its coach.</summary>
    private async Task<EvaluationSessionDto?> FindAsync(Guid id)
    {
        var session = await sessionRepository.GetByIdWithParticipantsAsync(id);
        return session == null ? null : mapper.Map<EvaluationSessionDto>(session);
    }
}
