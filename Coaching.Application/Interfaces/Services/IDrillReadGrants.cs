using Coaching.Application.DTOs.Drills;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// The places that show a reader a drill that is not theirs to open on its own: the plan of an
/// event they may read, and feedback they may read. Each opens only the drills it holds, so naming
/// an event or a feedback shows nothing its own page does not already show.
/// </summary>
public interface IDrillReadGrants
{
    Task<bool> GrantsAsync(Guid drillId, Guid userId, DrillReadContext context);
}
