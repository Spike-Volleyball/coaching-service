using Coaching.Application.Interfaces.Services;
using Shared.Security.Access;

namespace Coaching.Authorization;

/// <summary>A run belongs to its event, so its id here is the event's.</summary>
public sealed class RunAuthority(IRunService runs) : IResourceAuthority<RunAccess>
{
    public Task<bool> CanAsync(Guid? userId, Guid resourceId, RunAccess access, CancellationToken ct) =>
        (userId, access) switch
        {
            (null, _) => Task.FromResult(false),
            ({ } readerId, RunAccess.Read) => runs.CanReadRunAsync(resourceId, readerId),
            ({ } controllerId, RunAccess.Control) => runs.CanControlRunAsync(resourceId, controllerId),
            _ => Task.FromResult(false),
        };
}
