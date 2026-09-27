using Asp.Versioning;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.DataAccess.Providers.Interfaces;

namespace Coaching.Controllers.V1;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class EvaluationExercisesController : Shared.Microservices.Controllers.BaseApiController
{
    private readonly IEvaluationExerciseService _exerciseService;

    public EvaluationExercisesController(IEvaluationExerciseService exerciseService, IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _exerciseService = exerciseService;
    }

    [HttpGet("evaluation-exercises")]
    public async Task<IActionResult> GetPublic([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _exerciseService.GetPublicExercisesAsync(page, pageSize);
        return Ok(result);
    }

    [HttpGet("me/evaluation-exercises")]
    public async Task<IActionResult> GetMine([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        CheckIsUserLoggedIn();
        var result = await _exerciseService.GetUserExercisesAsync(JwtPayload.UserId, page, pageSize);
        return Ok(result);
    }

    /// <summary>
    /// An exercise outside any club is the public library's, which lists to any signed-in reader.
    /// Everything else the service refuses as it would a missing exercise.
    /// </summary>
    [HttpGet("evaluation-exercises/{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var exercise = await _exerciseService.GetByIdForUserAsync(id, JwtPayload!.UserId);
        return Ok(exercise);
    }

    [HttpGet("clubs/{clubId:guid}/evaluation-exercises")]
    public async Task<IActionResult> GetByClubId(Guid clubId)
    {
        CheckIsUserLoggedIn();
        var exercises = await _exerciseService.GetByClubIdAsync(clubId, JwtPayload.UserId);
        return Ok(exercises);
    }

    [HttpPost("evaluation-exercises")]
    public async Task<IActionResult> Create([FromBody] CreateEvaluationExerciseDto request)
    {
        CheckIsUserLoggedIn();
        var exercise = await _exerciseService.CreateAsync(request, JwtPayload.UserId);
        return CreatedAtAction(nameof(GetById), new { id = exercise.Id, version = "1.0" }, exercise);
    }

    [HttpPut("evaluation-exercises/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEvaluationExerciseDto request)
    {
        CheckIsUserLoggedIn();
        var exercise = await _exerciseService.UpdateAsync(id, request, JwtPayload.UserId);
        return Ok(exercise);
    }

    [HttpDelete("evaluation-exercises/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        CheckIsUserLoggedIn();
        await _exerciseService.DeleteAsync(id, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPost("evaluation-exercises/{id:guid}/metrics")]
    public async Task<IActionResult> AddMetric(Guid id, [FromBody] AddMetricDto request)
    {
        CheckIsUserLoggedIn();
        var exercise = await _exerciseService.AddMetricAsync(id, request, JwtPayload.UserId);
        return Ok(exercise);
    }

    [HttpPut("evaluation-exercises/{id:guid}/metrics/{metricId:guid}")]
    public async Task<IActionResult> UpdateMetric(Guid id, Guid metricId, [FromBody] UpdateEvaluationMetricDto request)
    {
        CheckIsUserLoggedIn();
        var exercise = await _exerciseService.UpdateMetricAsync(id, metricId, request, JwtPayload.UserId);
        return Ok(exercise);
    }

    [HttpDelete("evaluation-exercises/{id:guid}/metrics/{metricId:guid}")]
    public async Task<IActionResult> RemoveMetric(Guid id, Guid metricId)
    {
        CheckIsUserLoggedIn();
        var exercise = await _exerciseService.RemoveMetricAsync(id, metricId, JwtPayload.UserId);
        return Ok(exercise);
    }

    [HttpPut("evaluation-exercises/{id:guid}/metrics/{metricId:guid}/skill-weights")]
    public async Task<IActionResult> UpdateMetricSkillWeights(Guid id, Guid metricId, [FromBody] List<CreateMetricSkillWeightDto> weights)
    {
        CheckIsUserLoggedIn();
        var exercise = await _exerciseService.UpdateMetricSkillWeightsAsync(id, metricId, weights, JwtPayload.UserId);
        return Ok(exercise);
    }
}
