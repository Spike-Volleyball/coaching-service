using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class TrainingPlanRunRepository : BaseRepository<TrainingPlanRun>, ITrainingPlanRunRepository
{
    public TrainingPlanRunRepository(CoachingDbContext context) : base(context) { }

    public async Task<TrainingPlanRun?> GetByEventIdWithDetailsAsync(Guid eventId)
    {
        return await _dbSet
            .Include(r => r.Items.OrderBy(i => i.Order))
                // A Stations row is nothing without its groups, and a restart re-snapshots them:
                // both the reading and the rebuilding need the old ones loaded.
                .ThenInclude(i => i.Stations.OrderBy(s => s.Order))
                    .ThenInclude(s => s.Items.OrderBy(r => r.Order))
            // A single chain: its rows are the run's leaves, a sum rather than a product.
            .AsSingleQuery()
            .FirstOrDefaultAsync(r => r.EventId == eventId && !r.IsDeleted);
    }
}
