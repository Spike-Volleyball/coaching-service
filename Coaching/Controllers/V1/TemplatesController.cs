using Asp.Versioning;
using Coaching.Application.Exceptions;
using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.DataAccess.Providers.Interfaces;
using Shared.Microservices.Guardian;

namespace Coaching.Controllers.V1;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class PlansController : Shared.Microservices.Controllers.BaseApiController
{
    private readonly ITrainingPlanService _planService;
    private readonly IPlanCoachService _planCoachService;

    public PlansController(
        ITrainingPlanService planService,
        IPlanCoachService planCoachService,
        IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _planService = planService;
        _planCoachService = planCoachService;
    }

    #region CRUD

    [HttpGet("plans/{id:guid}")]
    public async Task<IActionResult> GetById([FromRoute] Guid id)
    {
        var userId = JwtPayload?.UserId;
        var plan = await _planService.GetByIdAsync(id, userId);
        if (plan == null) return NotFound();
        return Ok(plan);
    }

    [HttpPost("plans")]
    public async Task<IActionResult> Create([FromBody] CreatePlanDto request)
    {
        CheckIsUserLoggedIn();
        var plan = await _planService.CreateAsync(request, JwtPayload.UserId);
        return CreatedAtAction(nameof(GetById), new { id = plan.Id, version = "1.0" }, plan);
    }

