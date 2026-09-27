using Coaching.Application.DTOs.Feedback;
using Coaching.Domain.Enums;

namespace Coaching.Application.Interfaces.Services;

public interface IBadgeService
{
    Task<PlayerBadgeDto> AwardBadgeAsync(AwardBadgeDto request, Guid awardedByUserId);
    Task<IEnumerable<PlayerBadgeDto>> GetPlayerBadgesAsync(Guid userId, int page = 1, int pageSize = 20);
    Task<BadgeStatsDto> GetPlayerBadgeStatsAsync(Guid userId);
    Task<IEnumerable<PlayerBadgeDto>> GetRecentBadgesAsync(Guid userId, int limit = 10);
}
