using Asp.Versioning;
using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.DataAccess.Providers.Interfaces;
using Shared.Enums;

namespace Coaching.Controllers;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class FeedbackController : Shared.Microservices.Controllers.BaseApiController
{
    private readonly IFeedbackService _feedbackService;
    private readonly IFeedbackAuthorizationService _authorizationService;

    public FeedbackController(
        IFeedbackService feedbackService,
        IFeedbackAuthorizationService authorizationService,
        IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _feedbackService = feedbackService;
        _authorizationService = authorizationService;
    }

    [HttpGet("feedback/{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.GetByIdAsync(id, JwtPayload.UserId);
        if (feedback == null) return NotFound();
        return Ok(feedback);
    }

    [HttpGet("events/{eventId:guid}/feedback")]
    public async Task<IActionResult> GetByEventId(Guid eventId)
    {
        CheckIsUserLoggedIn();
        var feedbacks = await _feedbackService.GetByEventIdAsync(eventId, JwtPayload.UserId);
        return Ok(feedbacks);
    }

    /// <summary>
    /// Lightweight authorization check: can the current user create feedback?
    /// Returns only a boolean — denial reasons are logged server-side.
    ///
    /// A team or group must be named through contextType/contextId when the answer depends on it:
    /// a coach of one team holds that role on the team, so a club-only question answers no for
    /// them and their "Give feedback" button never appears.
    /// </summary>
    [HttpGet("feedback/can-create")]
    public async Task<IActionResult> CanCreate(
        [FromQuery] Guid recipientUserId,
        [FromQuery] Guid? eventId = null,
        [FromQuery] Guid? clubId = null,
        [FromQuery] ContextType? contextType = null,
        [FromQuery] Guid? contextId = null)
    {
        CheckIsUserLoggedIn();

        var eligible = await _authorizationService.GetEligibleRecipientsAsync(
            new FeedbackScope(eventId, clubId, contextType, contextId),
            [recipientUserId],
            JwtPayload.UserId);
        return Ok(new { canCreate = eligible.Contains(recipientUserId) });
    }

    /// <summary>
    /// The same question for a whole roster at once, answered with the recipients the current
    /// user may give feedback to. One request per member list rather than one per member: the
    /// caller's standing and the roster are resolved once and every recipient is judged against
    /// them. Repeat recipientUserIds in the query; at most 100 distinct ids per request.
    /// </summary>
    [HttpGet("feedback/can-create/batch")]
    public async Task<IActionResult> CanCreateBatch(
        [FromQuery] Guid[] recipientUserIds,
        [FromQuery] Guid? eventId = null,
        [FromQuery] Guid? clubId = null,
        [FromQuery] ContextType? contextType = null,
        [FromQuery] Guid? contextId = null)
    {
        CheckIsUserLoggedIn();

        var eligibleRecipientIds = await _authorizationService.GetEligibleRecipientsAsync(
            new FeedbackScope(eventId, clubId, contextType, contextId),
            recipientUserIds,
            JwtPayload.UserId);
        return Ok(new { eligibleRecipientIds });
    }

    [HttpGet("me/feedback/received")]
    public async Task<IActionResult> GetReceivedFeedback([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        // Prevent unbounded result sets
        pageSize = Math.Clamp(pageSize, 1, 100);

        CheckIsUserLoggedIn();
        var result = await _feedbackService.GetReceivedFeedbackAsync(JwtPayload.UserId, page, pageSize);
        return Ok(result);
    }

    [HttpGet("me/feedback/given")]
    public async Task<IActionResult> GetGivenFeedback([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        // Prevent unbounded result sets
        pageSize = Math.Clamp(pageSize, 1, 100);

        CheckIsUserLoggedIn();
        var result = await _feedbackService.GetGivenFeedbackAsync(JwtPayload.UserId, page, pageSize);
        return Ok(result);
    }

    [HttpGet("me/feedback/unseen-count")]
    public async Task<IActionResult> GetUnseenCount()
    {
        CheckIsUserLoggedIn();
        var count = await _feedbackService.GetUnseenCountAsync(JwtPayload.UserId);
        return Ok(new { count });
    }

    [HttpPost("feedback/{id:guid}/seen")]
    public async Task<IActionResult> MarkSeen(Guid id)
    {
        CheckIsUserLoggedIn();
        await _feedbackService.MarkSeenAsync(id, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPost("feedback/media/upload-url")]
    public async Task<IActionResult> GetMediaUploadUrl([FromBody] FeedbackMediaUploadRequestDto request)
    {
        CheckIsUserLoggedIn();
        var response = await _feedbackService.GetMediaUploadUrlAsync(request, JwtPayload.UserId);
        return Ok(response);
    }

    [HttpPost("feedback")]
    public async Task<IActionResult> Create([FromBody] CreateFeedbackDto request)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.CreateAsync(request, JwtPayload.UserId);
        return CreatedAtAction(nameof(GetById), new { id = feedback.Id, version = "1.0" }, feedback);
    }

    [HttpPut("feedback/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateFeedbackDto request)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.UpdateAsync(id, request, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpDelete("feedback/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        CheckIsUserLoggedIn();
        await _feedbackService.DeleteAsync(id, JwtPayload.UserId);
        return NoContent();
    }

    [HttpPut("feedback/{id:guid}/share")]
    public async Task<IActionResult> ShareWithPlayer(Guid id, [FromQuery] bool share = true)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.ShareWithPlayerAsync(id, share, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpPost("feedback/{id:guid}/improvement-points")]
    public async Task<IActionResult> AddImprovementPoint(Guid id, [FromBody] AddImprovementPointDto request)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.AddImprovementPointAsync(id, request, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpPut("feedback/{id:guid}/improvement-points/{pointId:guid}")]
    public async Task<IActionResult> UpdateImprovementPoint(Guid id, Guid pointId, [FromBody] UpdateImprovementPointDto request)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.UpdateImprovementPointAsync(id, pointId, request, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpDelete("feedback/{id:guid}/improvement-points/{pointId:guid}")]
    public async Task<IActionResult> RemoveImprovementPoint(Guid id, Guid pointId)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.RemoveImprovementPointAsync(id, pointId, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpPost("feedback/{id:guid}/improvement-points/{pointId:guid}/drills/{drillId:guid}")]
    public async Task<IActionResult> AddDrillToPoint(Guid id, Guid pointId, Guid drillId)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.AddDrillToPointAsync(id, pointId, drillId, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpDelete("feedback/{id:guid}/improvement-points/{pointId:guid}/drills/{drillId:guid}")]
    public async Task<IActionResult> RemoveDrillFromPoint(Guid id, Guid pointId, Guid drillId)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.RemoveDrillFromPointAsync(id, pointId, drillId, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpPost("feedback/{id:guid}/improvement-points/{pointId:guid}/media")]
    public async Task<IActionResult> AddMediaToPoint(Guid id, Guid pointId, [FromBody] CreateImprovementPointMediaDto request)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.AddMediaToPointAsync(id, pointId, request, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpDelete("feedback/{id:guid}/improvement-points/{pointId:guid}/media/{mediaId:guid}")]
    public async Task<IActionResult> RemoveMediaFromPoint(Guid id, Guid pointId, Guid mediaId)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.RemoveMediaFromPointAsync(id, pointId, mediaId, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpPost("feedback/{id:guid}/praise")]
    public async Task<IActionResult> AddPraise(Guid id, [FromBody] CreatePraiseDto request)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.AddPraiseAsync(id, request, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpPut("feedback/{id:guid}/praise")]
    public async Task<IActionResult> UpdatePraise(Guid id, [FromBody] UpdatePraiseDto request)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.UpdatePraiseAsync(id, request, JwtPayload.UserId);
        return Ok(feedback);
    }

    [HttpDelete("feedback/{id:guid}/praise")]
    public async Task<IActionResult> RemovePraise(Guid id)
    {
        CheckIsUserLoggedIn();
        var feedback = await _feedbackService.RemovePraiseAsync(id, JwtPayload.UserId);
        return Ok(feedback);
    }
}
