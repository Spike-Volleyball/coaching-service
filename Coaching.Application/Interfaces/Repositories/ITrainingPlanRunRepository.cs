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
}
