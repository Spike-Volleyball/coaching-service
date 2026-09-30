using Asp.Versioning;
using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Shared.DataAccess.Providers.Interfaces;
using Shared.Security.Access;

namespace Coaching.Controllers.V1;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class RunController : Shared.Microservices.Controllers.BaseApiController
{
    private const string RunControlOnly =
        "The run service lets through only whoever may run the session — its plan's creator or an event admin — and asks before it reads the run, as for every other control";

    private readonly IRunService _runService;

    public RunController(
        IRunService runService,
        IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _runService = runService;
    }

    [HttpGet("events/{eventId:guid}/plans/run")]
    public async Task<IActionResult> GetRun([FromRoute] Guid eventId)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.GetByEventIdAsync(eventId, JwtPayload.UserId);
        if (run == null) return Content("null", "application/json");
        return Ok(run);
    }

    [HttpGet("events/{eventId:guid}/plans/run/permissions")]
    [NoResourceScope("Answers only whether the caller may control the event's run, and false for anyone who may not — a missing event reads like any other")]
    public async Task<IActionResult> GetRunPermissions([FromRoute] Guid eventId)
    {
        CheckIsUserLoggedIn();
        var canControl = await _runService.CanControlRunAsync(eventId, JwtPayload.UserId);
        return Ok(new RunPermissionsDto(canControl));
    }

    [HttpPost("events/{eventId:guid}/plans/run/start")]
    public async Task<IActionResult> StartRun(
        [FromRoute] Guid eventId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StartRunDto? request)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.StartAsync(eventId, JwtPayload.UserId, request?.Restart ?? false, request?.AutoAdvance);
        return Ok(run);
    }

    [HttpPost("events/{eventId:guid}/plans/run/pause")]
    public async Task<IActionResult> PauseRun(
        [FromRoute] Guid eventId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RunTapDto? request)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.PauseAsync(eventId, JwtPayload.UserId, request?.OccurredAt);
        return Ok(run);
    }

    [HttpPost("events/{eventId:guid}/plans/run/resume")]
    public async Task<IActionResult> ResumeRun(
        [FromRoute] Guid eventId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RunTapDto? request)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.ResumeAsync(eventId, JwtPayload.UserId, request?.OccurredAt);
        return Ok(run);
    }

    [HttpPost("events/{eventId:guid}/plans/run/advance")]
    public async Task<IActionResult> AdvanceRun([FromRoute] Guid eventId, [FromBody] AdvanceRunDto request)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.AdvanceAsync(eventId, request.FromItemId, JwtPayload.UserId, request.OccurredAt);
        return Ok(run);
    }

    [HttpPost("events/{eventId:guid}/plans/run/goto")]
    [NoResourceScope(RunControlOnly)]
    public async Task<IActionResult> GoToRunItem([FromRoute] Guid eventId, [FromBody] GoToRunDto request)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.GoToAsync(
            eventId, request.FromItemId, request.ToItemId, JwtPayload.UserId, request.OccurredAt);
        return Ok(run);
    }

    [HttpPost("events/{eventId:guid}/plans/run/complete")]
    public async Task<IActionResult> CompleteRun(
        [FromRoute] Guid eventId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RunTapDto? request)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.CompleteAsync(eventId, JwtPayload.UserId, request?.OccurredAt);
        return Ok(run);
    }

    [HttpPost("events/{eventId:guid}/plans/run/reopen")]
    [NoResourceScope(RunControlOnly)]
    public async Task<IActionResult> ReopenRun(
        [FromRoute] Guid eventId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RunTapDto? request)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.ReopenAsync(eventId, JwtPayload.UserId, request?.OccurredAt);
        return Ok(run);
    }

    [HttpPost("events/{eventId:guid}/plans/run/auto-advance")]
    [NoResourceScope(RunControlOnly)]
    public async Task<IActionResult> SetRunAutoAdvance([FromRoute] Guid eventId, [FromBody] RunAutoAdvanceDto request)
    {
        CheckIsUserLoggedIn();
        var run = await _runService.SetAutoAdvanceAsync(eventId, request.Enabled!.Value, JwtPayload.UserId);
        return Ok(run);
    }
}
