using Coaching.Application.DTOs.Templates;

namespace Coaching.Application.Interfaces.Services;

public interface IRunService
{
    /// <summary>Returns the run for the event, or null when no run has started. View = any participant.</summary>
    Task<RunDto?> GetByEventIdAsync(Guid eventId, Guid requestingUserId);

    /// <summary>Whether the user may watch the event's run: its plan's creator, a participant or a host.</summary>
    Task<bool> CanReadRunAsync(Guid eventId, Guid userId);

    /// <summary>Create-or-reset: snapshot all plan items, set Running with the first item current. Plan creator only.</summary>
    Task<RunDto> StartAsync(Guid eventId, Guid requestingUserId);

    /// <summary>Capture elapsed, set Paused. Plan creator only.</summary>
    Task<RunDto> PauseAsync(Guid eventId, Guid requestingUserId);

    /// <summary>Re-anchor the virtual start, set Running. Plan creator only.</summary>
    Task<RunDto> ResumeAsync(Guid eventId, Guid requestingUserId);

    /// <summary>Finalize the current item and move to the next (or complete). fromItemId guards concurrent advance. Plan creator only.</summary>
    Task<RunDto> AdvanceAsync(Guid eventId, Guid fromItemId, Guid requestingUserId);

    /// <summary>Finalize the current item and set Completed. Plan creator only.</summary>
    Task<RunDto> CompleteAsync(Guid eventId, Guid requestingUserId);
}
