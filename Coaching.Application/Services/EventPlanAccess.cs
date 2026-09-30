using Coaching.Application.Interfaces.Services;

namespace Coaching.Application.Services;

/// <summary>
/// Who may read an event's plan. Being at the session is one way in; being responsible for it is
/// the other — a club owner, a head coach covering it, the coach of the group it belongs to are
/// not participants, and all of them need the plan. IsEventParticipant stays a question about
/// attendance, so the second arm is asked separately rather than by widening "participant" for
/// every other caller. The plan's own GET and the drill read it vouches for both ask here.
/// </summary>
public static class EventPlanAccess
{
    public static async Task<(bool EventExists, bool MayRead)> StandingAsync(
        IEventsGrpcClient events, Guid eventId, Guid userId)
    {
        var (isParticipant, eventExists) = await events.IsEventParticipantAsync(eventId, userId);
        return (eventExists, eventExists && (isParticipant || await events.IsEventAdminAsync(eventId, userId)));
    }
}
