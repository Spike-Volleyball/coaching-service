using Coaching.Domain.Models.Templates;
using Shared.DataAccess.Repositories.Interfaces;

namespace Coaching.Application.Interfaces.Repositories;

public interface ITrainingPlanRunRepository : IRepository<TrainingPlanRun>
{
    /// <summary>
    /// Loads the run (with its items ordered by Order) for the instance plan attached to the event.
    /// Returns null when no run has started.
    /// </summary>
    Task<TrainingPlanRun?> GetByEventIdWithDetailsAsync(Guid eventId);

    /// <summary>
    /// The same run as the database holds it now, untracked — past whatever this request has
    /// staged, as after a save refused because another write got there first.
    /// </summary>
    Task<TrainingPlanRun?> GetByEventIdWithDetailsNoTrackingAsync(Guid eventId);

    /// <summary>The run with its items, tracked, as <see cref="GetByEventIdWithDetailsAsync"/> loads it.</summary>
    Task<TrainingPlanRun?> GetWithDetailsAsync(Guid runId);

    /// <summary>
    /// The runs auto-advance has to move on at <paramref name="now"/>: Running, set to auto-advance,
    /// and on a step with a planned time that is up and a step after it. Asked in the database, so
    /// an idle sweep reads no run at all.
    /// </summary>
    Task<List<Guid>> GetIdsDueToAutoAdvanceAsync(DateTime now, CancellationToken cancellationToken);
}