    [HttpPut("plans/{id:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] UpdatePlanDto request)
    {
        CheckIsUserLoggedIn();
        var plan = await _planService.UpdateAsync(id, request, JwtPayload.UserId);
        return Ok(plan);
    }

    [HttpDelete("plans/{id:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        await _planService.DeleteAsync(id, JwtPayload.UserId);
        return NoContent();
    }

    #endregion

    #region Event Plans

    [HttpPost("events/{eventId:guid}/plans")]
    public async Task<IActionResult> CreateEventPlan([FromRoute] Guid eventId, [FromBody] CreateEventPlanDto request)
    {
        CheckIsUserLoggedIn();
        var result = await _planService.CreateEventPlanAsync(eventId, request, JwtPayload.UserId);
        return CreatedAtAction(nameof(GetById), new { id = result.Id, version = "1.0" }, result);
    }

    [HttpGet("events/{eventId:guid}/plans")]
    [AcceptsSubject]
    public async Task<IActionResult> GetEventPlan([FromRoute] Guid eventId, [FromSubject] Guid subjectUserId)
    {
        CheckIsUserLoggedIn();
        // A guardian opening their child's event is authorized against the child's participation.
        var result = await _planService.GetByEventIdAsync(eventId, subjectUserId);
        if (result == null) return NotFound();
        return Ok(result);
    }

    [HttpPost("plans/{id:guid}/promote")]
    public async Task<IActionResult> PromoteToTemplate([FromRoute] Guid id, [FromBody] PromotePlanDto? request)
    {
        CheckIsUserLoggedIn();
        var result = await _planService.PromoteToTemplateAsync(id, request ?? new PromotePlanDto(null, null), JwtPayload.UserId);
        return CreatedAtAction(nameof(GetById), new { id = result.Id, version = "1.0" }, result);
    }

    #endregion

    #region List/Browse

    [HttpGet("me/plans")]
    public async Task<IActionResult> GetMyPlans([FromQuery] PlanFilterRequest filter)
    {
        CheckIsUserLoggedIn();
        var result = await _planService.GetMyPlansAsync(JwtPayload.UserId, filter);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/plans")]
    public async Task<IActionResult> GetClubPlans([FromRoute] Guid clubId, [FromQuery] PlanFilterRequest filter)
    {
        CheckIsUserLoggedIn();
        var result = await _planService.GetClubPlansAsync(clubId, JwtPayload.UserId, filter);
        return Ok(result);
    }

    [HttpGet("plans")]
    public async Task<IActionResult> GetPublicPlans([FromQuery] PlanFilterRequest filter)
    {
        var userId = JwtPayload?.UserId is { } id && id != Guid.Empty ? id : (Guid?)null;
        var result = await _planService.GetPublicPlansAsync(filter, userId);
        return Ok(result);
    }

    [HttpGet("me/plans/bookmarks")]
    public async Task<IActionResult> GetBookmarkedPlans([FromQuery] PlanFilterRequest filter)
    {
        CheckIsUserLoggedIn();
        var result = await _planService.GetBookmarkedPlansAsync(JwtPayload.UserId, filter);
        return Ok(result);
    }

    [HttpGet("me/plans/likes")]
    public async Task<IActionResult> GetLikedPlans([FromQuery] PlanFilterRequest filter)
    {
        CheckIsUserLoggedIn();
        var result = await _planService.GetLikedPlansAsync(JwtPayload.UserId, filter);
        return Ok(result);
    }

    #endregion

    #region Sections

    [HttpPost("plans/{id:guid}/sections")]
    public async Task<IActionResult> AddSection([FromRoute] Guid id, [FromBody] CreatePlanSectionDto request)
    {
        CheckIsUserLoggedIn();
        var section = await _planService.AddSectionAsync(id, request, JwtPayload.UserId);
        return Created($"/v1/plans/{id}/sections/{section.Id}", section);
    }

    [HttpPut("plans/{id:guid}/sections/{sectionId:guid}")]
    public async Task<IActionResult> UpdateSection([FromRoute] Guid id, [FromRoute] Guid sectionId, [FromBody] UpdatePlanSectionDto request)
    {
        CheckIsUserLoggedIn();
        var section = await _planService.UpdateSectionAsync(id, sectionId, request, JwtPayload.UserId);
        return Ok(section);
    }

    [HttpDelete("plans/{id:guid}/sections/{sectionId:guid}")]
    public async Task<IActionResult> DeleteSection([FromRoute] Guid id, [FromRoute] Guid sectionId)
    {
        CheckIsUserLoggedIn();
        await _planService.DeleteSectionAsync(id, sectionId, JwtPayload.UserId);
        return NoContent();
    }

    #endregion

    #region Items

    [HttpPost("plans/{id:guid}/items")]
    public async Task<IActionResult> AddItem([FromRoute] Guid id, [FromBody] CreatePlanItemDto request)
    {
        CheckIsUserLoggedIn();
        var item = await _planService.AddItemAsync(id, request, JwtPayload.UserId);
        return Created($"/v1/plans/{id}/items/{item.Id}", item);
    }

    [HttpPut("plans/{id:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> UpdateItem([FromRoute] Guid id, [FromRoute] Guid itemId, [FromBody] UpdatePlanItemDto request)
    {
        CheckIsUserLoggedIn();
        var item = await _planService.UpdateItemAsync(id, itemId, request, JwtPayload.UserId);
        return Ok(item);
    }

    [HttpDelete("plans/{id:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> DeleteItem([FromRoute] Guid id, [FromRoute] Guid itemId)
    {
        CheckIsUserLoggedIn();
        await _planService.DeleteItemAsync(id, itemId, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPut("plans/{id:guid}/items/reorder")]
    public async Task<IActionResult> ReorderItems([FromRoute] Guid id, [FromBody] ReorderPlanItemsDto request)
    {
        CheckIsUserLoggedIn();
        await _planService.ReorderItemsAsync(id, request, JwtPayload.UserId);
        return Ok();
    }

    #endregion

    #region Coaches

    [HttpPut("plans/{id:guid}/coaches")]
    public async Task<IActionResult> AssignPlanCoaches([FromRoute] Guid id, [FromBody] AssignCoachesDto request)
    {
        CheckIsUserLoggedIn();
        var coaches = await _planCoachService.ReplacePlanCoachesAsync(id, request, JwtPayload.UserId);
        return Ok(coaches);
    }

    [HttpPut("plans/{id:guid}/stations/{stationId:guid}/coaches")]
    public async Task<IActionResult> AssignStationCoaches([FromRoute] Guid id, [FromRoute] Guid stationId, [FromBody] AssignCoachesDto request)
    {
        CheckIsUserLoggedIn();
        var coaches = await _planCoachService.ReplaceStationCoachesAsync(id, stationId, request, JwtPayload.UserId);
        return Ok(coaches);
    }

    #endregion

    #region Likes

    [HttpPost("plans/{id:guid}/like")]
    public async Task<IActionResult> Like([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        var status = await _planService.LikeAsync(id, JwtPayload.UserId);
        return Ok(status);
    }

    [HttpDelete("plans/{id:guid}/like")]
    public async Task<IActionResult> Unlike([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        var status = await _planService.UnlikeAsync(id, JwtPayload.UserId);
        return Ok(status);
    }

    [HttpGet("plans/{id:guid}/like")]
    public async Task<IActionResult> GetLikeStatus([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        var status = await _planService.GetLikeStatusAsync(id, JwtPayload.UserId);
        return Ok(status);
    }

    #endregion

    #region Bookmarks

    [HttpPost("plans/{id:guid}/bookmark")]
    public async Task<IActionResult> Bookmark([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        var status = await _planService.BookmarkAsync(id, JwtPayload.UserId);
        return Ok(status);
    }

    [HttpDelete("plans/{id:guid}/bookmark")]
    public async Task<IActionResult> Unbookmark([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        var status = await _planService.UnbookmarkAsync(id, JwtPayload.UserId);
        return Ok(status);
    }

    #endregion

    #region Comments

    [HttpPost("plans/{id:guid}/comments")]
    public IActionResult CreateComment([FromRoute] Guid id)
    {
        CheckIsUserLoggedIn();
        throw new CommentsMovedException();
    }

    [HttpGet("plans/{id:guid}/comments")]
    public async Task<IActionResult> GetComments([FromRoute] Guid id, [FromQuery] Guid? cursor, [FromQuery] int limit = 20)
    {
        CheckIsUserLoggedIn();
        var result = await _planService.GetCommentsAsync(id, cursor, limit, JwtPayload.UserId);
        return Ok(result);
    }

    [HttpDelete("plans/{id:guid}/comments/{commentId:guid}")]
    public IActionResult DeleteComment([FromRoute] Guid id, [FromRoute] Guid commentId)
    {
        CheckIsUserLoggedIn();
        throw new CommentsMovedException();
    }

    #endregion
}
