using Asp.Versioning;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.DataAccess.Providers.Interfaces;

namespace Coaching.Controllers.V1;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class EvaluationPlansController : Shared.Microservices.Controllers.BaseApiController
{
    private readonly IEvaluationPlanService _planService;

    public EvaluationPlansController(IEvaluationPlanService planService, IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _planService = planService;
    }

    [HttpGet("evaluation-plans/me")]
    public async Task<IActionResult> GetMyPlans()
    {
        CheckIsUserLoggedIn();
        var plans = await _planService.GetByUserIdAsync(JwtPayload.UserId);
        return Ok(plans);
    }

    [HttpGet("clubs/{clubId:guid}/evaluation-plans")]
    public async Task<IActionResult> GetByClubId(Guid clubId)
    {
        CheckIsUserLoggedIn();
        var plans = await _planService.GetByClubIdAsync(clubId, JwtPayload.UserId);
        return Ok(plans);
    }

    /// <summary>
    /// No plan is public, so an anonymous caller is asked to sign in before anything is looked up —
    /// the same 401 whether or not the plan exists.
    /// </summary>
    [HttpGet("evaluation-plans/{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        CheckIsUserLoggedIn();
        var plan = await _planService.GetByIdForUserAsync(id, JwtPayload.UserId);
        return Ok(plan);
    }

    [HttpPost("evaluation-plans")]
    public async Task<IActionResult> Create([FromBody] CreateEvaluationPlanDto request)
    {
        CheckIsUserLoggedIn();
        var plan = await _planService.CreateAsync(request, JwtPayload.UserId);
        return CreatedAtAction(nameof(GetById), new { id = plan.Id, version = "1.0" }, plan);
    }

    [HttpPut("evaluation-plans/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEvaluationPlanDto request)
    {
        CheckIsUserLoggedIn();
        var plan = await _planService.UpdateAsync(id, request, JwtPayload.UserId);
        return Ok(plan);
    }

    [HttpDelete("evaluation-plans/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        CheckIsUserLoggedIn();
        await _planService.DeleteAsync(id, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPost("evaluation-plans/{id:guid}/items")]
    public async Task<IActionResult> AddItem(Guid id, [FromBody] AddPlanItemDto request)
    {
        CheckIsUserLoggedIn();
        var plan = await _planService.AddItemAsync(id, request, JwtPayload.UserId);
        return Ok(plan);
    }

    [HttpDelete("evaluation-plans/{id:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> RemoveItem(Guid id, Guid itemId)
    {
        CheckIsUserLoggedIn();
        var plan = await _planService.RemoveItemAsync(id, itemId, JwtPayload.UserId);
        return Ok(plan);
    }

    [HttpPut("evaluation-plans/{id:guid}/items/reorder")]
    public async Task<IActionResult> ReorderItems(Guid id, [FromBody] ReorderPlanItemsDto request)
    {
        CheckIsUserLoggedIn();
        var plan = await _planService.ReorderItemsAsync(id, request.ItemIds, JwtPayload.UserId);
        return Ok(plan);
    }
}
