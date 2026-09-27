using Asp.Versioning;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.DataAccess.Providers.Interfaces;

namespace Coaching.Controllers.V1;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class EvaluationSessionsController : Shared.Microservices.Controllers.BaseApiController
{
    private readonly IEvaluationSessionService _sessionService;
    private readonly IEvaluationSessionLifecycleService _lifecycleService;
    private readonly IEvaluationGroupService _groupService;
    private readonly IEvaluationScoringService _scoringService;

    public EvaluationSessionsController(
        IEvaluationSessionService sessionService,
        IEvaluationSessionLifecycleService lifecycleService,
        IEvaluationGroupService groupService,
        IEvaluationScoringService scoringService,
        IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _sessionService = sessionService;
        _lifecycleService = lifecycleService;
        _groupService = groupService;
        _scoringService = scoringService;
    }

    [HttpGet("evaluation-sessions/me")]
    public async Task<IActionResult> GetMySessions([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        CheckIsUserLoggedIn();
        var sessions = await _sessionService.GetMySessionsAsync(JwtPayload.UserId, page, pageSize);
        return Ok(sessions);
    }

    [HttpGet("clubs/{clubId:guid}/evaluation-sessions")]
    public async Task<IActionResult> GetByClubId(Guid clubId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        CheckIsUserLoggedIn();
        var sessions = await _sessionService.GetByClubIdAsync(clubId, JwtPayload.UserId, page, pageSize);
        return Ok(sessions);
    }

    /// <summary>
    /// No session is public, so an anonymous caller is asked to sign in before anything is looked
    /// up — the same 401 whether or not the session exists.
    /// </summary>
    [HttpGet("evaluation-sessions/{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        CheckIsUserLoggedIn();
        var session = await _sessionService.GetByIdForUserAsync(id, JwtPayload.UserId);
        return Ok(session);
    }

    [HttpPost("evaluation-sessions")]
    public async Task<IActionResult> Create([FromBody] CreateEvaluationSessionDto request)
    {
        CheckIsUserLoggedIn();
        var session = await _sessionService.CreateAsync(request, JwtPayload.UserId);
        return CreatedAtAction(nameof(GetById), new { id = session.Id, version = "1.0" }, session);
    }

    [HttpPut("evaluation-sessions/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEvaluationSessionDto request)
    {
        CheckIsUserLoggedIn();
        var session = await _sessionService.UpdateAsync(id, request, JwtPayload.UserId);
        return Ok(session);
    }

    [HttpDelete("evaluation-sessions/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        CheckIsUserLoggedIn();
        await _sessionService.DeleteAsync(id, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPost("evaluation-sessions/{id:guid}/participants")]
    public async Task<IActionResult> AddParticipants(Guid id, [FromBody] AddParticipantsDto request)
    {
        CheckIsUserLoggedIn();
        var session = await _sessionService.AddParticipantsAsync(id, request, JwtPayload.UserId);
        return Ok(session);
    }

    [HttpDelete("evaluation-sessions/{id:guid}/participants/{participantId:guid}")]
    public async Task<IActionResult> RemoveParticipant(Guid id, Guid participantId)
    {
        CheckIsUserLoggedIn();
        var session = await _sessionService.RemoveParticipantAsync(id, participantId, JwtPayload.UserId);
        return Ok(session);
    }

    // Session lifecycle

    [HttpPost("evaluation-sessions/{id:guid}/start")]
    public async Task<IActionResult> Start(Guid id)
    {
        CheckIsUserLoggedIn();
        var session = await _lifecycleService.StartSessionAsync(id, JwtPayload.UserId);
        return Ok(session);
    }

    [HttpPost("evaluation-sessions/{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id)
    {
        CheckIsUserLoggedIn();
        var session = await _lifecycleService.PauseSessionAsync(id, JwtPayload.UserId);
        return Ok(session);
    }

    [HttpPost("evaluation-sessions/{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id)
    {
        CheckIsUserLoggedIn();
        var session = await _lifecycleService.ResumeSessionAsync(id, JwtPayload.UserId);
        return Ok(session);
    }

    [HttpPost("evaluation-sessions/{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id)
    {
        CheckIsUserLoggedIn();
        var session = await _lifecycleService.CompleteSessionAsync(id, JwtPayload.UserId);
        return Ok(session);
    }

    [HttpGet("evaluation-sessions/{id:guid}/progress")]
    public async Task<IActionResult> GetProgress(Guid id)
    {
        CheckIsUserLoggedIn();
        var progress = await _lifecycleService.GetSessionProgressAsync(id, JwtPayload.UserId);
        return Ok(progress);
    }

    [HttpPut("evaluation-sessions/{id:guid}/sharing")]
    public async Task<IActionResult> UpdateSharing(Guid id, [FromBody] UpdateSharingDto request)
    {
        CheckIsUserLoggedIn();
        await _lifecycleService.UpdateSharingAsync(id, request, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPut("evaluation-sessions/{id:guid}/evaluations/{evaluationId:guid}/sharing")]
    public async Task<IActionResult> UpdatePlayerSharing(Guid id, Guid evaluationId, [FromBody] UpdatePlayerSharingDto request)
    {
        CheckIsUserLoggedIn();
        await _lifecycleService.UpdatePlayerSharingAsync(id, evaluationId, request, JwtPayload.UserId);
        return NoContent();
    }

    // Groups

    [HttpPost("evaluation-sessions/{sessionId:guid}/groups")]
    public async Task<IActionResult> CreateGroup(Guid sessionId, [FromBody] CreateGroupDto request)
    {
        CheckIsUserLoggedIn();
        var group = await _groupService.CreateGroupAsync(sessionId, request, JwtPayload.UserId);
        return Ok(group);
    }

    [HttpPut("evaluation-sessions/{sessionId:guid}/groups/{groupId:guid}")]
    public async Task<IActionResult> UpdateGroup(Guid sessionId, Guid groupId, [FromBody] UpdateGroupDto request)
    {
        CheckIsUserLoggedIn();
        var group = await _groupService.UpdateGroupAsync(sessionId, groupId, request, JwtPayload.UserId);
        return Ok(group);
    }

    [HttpDelete("evaluation-sessions/{sessionId:guid}/groups/{groupId:guid}")]
    public async Task<IActionResult> DeleteGroup(Guid sessionId, Guid groupId)
    {
        CheckIsUserLoggedIn();
        await _groupService.DeleteGroupAsync(sessionId, groupId, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPost("evaluation-sessions/{sessionId:guid}/groups/auto-split")]
    public async Task<IActionResult> AutoSplitGroups(Guid sessionId, [FromBody] AutoSplitGroupsDto request)
    {
        CheckIsUserLoggedIn();
        var groups = await _groupService.AutoSplitGroupsAsync(sessionId, request, JwtPayload.UserId);
        return Ok(groups);
    }

    [HttpPost("evaluation-sessions/{sessionId:guid}/groups/{groupId:guid}/players")]
    public async Task<IActionResult> AddPlayerToGroup(Guid sessionId, Guid groupId, [FromBody] AssignPlayerToGroupDto request)
    {
        CheckIsUserLoggedIn();
        var group = await _groupService.AddPlayerToGroupAsync(sessionId, groupId, request, JwtPayload.UserId);
        return Ok(group);
    }

    [HttpDelete("evaluation-sessions/{sessionId:guid}/groups/{groupId:guid}/players/{playerId:guid}")]
    public async Task<IActionResult> RemovePlayerFromGroup(Guid sessionId, Guid groupId, Guid playerId)
    {
        CheckIsUserLoggedIn();
        await _groupService.RemovePlayerFromGroupAsync(sessionId, groupId, playerId, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPost("evaluation-sessions/{sessionId:guid}/groups/move-player")]
    public async Task<IActionResult> MovePlayer(Guid sessionId, [FromBody] MovePlayerDto request)
    {
        CheckIsUserLoggedIn();
        await _groupService.MovePlayerAsync(sessionId, request, JwtPayload.UserId);
        return NoContent();
    }

    // Scoring

    [HttpPost("evaluation-sessions/{sessionId:guid}/scores")]
    public async Task<IActionResult> SubmitScores(Guid sessionId, [FromBody] SubmitExerciseScoresDto request)
    {
        CheckIsUserLoggedIn();
        var score = await _scoringService.SubmitExerciseScoresAsync(sessionId, request, JwtPayload.UserId);
        return Ok(score);
    }

    [HttpGet("evaluation-sessions/{sessionId:guid}/scores")]
    public async Task<IActionResult> GetSessionScores(Guid sessionId)
    {
        CheckIsUserLoggedIn();
        var scores = await _scoringService.GetSessionScoresAsync(sessionId, JwtPayload.UserId);
        return Ok(scores);
    }

    [HttpGet("evaluation-sessions/{sessionId:guid}/groups/{groupId:guid}/exercises/{exerciseId:guid}/scores")]
    public async Task<IActionResult> GetGroupExerciseScores(Guid sessionId, Guid groupId, Guid exerciseId)
    {
        CheckIsUserLoggedIn();
        var scores = await _scoringService.GetGroupExerciseScoresAsync(sessionId, groupId, exerciseId, JwtPayload.UserId);
        return Ok(scores);
    }
}
