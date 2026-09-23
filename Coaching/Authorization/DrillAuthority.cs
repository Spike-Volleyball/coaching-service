using Coaching.Application.Interfaces.Services;
using Shared.Security.Access;

namespace Coaching.Authorization;

public sealed class DrillAuthority(IDrillService drills) : IResourceAuthority<DrillAccess>
{
    public Task<bool> CanAsync(Guid? userId, Guid resourceId, DrillAccess access, CancellationToken ct) =>
        userId is { } readerId && access == DrillAccess.Read
            ? drills.CanReadAsync(resourceId, readerId)
            : Task.FromResult(false);
}
