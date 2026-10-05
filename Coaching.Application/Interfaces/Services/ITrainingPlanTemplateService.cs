using Coaching.Application.DTOs.Comments;
using Coaching.Application.DTOs.Templates;

namespace Coaching.Application.Interfaces.Services;

public interface ITrainingPlanService
{
    // CRUD
    Task<TrainingPlanDetailDto> CreateAsync(CreatePlanDto request, Guid userId);
    Task<TrainingPlanDetailDto?> GetByIdAsync(Guid id, Guid? userId = null);
    Task<TrainingPlanDetailDto> UpdateAsync(Guid id, UpdatePlanDto request, Guid userId);
    Task DeleteAsync(Guid id, Guid userId);

    // Event Plans
    Task<TrainingPlanDetailDto> CreateEventPlanAsync(Guid eventId, CreateEventPlanDto request, Guid userId);
    Task<TrainingPlanDetailDto?> GetByEventIdAsync(Guid eventId, Guid userId);
    Task<TrainingPlanDetailDto> PromoteToTemplateAsync(Guid planId, PromotePlanDto request, Guid userId);

    // List/Browse
    Task<PlanListResponseDto> GetMyPlansAsync(Guid userId, PlanFilterRequest filter);
    Task<PlanListResponseDto> GetClubPlansAsync(Guid clubId, Guid userId, PlanFilterRequest filter);
    Task<PlanListResponseDto> GetPublicPlansAsync(PlanFilterRequest filter, Guid? userId = null);
    Task<PlanListResponseDto> GetBookmarkedPlansAsync(Guid userId, PlanFilterRequest filter);
    Task<PlanListResponseDto> GetLikedPlansAsync(Guid userId, PlanFilterRequest filter);

    // Sections
    Task<PlanSectionDto> AddSectionAsync(Guid planId, CreatePlanSectionDto request, Guid userId);
    Task<PlanSectionDto> UpdateSectionAsync(Guid planId, Guid sectionId, UpdatePlanSectionDto request, Guid userId);
    Task DeleteSectionAsync(Guid planId, Guid sectionId, Guid userId);

    // Items
    Task<PlanItemDto> AddItemAsync(Guid planId, CreatePlanItemDto request, Guid userId);
    Task<PlanItemDto> UpdateItemAsync(Guid planId, Guid itemId, UpdatePlanItemDto request, Guid userId);
    Task DeleteItemAsync(Guid planId, Guid itemId, Guid userId);
    Task ReorderItemsAsync(Guid planId, ReorderPlanItemsDto request, Guid userId);

    // Likes
    Task<PlanLikeStatusDto> LikeAsync(Guid planId, Guid userId);
    Task<PlanLikeStatusDto> UnlikeAsync(Guid planId, Guid userId);
    Task<PlanLikeStatusDto> GetLikeStatusAsync(Guid planId, Guid userId);

    // Bookmarks
    Task<PlanBookmarkStatusDto> BookmarkAsync(Guid planId, Guid userId);
    Task<PlanBookmarkStatusDto> UnbookmarkAsync(Guid planId, Guid userId);

    // Comments
    Task<PlanCommentsResponseDto> GetCommentsAsync(Guid planId, Guid? cursor, int limit, Guid userId);
    Task<CommentStanding> GetCommentStandingAsync(Guid planId, Guid userId);
}
