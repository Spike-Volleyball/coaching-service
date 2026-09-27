using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Models.Evaluation;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class PlayerEvaluationRepository : BaseRepository<PlayerEvaluation>, IPlayerEvaluationRepository
{
    public PlayerEvaluationRepository(CoachingDbContext context) : base(context)
    {
    }

    public async Task<PlayerEvaluation?> GetByIdWithScoresAsync(Guid id)
    {
        return await _dbSet
            .AsSplitQuery()
            .Include(e => e.MetricScores.Where(s => !s.IsDeleted))
                .ThenInclude(s => s.Metric)
                    .ThenInclude(m => m.SkillWeights.Where(w => !w.IsDeleted))
            .Include(e => e.SkillScores.Where(s => !s.IsDeleted))
            .Include(e => e.Participant)
            .FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted);
    }

    public async Task<PlayerEvaluation?> GetByParticipantIdAsync(Guid participantId)
    {
        return await _dbSet
            .Include(e => e.MetricScores.Where(s => !s.IsDeleted))
            .Include(e => e.SkillScores.Where(s => !s.IsDeleted))
            .AsSplitQuery()
            .FirstOrDefaultAsync(e => e.EvaluationParticipantId == participantId && !e.IsDeleted);
    }

    public async Task<IEnumerable<PlayerEvaluation>> GetBySessionIdAsync(Guid sessionId)
    {
        return await _dbSet
            .Include(e => e.SkillScores.Where(s => !s.IsDeleted))
            .Include(e => e.Participant)
            .Where(e => e.Participant.EvaluationSessionId == sessionId && !e.IsDeleted)
            .OrderByDescending(e => e.SkillScores.Any() ? e.SkillScores.Average(s => s.Score) : 0)
            .ToListAsync();
    }

    public async Task<IEnumerable<PlayerEvaluation>> GetByPlayerIdAsync(Guid playerId, int page = 1, int pageSize = 20)
    {
        return await _dbSet
            .Include(e => e.SkillScores.Where(s => !s.IsDeleted))
            .Include(e => e.Participant)
                .ThenInclude(p => p.Session)
            .Where(e => e.PlayerId == playerId && e.SharedWithPlayer && !e.IsDeleted)
            .OrderByDescending(e => e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }
}
