namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// Event context returned from events-service for authorization decisions.
/// </summary>
public record EventContext(
    string EventType,
    string ContextType,
    Guid? ContextId);

/// <summary>
/// What a feedback row says about the session it was given at: enough to print the card without
/// the client fetching the event itself.
/// </summary>
public record EventInfo(
    Guid Id,
    string Name,
    DateTime StartTime,
    string Type);

/// <summary>
/// gRPC client for authorization checks and event summaries against events-service.
/// </summary>
public interface IEventsGrpcClient
{
    /// <summary>
    /// Check if a user is an admin (organizer/co-organizer) of an event.
    /// </summary>
    Task<bool> IsEventAdminAsync(Guid eventId, Guid userId);

    /// <summary>
    /// Check if a user is a participant of an event and whether the event exists.
    /// </summary>
    Task<(bool IsParticipant, bool EventExists)> IsEventParticipantAsync(Guid eventId, Guid userId);

    /// <summary>
    /// The user ids on an event's roster. One call answers for every recipient a screen asks
    /// about, which is what the feedback can-create batch relies on. The roster is cached, so the
    /// caller names who it is asking about: a cached copy missing any of them may predate their
    /// invitation, and is re-read rather than trusted.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetEventParticipantIdsAsync(Guid eventId, IReadOnlyCollection<Guid> askingAbout);

    /// <summary>
    /// Get the context of an event (type, context type, context ID) for authorization.
    /// Returns null if the event does not exist.
    /// </summary>
    Task<EventContext?> GetEventContextAsync(Guid eventId);

    /// <summary>
    /// Name, start and type of every live event among these ids, keyed by id. One call answers
    /// for a whole page of feedback rows; an id that names no live event is simply absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EventInfo>> GetEventInfoAsync(IReadOnlyCollection<Guid> eventIds);
}
