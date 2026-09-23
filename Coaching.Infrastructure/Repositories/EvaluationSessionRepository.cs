using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Models.Evaluation;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class EvaluationSessionRepository : BaseRepository<EvaluationSession>, IEvaluationSessionRepository
{
    public EvaluationSessionRepository(CoachingDbContext context) : base(context)
    {
    }

    public async Task<EvaluationSession?> GetByIdWithParticipantsAsync(Guid id)
    {
        return await _dbSet
            .AsSplitQuery()
            .Include(s => s.EvaluationPlan)
                .ThenInclude(p => p!.Items.Where(i => !i.IsDeleted).OrderBy(i => i.Order))
                    .ThenInclude(i => i.Exercise)
                        .ThenInclude(e => e.Metrics.Where(m => !m.IsDeleted))
            .Include(s => s.Participants.Where(p => !p.IsDeleted))
                .ThenInclude(p => p.Evaluation)
            .Include(s => s.Groups.Where(g => !g.IsDeleted).OrderBy(g => g.Order))
                .ThenInclude(g => g.Players.Where(p => !p.IsDeleted))
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted);
    }

    public async Task<IEnumerable<EvaluationSession>> GetByClubIdAsync(Guid clubId, int page = 1, int pageSize = 20)
    {
        return await _dbSet
            .Include(s => s.Participants.Where(p => !p.IsDeleted))
            .Where(s => s.ClubId == clubId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<IEnumerable<EvaluationSession>> GetByCoachUserIdAsync(Guid coachUserId, int page = 1, int pageSize = 20)
    {
        return await _dbSet
            .Include(s => s.Participants.Where(p => !p.IsDeleted))
            .Where(s => s.CoachUserId == coachUserId && !s.IsDeleted)
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }
}
