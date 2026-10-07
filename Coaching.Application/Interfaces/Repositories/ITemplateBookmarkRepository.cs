using Coaching.Domain.Models.Templates;
using Shared.DataAccess.Repositories.Interfaces;

namespace Coaching.Application.Interfaces.Repositories;

public interface IPlanBookmarkRepository : IRepository<PlanBookmark>
{
    Task<PlanBookmark?> GetByTemplateAndUserAsync(Guid templateId, Guid userId);
    Task<IEnumerable<PlanBookmark>> GetByUserAsync(Guid userId, IReadOnlyCollection<Guid> memberClubIds, int skip, int take);
    Task<int> GetCountByUserAsync(Guid userId, IReadOnlyCollection<Guid> memberClubIds);
    Task<IEnumerable<Guid>> GetUserBookmarkedPlanIdsAsync(Guid userId, IEnumerable<Guid> planIds);
}
