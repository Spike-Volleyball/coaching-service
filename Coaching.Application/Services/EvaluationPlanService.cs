using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Models.Evaluation;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class EvaluationPlanService(
    IEvaluationPlanRepository planRepository,
    IRepository<EvaluationPlanItem> itemRepository,
    IEvaluationExerciseRepository exerciseRepository,
    IEvaluationAccess access,
    IMapper mapper) : IEvaluationPlanService
{
    private const string PlanNotFound = "Evaluation plan not found";

    public async Task<EvaluationPlanDto> CreateAsync(CreateEvaluationPlanDto request, Guid userId)
    {
        var plan = mapper.Map<EvaluationPlan>(request);
        plan.CreatedByUserId = userId;

        planRepository.Add(plan);
        await planRepository.SaveChangesAsync();

        // Add exercises
        if (request.ExerciseIds != null)
        {
            var order = 1;
            foreach (var exerciseId in request.ExerciseIds)
            {
                var exercise = await exerciseRepository.GetByIdAsync(exerciseId);
                if (exercise != null)
                {
                    var item = new EvaluationPlanItem
                    {
                        PlanId = plan.Id,
                        ExerciseId = exerciseId,
                        Order = order++
                    };
                    itemRepository.Add(item);
                }
            }
            await itemRepository.SaveChangesAsync();
        }

        return await FindAsync(plan.Id) ?? throw new Exception("Failed to retrieve created plan");
    }

    public async Task<EvaluationPlanDto> GetByIdForUserAsync(Guid id, Guid userId)
    {
        var plan = await planRepository.GetByIdWithItemsAsync(id);

        // One answer for "you may not read this" and "there is nothing here", message and all,
        // so a stranger holding an id cannot tell which it is.
        if (plan == null || !await access.MayReadPlanAsync(plan, userId))
            throw new EntityNotFoundException(PlanNotFound);

        return mapper.Map<EvaluationPlanDto>(plan);
    }

    public async Task<List<EvaluationPlanDto>> GetByClubIdAsync(Guid clubId, Guid userId)
    {
        // Someone without standing sees what a club with no plans shows — the answer this
        // service already gives for a club's drills and training plans.
        if (!await access.MayReadClubAsync(clubId, userId))
            return [];

        var plans = await planRepository.GetByClubIdAsync(clubId);
        return mapper.Map<List<EvaluationPlanDto>>(plans);
    }

    public async Task<List<EvaluationPlanDto>> GetByUserIdAsync(Guid userId)
    {
        var plans = await planRepository.GetByUserIdAsync(userId);
        return mapper.Map<List<EvaluationPlanDto>>(plans);
    }

    public async Task<EvaluationPlanDto> UpdateAsync(Guid id, UpdateEvaluationPlanDto request, Guid userId)
    {
        var plan = await planRepository.GetByIdAsync(id);
        if (plan == null)
            throw new EntityNotFoundException(PlanNotFound);

        if (plan.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can update this evaluation plan");

        if (request.Name != null) plan.Name = request.Name;
        if (request.Notes != null) plan.Notes = request.Notes;

        planRepository.Update(plan);
        await planRepository.SaveChangesAsync();

        return await FindAsync(id) ?? throw new Exception("Failed to retrieve plan");
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var plan = await planRepository.GetByIdAsync(id);
        if (plan == null)
            throw new EntityNotFoundException(PlanNotFound);

        if (plan.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can delete this evaluation plan");

        plan.IsDeleted = true;
        planRepository.Update(plan);
        await planRepository.SaveChangesAsync();
    }

    public async Task<EvaluationPlanDto> AddItemAsync(Guid planId, AddPlanItemDto request, Guid userId)
    {
        var plan = await planRepository.GetByIdWithItemsAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException(PlanNotFound);

        if (plan.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can modify this evaluation plan");

        var exercise = await exerciseRepository.GetByIdAsync(request.ExerciseId);
        if (exercise == null)
            throw new EntityNotFoundException("Exercise not found");

        var maxOrder = plan.Items.Any() ? plan.Items.Max(i => i.Order) : 0;
        var order = request.Order ?? maxOrder + 1;

        var item = new EvaluationPlanItem
        {
            PlanId = planId,
            ExerciseId = request.ExerciseId,
            Order = order
        };

        itemRepository.Add(item);
        await itemRepository.SaveChangesAsync();

        return await FindAsync(planId) ?? throw new Exception("Failed to retrieve plan");
    }

    public async Task<EvaluationPlanDto> RemoveItemAsync(Guid planId, Guid itemId, Guid userId)
    {
        var plan = await planRepository.GetByIdWithItemsAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException(PlanNotFound);

        if (plan.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can modify this evaluation plan");

        var item = plan.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
            throw new EntityNotFoundException("Item not found");

        item.IsDeleted = true;
        itemRepository.Update(item);
        await itemRepository.SaveChangesAsync();

        return await FindAsync(planId) ?? throw new Exception("Failed to retrieve plan");
    }

    public async Task<EvaluationPlanDto> ReorderItemsAsync(Guid planId, List<Guid> itemIds, Guid userId)
    {
        var plan = await planRepository.GetByIdWithItemsAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException(PlanNotFound);

        if (plan.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can modify this evaluation plan");

        var order = 1;
        foreach (var itemId in itemIds)
        {
            var item = plan.Items.FirstOrDefault(i => i.Id == itemId);
            if (item != null)
            {
                item.Order = order++;
                itemRepository.Update(item);
            }
        }
        await itemRepository.SaveChangesAsync();

        return await FindAsync(planId) ?? throw new Exception("Failed to retrieve plan");
    }

    /// <summary>The plan a write just touched, for its answer: the writer is its author.</summary>
    private async Task<EvaluationPlanDto?> FindAsync(Guid id)
    {
        var plan = await planRepository.GetByIdWithItemsAsync(id);
        return plan == null ? null : mapper.Map<EvaluationPlanDto>(plan);
    }
}
