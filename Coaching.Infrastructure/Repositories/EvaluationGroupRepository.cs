using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Models.Evaluation;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class EvaluationGroupRepository : BaseRepository<EvaluationGroup>, IEvaluationGroupRepository
{
    public EvaluationGroupRepository(CoachingDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<EvaluationGroup>> GetBySessionIdAsync(Guid sessionId)
    {
        return await _dbSet
            .Where(g => g.SessionId == sessionId && !g.IsDeleted)
            .Include(g => g.Players.Where(p => !p.IsDeleted))
            .OrderBy(g => g.Order)
            .ToListAsync();
    }

    public async Task<EvaluationGroup?> GetByIdWithPlayersAsync(Guid groupId)
    {
        return await _dbSet
            .Where(g => g.Id == groupId && !g.IsDeleted)
            .Include(g => g.Players.Where(p => !p.IsDeleted))
            .FirstOrDefaultAsync();
    }

    public Task<bool> IsEvaluatorAsync(Guid sessionId, Guid userId) =>
        _dbSet.AnyAsync(g => g.SessionId == sessionId && g.EvaluatorUserId == userId && !g.IsDeleted);
}
