using Asp.Versioning;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.DataAccess.Providers.Interfaces;

namespace Coaching.Controllers.V1;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class ThresholdsController : Shared.Microservices.Controllers.BaseApiController
{
    private readonly IThresholdService _thresholdService;
    private readonly IPlayerEvaluationService _evaluationService;

    public ThresholdsController(
        IThresholdService thresholdService,
        IPlayerEvaluationService evaluationService,
        IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _thresholdService = thresholdService;
        _evaluationService = evaluationService;
    }

    [HttpGet("clubs/{clubId:guid}/thresholds")]
    public async Task<IActionResult> GetByClubId(Guid clubId)
    {
        CheckIsUserLoggedIn();
        var thresholds = await _thresholdService.GetByClubIdAsync(clubId, JwtPayload.UserId);
        return Ok(thresholds);
    }

    [HttpPost("clubs/{clubId:guid}/thresholds")]
    public async Task<IActionResult> Create(Guid clubId, [FromBody] CreateThresholdDto request)
    {
        CheckIsUserLoggedIn();
        var threshold = await _thresholdService.CreateAsync(clubId, request, JwtPayload.UserId);
        return Ok(threshold);
    }

    [HttpPut("thresholds/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateThresholdDto request)
    {
        CheckIsUserLoggedIn();
        var threshold = await _thresholdService.UpdateAsync(id, request, JwtPayload.UserId);
        return Ok(threshold);
    }

    [HttpDelete("thresholds/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        CheckIsUserLoggedIn();
        await _thresholdService.DeleteAsync(id, JwtPayload.UserId);
        return NoContent();
    }

    [HttpGet("clubs/{clubId:guid}/evaluations/{evaluationId:guid}/check-threshold")]
    public async Task<IActionResult> CheckThreshold(Guid clubId, Guid evaluationId)
    {
        CheckIsUserLoggedIn();
        var evaluation = await _evaluationService.GetByIdAsync(evaluationId, JwtPayload.UserId);
        if (evaluation == null) return NotFound();

        var result = await _thresholdService.CheckPlayerAsync(clubId, evaluation);
        return Ok(result);
    }
}
