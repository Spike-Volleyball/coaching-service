using Coaching.Application.Interfaces.Services;
using Shared.Security.Access;

namespace Coaching.Authorization;

/// <summary>A run belongs to its event, so its id here is the event's.</summary>
public sealed class RunAuthority(IRunService runs) : IResourceAuthority<RunAccess>
{
    public Task<bool> CanAsync(Guid? userId, Guid resourceId, RunAccess access, CancellationToken ct) =>
        userId is { } readerId && access == RunAccess.Read
            ? runs.CanReadRunAsync(resourceId, readerId)
            : Task.FromResult(false);
}
