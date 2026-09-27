using Coaching.Domain.Models.Evaluation;
using Shared.DataAccess.Repositories.Interfaces;

namespace Coaching.Application.Interfaces.Repositories;

public interface IEvaluationGroupRepository : IRepository<EvaluationGroup>
{
    Task<IEnumerable<EvaluationGroup>> GetBySessionIdAsync(Guid sessionId);
    Task<EvaluationGroup?> GetByIdWithPlayersAsync(Guid groupId);

    /// <summary>Whether the user scores one of the session's groups.</summary>
    Task<bool> IsEvaluatorAsync(Guid sessionId, Guid userId);
}
