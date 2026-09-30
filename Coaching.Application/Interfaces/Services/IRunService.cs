using Coaching.Application.DTOs.Templates;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// An event's training run. Whoever may control it — the plan's creator or an event admin — is
/// one rule, <see cref="CanControlRunAsync"/>, applied to every control operation and to the
/// <c>CanControl</c> a read reports.
/// </summary>
public interface IRunService
{
    /// <summary>Returns the run for the event, or null when no run has started. View = any participant.</summary>
    Task<RunDto?> GetByEventIdAsync(Guid eventId, Guid requestingUserId);

    /// <summary>Whether the user may watch the event's run: its plan's creator, a participant or a host.</summary>
    Task<bool> CanReadRunAsync(Guid eventId, Guid userId);

    /// <summary>Whether the user may control the event's run: its plan's creator or an event admin. Needs no run.</summary>
    Task<bool> CanControlRunAsync(Guid eventId, Guid userId);

    /// <summary>
    /// Create-or-reset: snapshot all plan items, set Running with the first item current. A run
    /// that is Running or Paused is only reset when <paramref name="restart"/> is set; otherwise
    /// a conflict.
    /// </summary>
    Task<RunDto> StartAsync(Guid eventId, Guid requestingUserId, bool restart = false);

    // Every control operation below takes occurredAt: when the tap was made, for a tap queued
    // offline that reaches the server late. Null means now. Item ids are plan item ids — the
    // same ids as the run's CurrentItemId.

    /// <summary>Capture elapsed, set Paused.</summary>
    Task<RunDto> PauseAsync(Guid eventId, Guid requestingUserId, DateTimeOffset? occurredAt = null);

    /// <summary>Re-anchor the virtual start, set Running.</summary>
    Task<RunDto> ResumeAsync(Guid eventId, Guid requestingUserId, DateTimeOffset? occurredAt = null);

    /// <summary>
    /// Finish the current item and enter the next by order, running — or complete the run after
    /// the last. fromItemId guards against a double or stale tap.
    /// </summary>
    Task<RunDto> AdvanceAsync(Guid eventId, Guid fromItemId, Guid requestingUserId, DateTimeOffset? occurredAt = null);

    /// <summary>
    /// Leave the current item for any other, running. Going back discards the visit to the item
    /// left; going forward finishes it. fromItemId guards as for advance.
    /// </summary>
    Task<RunDto> GoToAsync(Guid eventId, Guid fromItemId, Guid toItemId, Guid requestingUserId, DateTimeOffset? occurredAt = null);

    /// <summary>Finish the current item and set Completed.</summary>
    Task<RunDto> CompleteAsync(Guid eventId, Guid requestingUserId, DateTimeOffset? occurredAt = null);

    /// <summary>Take a Completed run back to Running on the item it ended on, from that item's time.</summary>
    Task<RunDto> ReopenAsync(Guid eventId, Guid requestingUserId, DateTimeOffset? occurredAt = null);
}
