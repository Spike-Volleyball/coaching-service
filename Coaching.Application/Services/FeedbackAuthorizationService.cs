using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Services;
using Microsoft.Extensions.Logging;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Application.Services;

/// <summary>
/// Authorization rules for feedback creation:
///
/// RULE 1: Event-linked feedback (EventId is set)
///   - Event type MUST be: TrainingSession, Evaluation, Trial, or Match
///   - Recipient MUST be a participant of the event
///   - IF event has ContextType=Club: user MUST be able to give feedback in that club
///   - IF event has ContextType=Team/Group: user MUST coach that unit — through a role on the
///     unit, or through a club role that reaches into it — OR be an event admin
///   - IF event has no context: user MUST be event admin (Owner/Admin/Organizer)
///
/// RULE 2: Standalone feedback in a team or group (no EventId, ContextType/ContextId are set)
///   - User MUST coach that unit, by the same test RULE 1 applies to a unit event
///   - Recipient MUST hold a row on that unit
///
/// RULE 3: Standalone feedback with club (no EventId, no unit, ClubId is set)
///   - User MUST be able to give feedback in that club
///   - Recipient MUST be an active member of that club
///
/// RULE 4: Standalone feedback without club (no EventId, no unit, no ClubId)
///   - Currently not supported — a club or a unit is required for standalone feedback
///
/// "Able to give feedback" is asked of clubs-service, which owns the role vocabulary. It is
/// wider than club staff: a team's or group's own Coach and AssistantCoach coach the people in
/// front of them, which is the whole reason the unit rules above exist.
///
/// Every rule splits into facts about the scope and the caller, which are the same for every
/// recipient, and one fact about the recipient: whether they are on the roster the scope names.
/// The scope is therefore resolved once, and each recipient is judged against it in memory —
/// a roster screen asking about twenty people costs the same round trips as asking about one.
/// </summary>
public class FeedbackAuthorizationService(
    IEventsGrpcClient eventsClient,
    IClubsGrpcClient clubsClient,
    ILogger<FeedbackAuthorizationService> logger) : IFeedbackAuthorizationService
{
    private static readonly HashSet<string> AllowedEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TrainingSession", "Evaluation", "Trial", "Match"
    };

    public async Task<Guid?> ValidateCreateAsync(CreateFeedbackDto request, Guid userId)
    {
        var scope = await ResolveAsync(FeedbackScope.Of(request), userId);
        var verdict = scope.Judge(request.RecipientUserId);
        if (!verdict.CanCreate)
            throw new ForbiddenException(verdict.Reason);
        return verdict.ResolvedClubId;
    }

    public async Task<IReadOnlyList<Guid>> GetEligibleRecipientsAsync(
        FeedbackScope scope, IReadOnlyCollection<Guid> recipientUserIds, Guid userId)
    {
        var recipients = recipientUserIds.Distinct().ToList();
        if (recipients.Count > IFeedbackAuthorizationService.MaxRecipientsPerBatch)
            throw new ValidationException("recipientUserIds", "TOO_MANY",
                $"Ask about at most {IFeedbackAuthorizationService.MaxRecipientsPerBatch} recipients per request");
        if (recipients.Count == 0)
            return [];

        var resolved = await ResolveAsync(scope, userId);
        var eligible = new List<Guid>(recipients.Count);
        var denials = new Dictionary<string, int>();
        foreach (var recipient in recipients)
        {
            var verdict = resolved.Judge(recipient);
            if (verdict.CanCreate)
                eligible.Add(recipient);
            else
                denials[verdict.Reason] = denials.GetValueOrDefault(verdict.Reason) + 1;
        }

        foreach (var (reason, count) in denials)
        {
            logger.LogInformation(
                "Feedback creation denied for user {UserId} towards {DeniedCount} of {RecipientCount} recipients: {Reason}",
                userId, count, recipients.Count, reason);
        }

        return eligible;
    }

    private async Task<ResolvedScope> ResolveAsync(FeedbackScope scope, Guid userId)
    {
        if (scope.EventId is { } eventId)
            return await ResolveEventLinkedAsync(eventId, userId);

        if (UnitOf(scope.ContextType, scope.ContextId) is { } unit)
            return await ResolveStandaloneUnitAsync(unit, userId);

        if (scope.ClubId is { } clubId)
            return await ResolveStandaloneClubAsync(clubId, userId);

        return ResolvedScope.Rejected(userId, "Either eventId or clubId must be provided");
    }

    private async Task<ResolvedScope> ResolveEventLinkedAsync(Guid eventId, Guid userId)
    {
        var eventContext = await eventsClient.GetEventContextAsync(eventId);
        if (eventContext == null)
            return ResolvedScope.Rejected(userId, "Event not found");

        if (!AllowedEventTypes.Contains(eventContext.EventType))
            return ResolvedScope.Rejected(userId,
                $"Feedback cannot be given on {eventContext.EventType} events. Allowed types: TrainingSession, Evaluation, Trial, Match");

        var participants = eventsClient.GetEventParticipantIdsAsync(eventId);
        var authority = ResolveEventAuthorityAsync(eventId, eventContext, userId);
        await Task.WhenAll(participants, authority);
        var roster = await participants;
        var (mayGive, deniedReason, resolvedClubId) = await authority;

        // A stranger to the session is refused as such before the caller's own standing is
        // mentioned; that is the order the rule states and the order the reasons read in.
        return new ResolvedScope(userId,
        [
            new Gate(roster.Contains, "The recipient is not a participant of this event"),
            Gate.ForCaller(mayGive, deniedReason)
        ], resolvedClubId);
    }

    private async Task<(bool MayGive, string DeniedReason, Guid? ResolvedClubId)> ResolveEventAuthorityAsync(
        Guid eventId, EventContext eventContext, Guid userId)
    {
        if (eventContext.ContextType == "Club" && eventContext.ContextId.HasValue)
        {
            var isCoach = await clubsClient.CanGiveFeedbackInClubAsync(userId, eventContext.ContextId.Value);
            return (isCoach, "Only coaches of this club can give feedback on club events", eventContext.ContextId);
        }

        // A team or group event is coached by that unit's coaches, whether or not they happen to
        // have organised this particular session.
        if (UnitOf(eventContext.ContextType, eventContext.ContextId) is { } unit)
        {
            var (coachesUnit, _) = await MayCoachUnitAsync(unit, userId);
            if (coachesUnit)
                return (true, string.Empty, null);
        }

        var isAdmin = await eventsClient.IsEventAdminAsync(eventId, userId);
        return (isAdmin, "Only event organizers and admins can give feedback on non-club events", null);
    }

    private async Task<ResolvedScope> ResolveStandaloneUnitAsync(UnitContext unit, Guid userId)
    {
        var (coachesUnit, clubId) = await MayCoachUnitAsync(unit, userId);
        if (!coachesUnit)
            return ResolvedScope.Rejected(userId, "Only coaches of this team or group can give feedback to its players");

        var members = await clubsClient.GetUnitMemberIdsAsync(unit.Type, unit.Id);
        return new ResolvedScope(userId,
            [new Gate(members.Contains, "The recipient is not a member of this team or group")],
            clubId);
    }

    private async Task<ResolvedScope> ResolveStandaloneClubAsync(Guid clubId, Guid userId)
    {
        var isCoach = await clubsClient.CanGiveFeedbackInClubAsync(userId, clubId);
        if (!isCoach)
            return ResolvedScope.Rejected(userId, "Only coaches can give standalone feedback to club members");

        var members = await clubsClient.GetClubMemberIdsAsync(clubId);
        return new ResolvedScope(userId,
            [new Gate(members.Contains, "The recipient is not a member of this club")],
            clubId);
    }

    /// <summary>
    /// May this user coach the people in one team or group, and which club owns it? A role on the
    /// unit answers first because it needs no second call; a club role that reaches into the unit
    /// is the fallback, and is why club staff need no row on every team they oversee.
    /// </summary>
    private async Task<(bool CoachesUnit, Guid? ClubId)> MayCoachUnitAsync(UnitContext unit, Guid userId)
    {
        var coachesUnit = clubsClient.CanGiveFeedbackInUnitAsync(userId, unit.Type, unit.Id);
        var clubId = await clubsClient.ResolveClubIdAsync(unit.Type, unit.Id);

        if (await coachesUnit)
            return (true, clubId);

        if (clubId is null)
            return (false, null);

        return (await clubsClient.CanGiveFeedbackInClubAsync(userId, clubId.Value), clubId);
    }

    private static UnitContext? UnitOf(ContextType? contextType, Guid? contextId) =>
        contextType is ContextType.Team or ContextType.Group
        && contextId is { } id
        && id != Guid.Empty
            ? new UnitContext(contextType.Value, id)
            : null;

    private static UnitContext? UnitOf(string? contextType, Guid? contextId) =>
        Enum.TryParse<ContextType>(contextType, ignoreCase: true, out var parsed)
            ? UnitOf(parsed, contextId)
            : null;

    private readonly record struct UnitContext(ContextType Type, Guid Id);

    private readonly record struct Verdict(bool CanCreate, string Reason, Guid? ResolvedClubId);

    /// <summary>
    /// One test a recipient must pass, with the reason given when they do not. A test about the
    /// caller admits everyone or no one; a test about the roster admits whoever is on it.
    /// </summary>
    private sealed record Gate(Func<Guid, bool> Admits, string Reason)
    {
        public static Gate ForCaller(bool mayGive, string deniedReason) => new(_ => mayGive, deniedReason);
    }

    /// <summary>
    /// A scope with every round trip already made: the gates a recipient passes through, in the
    /// order the rules state them. The self-check needs no round trip and comes first everywhere.
    /// </summary>
    private sealed record ResolvedScope(Guid CallerId, IReadOnlyList<Gate> Gates, Guid? ResolvedClubId)
    {
        public static ResolvedScope Rejected(Guid callerId, string reason) =>
            new(callerId, [Gate.ForCaller(false, reason)], null);

        public Verdict Judge(Guid recipientUserId)
        {
            if (recipientUserId == CallerId)
                return new Verdict(false, "You cannot give feedback to yourself", null);

            var refusedBy = Gates.FirstOrDefault(gate => !gate.Admits(recipientUserId));
            return refusedBy is null
                ? new Verdict(true, string.Empty, ResolvedClubId)
                : new Verdict(false, refusedBy.Reason, null);
        }
    }
}
