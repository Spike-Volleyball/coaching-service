using AutoMapper;
using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class BadgeService(
    IRepository<PlayerBadge> badgeRepository,
    IMapper mapper) : IBadgeService
{
    public const int MaxPageSize = 50;

    public async Task<PlayerBadgeDto> AwardBadgeAsync(AwardBadgeDto request, Guid awardedByUserId)
    {
        if (request.PraiseId == null && request.EventId == null)
            throw new BadRequestException("Either PraiseId or EventId must be provided", Shared.Enums.ErrorCodeEnum.ValidationError);

        var badge = new PlayerBadge
        {
            UserId = request.UserId,
            PraiseId = request.PraiseId,
            EventId = request.EventId,
            BadgeType = request.BadgeType,
            Message = request.Message,
            AwardedByUserId = awardedByUserId
        };

        badgeRepository.Add(badge);
        await badgeRepository.SaveChangesAsync();

        return MapToDto(badge);
    }

    public async Task<IEnumerable<PlayerBadgeDto>> GetPlayerBadgesAsync(Guid userId, int page = 1, int pageSize = 20)
    {
        var badges = await badgeRepository.Query()
            .Where(b => b.UserId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(Math.Clamp(pageSize, 1, MaxPageSize))
            .ToListAsync();

        return badges.Select(MapToDto);
    }

    public async Task<BadgeStatsDto> GetPlayerBadgeStatsAsync(Guid userId)
    {
        var badges = await badgeRepository.Query()
            .Where(b => b.UserId == userId)
            .ToListAsync();

        var badgesByType = badges
            .GroupBy(b => b.BadgeType)
            .ToDictionary(g => g.Key, g => g.Count());

        var mostCommonBadge = badgesByType.Any()
            ? badgesByType.OrderByDescending(kvp => kvp.Value).First().Key
            : (BadgeType?)null;

        var latestBadge = badges
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefault();

        return new BadgeStatsDto
        {
            TotalBadges = badges.Count,
            BadgesByType = badgesByType,
            MostCommonBadge = mostCommonBadge,
            LatestBadge = latestBadge != null ? MapToDto(latestBadge) : null
        };
    }

    public async Task<IEnumerable<PlayerBadgeDto>> GetRecentBadgesAsync(Guid userId, int limit = 10)
    {
        var badges = await badgeRepository.Query()
            .Where(b => b.UserId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToListAsync();

        return badges.Select(MapToDto);
    }

    private static PlayerBadgeDto MapToDto(PlayerBadge badge)
    {
        var (name, description, icon) = BadgeMetadata.GetBadgeInfo(badge.BadgeType);

        return new PlayerBadgeDto
        {
            Id = badge.Id,
            UserId = badge.UserId,
            PraiseId = badge.PraiseId,
            EventId = badge.EventId,
            BadgeType = badge.BadgeType,
            BadgeName = name,
            BadgeDescription = description,
            BadgeIcon = icon,
            Message = badge.Message,
            AwardedByUserId = badge.AwardedByUserId,
            CreatedAt = badge.CreatedAt
        };
    }
}
