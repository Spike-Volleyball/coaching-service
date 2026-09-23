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

        // Validate plan exists if provided
        if (request.EvaluationPlanId.HasValue)
        {
            var plan = await planRepository.GetByIdAsync(request.EvaluationPlanId.Value);
            if (plan == null)
                throw new EntityNotFoundException("Evaluation plan not found");
        }

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

        if (request.Title != null) session.Title = request.Title;
        if (request.Description != null) session.Description = request.Description;
        if (request.EvaluationPlanId.HasValue) session.EvaluationPlanId = request.EvaluationPlanId;
        if (request.Status.HasValue) session.Status = request.Status.Value;

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

    /// <summary>The session a write just touched, for its answer: the writer is its coach.</summary>
    private async Task<EvaluationSessionDto?> FindAsync(Guid id)
    {
        var session = await sessionRepository.GetByIdWithParticipantsAsync(id);
        return session == null ? null : mapper.Map<EvaluationSessionDto>(session);
    }
}
