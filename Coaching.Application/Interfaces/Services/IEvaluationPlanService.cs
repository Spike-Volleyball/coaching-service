using Coaching.Application.DTOs.Evaluation;

namespace Coaching.Application.Interfaces.Services;

public interface IEvaluationPlanService
{
    Task<EvaluationPlanDto> CreateAsync(CreateEvaluationPlanDto request, Guid userId);
    /// <summary>
    /// One plan, for a signed-in reader. Refuses rather than returning null: a plan the reader may
    /// not see raises the same not-found as one that is not there, so the two cannot be told apart.
    /// </summary>
    Task<EvaluationPlanDto> GetByIdForUserAsync(Guid id, Guid userId);

    /// <summary>A club's plans for its staff; anyone else gets the empty list a club with none gives.</summary>
    Task<List<EvaluationPlanDto>> GetByClubIdAsync(Guid clubId, Guid userId);
    Task<List<EvaluationPlanDto>> GetByUserIdAsync(Guid userId);
    Task<EvaluationPlanDto> UpdateAsync(Guid id, UpdateEvaluationPlanDto request, Guid userId);
    Task DeleteAsync(Guid id, Guid userId);

    // Items
    Task<EvaluationPlanDto> AddItemAsync(Guid planId, AddPlanItemDto request, Guid userId);
    Task<EvaluationPlanDto> RemoveItemAsync(Guid planId, Guid itemId, Guid userId);
    Task<EvaluationPlanDto> ReorderItemsAsync(Guid planId, List<Guid> itemIds, Guid userId);
}
