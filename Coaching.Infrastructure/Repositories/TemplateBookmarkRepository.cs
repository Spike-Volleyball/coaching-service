using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class PlanBookmarkRepository : BaseRepository<PlanBookmark>, IPlanBookmarkRepository
{
    public PlanBookmarkRepository(CoachingDbContext context) : base(context) { }

    public async Task<PlanBookmark?> GetByTemplateAndUserAsync(Guid templateId, Guid userId)
    {
        return await _dbSet.FirstOrDefaultAsync(b => b.TemplateId == templateId && b.UserId == userId && !b.IsDeleted);
    }

    public async Task<IEnumerable<PlanBookmark>> GetByUserAsync(Guid userId, int skip, int take)
    {
        return await StillReadable(userId)
            .Include(b => b.Plan)
                .ThenInclude(t => t.Items)
            .Include(b => b.Plan)
                .ThenInclude(t => t.Creator)
            .OrderByDescending(b => b.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
    }

    public async Task<int> GetCountByUserAsync(Guid userId)
    {
        return await StillReadable(userId).CountAsync();
    }

    public async Task<IEnumerable<Guid>> GetUserBookmarkedPlanIdsAsync(Guid userId, IEnumerable<Guid> planIds)
    {
        var ids = planIds.ToList();
        return await _dbSet
            .Where(b => b.UserId == userId && !b.IsDeleted && ids.Contains(b.TemplateId))
            .Select(b => b.TemplateId)
            .ToListAsync();
    }

    /// <summary>The person's own, on templates they may still read: one taken private leaves the list.</summary>
    private IQueryable<PlanBookmark> StillReadable(Guid userId)
    {
        var readable = _context.Set<TrainingPlan>().Where(TrainingPlan.TemplateReadableBy(userId)).Select(p => p.Id);
        return _dbSet.Where(b => b.UserId == userId && !b.IsDeleted && readable.Contains(b.TemplateId));
    }
}
