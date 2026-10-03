using Coaching.Application.DTOs.Templates;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// Pushes a run state update to every device watching the run.
/// Implemented in the web layer over IHubContext&lt;TrainingRunHub&gt;.
/// </summary>
public interface IRunBroadcaster
{
    /// <summary>
    /// Sends <paramref name="run"/> to the run's controllers with CanControl set and to its viewers
    /// without — whatever CanControl it carries is its caller's own answer, not theirs.
    /// </summary>
    Task BroadcastRunUpdatedAsync(Guid eventId, RunDto run);
}
