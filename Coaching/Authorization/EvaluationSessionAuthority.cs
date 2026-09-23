using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Shared.Security.Access;

namespace Coaching.Authorization;

public sealed class EvaluationSessionAuthority(IEvaluationSessionRepository sessions, IEvaluationAccess evaluations)
    : IResourceAuthority<EvaluationSessionAccess>
{
    public async Task<bool> CanAsync(Guid? userId, Guid resourceId, EvaluationSessionAccess access, CancellationToken ct) =>
        userId is { } readerId
        && access == EvaluationSessionAccess.Read
        && await evaluations.MayReadSessionAsync(await sessions.GetByIdAsync(resourceId), readerId);
}
