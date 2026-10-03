using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class TrainingPlanRunRepository : BaseRepository<TrainingPlanRun>, ITrainingPlanRunRepository
{
    public TrainingPlanRunRepository(CoachingDbContext context) : base(context) { }

    public Task<TrainingPlanRun?> GetByEventIdWithDetailsAsync(Guid eventId) =>
        WithDetails(_dbSet).FirstOrDefaultAsync(r => r.EventId == eventId && !r.IsDeleted);

    public Task<TrainingPlanRun?> GetByEventIdWithDetailsNoTrackingAsync(Guid eventId) =>
        WithDetails(_dbSet.AsNoTracking()).FirstOrDefaultAsync(r => r.EventId == eventId && !r.IsDeleted);

    public Task<TrainingPlanRun?> GetWithDetailsAsync(Guid runId) =>
        WithDetails(_dbSet).FirstOrDefaultAsync(r => r.Id == runId && !r.IsDeleted);

    // The step after is by Order, where the run takes it by position: two steps sharing an Order
    // can only make a due run wait for a read or a control to move it, never make the sweep read
    // one it cannot move.
    public Task<List<Guid>> GetIdsDueToAutoAdvanceAsync(DateTime now, CancellationToken cancellationToken) =>
        _dbSet.AsNoTracking()
            .Where(r => r.Status == RunStatus.Running && r.AutoAdvance && !r.IsDeleted && r.CurrentItemStartedAtUtc != null)
            .Where(r => r.Items.Any(current =>
                current.PlanItemId == r.CurrentItemId
                && current.PlannedDurationSeconds > 0
                && r.CurrentItemStartedAtUtc!.Value.AddSeconds(current.PlannedDurationSeconds) <= now
                && r.Items.Any(next => next.Order > current.Order)))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

    private static IQueryable<TrainingPlanRun> WithDetails(IQueryable<TrainingPlanRun> runs) =>
        runs
            .Include(r => r.Items.OrderBy(i => i.Order))
                // A Stations row is nothing without its groups, and a restart re-snapshots them:
                // both the reading and the rebuilding need the old ones loaded.
                .ThenInclude(i => i.Stations.OrderBy(s => s.Order))
                    .ThenInclude(s => s.Items.OrderBy(r => r.Order))
            // A single chain: its rows are the run's leaves, a sum rather than a product.
            .AsSingleQuery();
}
