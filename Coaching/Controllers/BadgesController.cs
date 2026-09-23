using Asp.Versioning;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.DataAccess.Providers.Interfaces;
using Shared.Security.Access;

namespace Coaching.Controllers;

[ApiVersion("1.0")]
[Route("v{version:apiVersion}")]
public class BadgesController : Shared.Microservices.Controllers.BaseApiController
{
    private readonly IBadgeService _badgeService;

    public BadgesController(IBadgeService badgeService, IJwtPayloadProvider jwtPayloadProvider)
        : base(jwtPayloadProvider)
    {
        _badgeService = badgeService;
    }

    [HttpGet("me/badges")]
    public async Task<IActionResult> GetMyBadges([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        CheckIsUserLoggedIn();
        var badges = await _badgeService.GetPlayerBadgesAsync(JwtPayload.UserId, page, pageSize);
        return Ok(badges);
    }

    [HttpGet("me/badges/stats")]
    public async Task<IActionResult> GetMyBadgeStats()
    {
        CheckIsUserLoggedIn();
        var stats = await _badgeService.GetPlayerBadgeStatsAsync(JwtPayload.UserId);
        return Ok(stats);
    }

    [HttpGet("me/badges/recent")]
    public async Task<IActionResult> GetMyRecentBadges([FromQuery] int limit = 10)
    {
        CheckIsUserLoggedIn();
        var badges = await _badgeService.GetRecentBadgesAsync(JwtPayload.UserId, limit);
        return Ok(badges);
    }

    [HttpGet("users/{userId:guid}/badges")]
    [NoResourceScope("A user's badges show on their profile to any signed-in viewer; who may see a profile is profiles-service's rule, not asked here yet")]
    public async Task<IActionResult> GetUserBadges([FromRoute] Guid userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var badges = await _badgeService.GetPlayerBadgesAsync(userId, page, pageSize);
        return Ok(badges);
    }
}
