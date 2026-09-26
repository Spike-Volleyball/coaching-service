using AutoMapper;
using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using Microsoft.EntityFrameworkCore;
using Shared.DTOs.Errors;
using Shared.Enums;
using Shared.Exceptions;
using Shared.Services.Analytics;

namespace Coaching.Application.Services;

public class EvaluationSessionService(
    IEvaluationSessionRepository sessionRepository,
    IEvaluationParticipantRepository participantRepository,
    IEvaluationPlanRepository planRepository,
    IEventsGrpcClient eventsClient,
    IClubsGrpcClient clubsClient,
    IAnalyticsCapture analytics,
    IEvaluationAccess access,
    IEvaluationPeople people,
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
        return await ToDtoAsync(session);
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

        EnsureRosterMayChange(session);
        await EnsureOnRosterAsync(session, request.PlayerIds);

        // (EvaluationSessionId, PlayerId) is uniquely indexed and removal only soft-deletes, so a
        // player added back gets their row back; inserting a second would throw 23505.
        var rows = await participantRepository.Query()
            .Where(p => p.EvaluationSessionId == sessionId)
            .ToDictionaryAsync(p => p.PlayerId);

        foreach (var playerId in request.PlayerIds.Distinct())
        {
            if (!rows.TryGetValue(playerId, out var row))
            {
                participantRepository.Add(new EvaluationParticipant
                {
                    EvaluationSessionId = sessionId,
                    PlayerId = playerId,
                    Source = request.Source
                });
            }
            else if (row.IsDeleted)
            {
                row.IsDeleted = false;
                row.Source = request.Source;
            }
        }
        await participantRepository.SaveChangesAsync();

        return await FindAsync(sessionId) ?? throw new Exception("Failed to retrieve session");
    }

    public async Task<EvaluationSessionDto> RemoveParticipantAsync(Guid sessionId, Guid participantId, Guid userId)
    {
        var session = await sessionRepository.GetByIdWithParticipantsAsync(sessionId);
        if (session == null)
            throw new EntityNotFoundException("Evaluation session not found");

        if (session.CoachUserId != userId)
            throw new ForbiddenException("Only the session coach can remove participants");

        EnsureRosterMayChange(session);

        var participant = session.Participants.FirstOrDefault(p => p.Id == participantId)
            ?? throw new EntityNotFoundException("Participant not found");

        // Out of the session is out of its groups: a group still holding them is a seat the start
        // would count and nobody could fill.
        participant.IsDeleted = true;
        foreach (var seat in session.Groups.SelectMany(g => g.Players).Where(p => p.PlayerId == participant.PlayerId))
            seat.IsDeleted = true;

        await participantRepository.SaveChangesAsync();

        return await FindAsync(sessionId) ?? throw new Exception("Failed to retrieve session");
    }

    /// <summary>
    /// The start builds an evaluation and a score for every player then in the session, so a player
    /// added afterwards has nothing to be scored into, and one removed leaves scores behind.
    /// </summary>
    private static void EnsureRosterMayChange(EvaluationSession session)
    {
        if (session.Status != EvaluationSessionStatus.Draft)
            throw new BadRequestException(
                "Players can only be added to or removed from a session before it starts", ErrorCodeEnum.ValidationError);
    }

    /// <summary>
    /// A session evaluates the players of the event it is run at, or of its club when it has none;
    /// anyone else used to be taken, and would have been evaluated against a club they are not in.
    /// </summary>
    private async Task EnsureOnRosterAsync(EvaluationSession session, IReadOnlyList<Guid> playerIds)
    {
        var roster = session.EventId is { } eventId
            ? await eventsClient.GetEventParticipantIdsAsync(eventId, playerIds)
            : await clubsClient.GetClubMemberIdsAsync(session.ClubId);

        var strangers = playerIds
            .Select((playerId, index) => (playerId, index))
            .Where(p => !roster.Contains(p.playerId))
            .Select(p => new FieldError($"playerIds[{p.index}]", "NOT_ON_ROSTER",
                session.EventId.HasValue ? "This player is not on the event" : "This player is not in the club"))
            .ToList();

        if (strangers.Count > 0)
            throw new ValidationException("Only the session's own players can be evaluated in it", strangers);
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
        return session == null ? null : await ToDtoAsync(session);
    }

    private async Task<EvaluationSessionDto> ToDtoAsync(EvaluationSession session)
    {
        var dto = mapper.Map<EvaluationSessionDto>(session);
        await people.FillAsync(dto.Groups);
        return dto;
    }
}
