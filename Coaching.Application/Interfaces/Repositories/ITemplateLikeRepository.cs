using Coaching.Domain.Models.Templates;
using Shared.DataAccess.Repositories.Interfaces;

namespace Coaching.Application.Interfaces.Repositories;

public interface IPlanLikeRepository : IRepository<PlanLike>
{
    Task<PlanLike?> GetByTemplateAndUserAsync(Guid templateId, Guid userId);
    Task<int> GetCountByTemplateAsync(Guid templateId);
    Task<IEnumerable<Guid>> GetUserLikedPlanIdsAsync(Guid userId, IEnumerable<Guid> planIds);
    Task<IEnumerable<PlanLike>> GetByUserAsync(Guid userId, IReadOnlyCollection<Guid> memberClubIds, int skip, int take);
    Task<int> GetCountByUserAsync(Guid userId, IReadOnlyCollection<Guid> memberClubIds);
}
