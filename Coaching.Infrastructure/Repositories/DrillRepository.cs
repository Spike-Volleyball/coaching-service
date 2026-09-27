using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Models.Drills;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class DrillRepository : BaseRepository<Drill>, IDrillRepository
{
    public DrillRepository(CoachingDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<Drill>> GetByCreatorAsync(Guid userId)
    {
        return await _dbSet
            .Where(d => d.CreatedByUserId == userId)
            .Include(d => d.Attachments)
            .Include(d => d.Equipment.OrderBy(e => e.Order))
            .Include(d => d.Dials.OrderBy(dial => dial.Order))
            .Include(d => d.Creator)
            .AsSplitQuery()
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Drill>> GetByClubAsync(Guid clubId)
    {
        return await _dbSet
            .Where(d => d.ClubId == clubId)
            .Include(d => d.Attachments)
            .Include(d => d.Equipment.OrderBy(e => e.Order))
            .Include(d => d.Dials.OrderBy(dial => dial.Order))
            .Include(d => d.Creator)
            .AsSplitQuery()
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
    }

    public async Task<Drill?> GetByIdWithDetailsAsync(Guid id)
    {
        return await _dbSet
            .Include(d => d.Attachments.OrderBy(a => a.Order))
            .Include(d => d.Equipment.OrderBy(e => e.Order))
            .Include(d => d.Dials.OrderBy(dial => dial.Order))
            .Include(d => d.Variations.OrderBy(v => v.Order))
                .ThenInclude(v => v.TargetDrill)
            .Include(d => d.Creator)
            .AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public override async Task<Drill?> GetByIdAsync(Guid id)
    {
        return await _dbSet
            .Include(d => d.Attachments.OrderBy(a => a.Order))
            .Include(d => d.Equipment.OrderBy(e => e.Order))
            .Include(d => d.Dials.OrderBy(dial => dial.Order))
            .Include(d => d.Variations.OrderBy(v => v.Order))
                .ThenInclude(v => v.TargetDrill)
            .AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == id);
    }
}
