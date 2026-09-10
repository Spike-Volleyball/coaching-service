using Coaching.Application.Interfaces.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shared.Contracts.Grpc;
using Shared.Enums;

namespace Coaching.Infrastructure.Services;

/// <summary>
/// gRPC client for clubs-service with in-memory caching for club info.
/// Club info is cached for 5 minutes since it rarely changes.
/// </summary>
public class ClubsGrpcClient : IClubsGrpcClient
{
    private readonly ClubsInternalService.ClubsInternalServiceClient _grpcClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ClubsGrpcClient> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string CacheKeyPrefix = "club_info_";

    // Role NAMES, because that is what the wire carries — the role enums live in clubs-service.
    // Both lists mirror who holds feedback.give and unit.feedback.give in clubs-service's
    // PermissionMap, and must move with it. Once this service's shared pin carries the
    // `permissions` field that GetMembership and CheckUserClubRoles already answer with, ask for
    // the permission instead and delete both lists.
    //
    // Admin is here for feedback and not in PermissionMap: club admins could give feedback before
    // the permission model landed, and this fix is not the place to take that away.
    private static readonly HashSet<string> FeedbackGivingClubRoles =
        new(StringComparer.OrdinalIgnoreCase) { "Owner", "Admin", "HeadCoach", "Coach" };

    // Deliberately narrower than the unit staff set: a Manager (team Manager/Admin, group Admin)
    // runs the unit's logistics without coaching it, and a Helper was never authority at all.
    private static readonly HashSet<string> FeedbackGivingUnitRoles =
        new(StringComparer.OrdinalIgnoreCase) { "Coach", "AssistantCoach" };

    // Who may read a club's or a team's coaching material. Separate from the feedback sets above
    // because they answer a different question: feedback is about appraising a player, and this is
    // about running the side. A Manager or team Admin belongs here and not there; a Helper in
    // neither. Same caveat as the sets above — replace with the permission once the wire carries it.
    private static readonly HashSet<string> ClubStaffRoles =
        new(StringComparer.OrdinalIgnoreCase) { "Owner", "Admin", "HeadCoach", "Coach" };

    private static readonly HashSet<string> UnitStaffRoles =
        new(StringComparer.OrdinalIgnoreCase) { "Coach", "AssistantCoach", "Manager", "Admin" };

    public ClubsGrpcClient(
        ClubsInternalService.ClubsInternalServiceClient grpcClient,
        IMemoryCache cache,
        ILogger<ClubsGrpcClient> logger)
    {
        _grpcClient = grpcClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IDictionary<Guid, ClubInfo>> GetClubInfoAsync(IEnumerable<Guid> clubIds)
    {
        var uniqueIds = clubIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (uniqueIds.Count == 0)
            return new Dictionary<Guid, ClubInfo>();

        var result = new Dictionary<Guid, ClubInfo>();
        var uncachedIds = new List<Guid>();

        // Check cache first
        foreach (var clubId in uniqueIds)
        {
            var cacheKey = $"{CacheKeyPrefix}{clubId}";
            if (_cache.TryGetValue(cacheKey, out ClubInfo? cachedInfo) && cachedInfo != null)
            {
                result[clubId] = cachedInfo;
            }
            else
            {
                uncachedIds.Add(clubId);
            }
        }

        // Fetch uncached items from gRPC
        if (uncachedIds.Count > 0)
        {
            _logger.LogDebug("Skipping club info fetch - GetClubNamesAsync not yet implemented in clubs-service gRPC");
        }

        return result;
    }

    public async Task<ClubInfo?> GetClubInfoAsync(Guid clubId)
    {
        if (clubId == Guid.Empty)
            return null;

        var cacheKey = $"{CacheKeyPrefix}{clubId}";

        if (_cache.TryGetValue(cacheKey, out ClubInfo? cachedInfo))
            return cachedInfo;

        var result = await GetClubInfoAsync([clubId]);
        return result.TryGetValue(clubId, out var info) ? info : null;
    }

    public async Task<SkillMatrixInfo?> GetDefaultSkillMatrixAsync(Guid clubId)
    {
        if (clubId == Guid.Empty)
            return null;

        try
        {
            var response = await _grpcClient.GetSkillMatrixAsync(new GetSkillMatrixRequest
            {
                ClubId = clubId.ToString()
            });

            if (!response.Found)
                return null;

            return MapToSkillMatrixInfo(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get skill matrix from clubs-service for club {ClubId}", clubId);
            return null;
        }
    }

    public async Task<SkillMatrixInfo?> GetSkillMatrixByIdAsync(Guid matrixId)
    {
        // The current gRPC proto only supports GetSkillMatrix by clubId.
        // For now, return null - this can be updated when the proto is extended.
        _logger.LogWarning("GetSkillMatrixByIdAsync not yet supported via gRPC - matrix {MatrixId}", matrixId);
        return null;
    }

    public async Task<bool> IsUserClubMemberAsync(Guid userId, Guid clubId)
    {
        var response = await CheckClubRolesAsync(userId, clubId);
        return response?.IsMember ?? false;
    }

    public async Task<bool> IsUserCoachInClubAsync(Guid userId, Guid clubId)
    {
        var response = await CheckClubRolesAsync(userId, clubId);
        // Keep this aligned with clubs-service's ClubMemberExtensions.IsHeadCoachOrAbove.
        return response != null
            && response.IsMember
            && response.Roles.Any(r => r is "HeadCoach" or "Admin" or "Owner");
    }

    public async Task<bool> CanGiveFeedbackInClubAsync(Guid userId, Guid clubId)
    {
        var response = await CheckClubRolesAsync(userId, clubId);
        return response != null
            && response.IsMember
            && response.Roles.Any(FeedbackGivingClubRoles.Contains);
    }

    public async Task<bool> CanGiveFeedbackInUnitAsync(Guid userId, ContextType contextType, Guid contextId)
    {
        var response = await GetUnitMembershipAsync(userId, contextType, contextId);
        return response != null
            && response.IsMember
            && response.Roles.Any(FeedbackGivingUnitRoles.Contains);
    }

    public async Task<IReadOnlySet<Guid>> GetClubMemberIdsAsync(Guid clubId)
    {
        try
        {
            var response = await _grpcClient.GetClubMembersAsync(new GetClubMembersRequest
            {
                ClubId = clubId.ToString(),
                Audience = Shared.Contracts.Grpc.Audience.Members
            });
            return UserIdsOf(response.Members);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch club members via gRPC for club {ClubId}", clubId);
            return new HashSet<Guid>();
        }
    }

    public async Task<IReadOnlySet<Guid>> GetUnitMemberIdsAsync(ContextType contextType, Guid contextId)
    {
        try
        {
            var members = contextType switch
            {
                ContextType.Team => (await _grpcClient.GetTeamMembersAsync(new GetTeamMembersRequest
                {
                    TeamId = contextId.ToString(),
                    Audience = Shared.Contracts.Grpc.Audience.Members
                })).Members,
                ContextType.Group => (await _grpcClient.GetGroupMembersAsync(new GetGroupMembersRequest
                {
                    GroupId = contextId.ToString(),
                    Audience = Shared.Contracts.Grpc.Audience.Members
                })).Members,
                _ => throw new ArgumentOutOfRangeException(nameof(contextType), contextType,
                    "Only teams and groups have a unit roster")
            };
            return UserIdsOf(members);
        }
        catch (Exception ex) when (ex is not ArgumentOutOfRangeException)
        {
            _logger.LogError(ex, "Failed to fetch {ContextType} members via gRPC for context {ContextId}",
                contextType, contextId);
            return new HashSet<Guid>();
        }
    }

    private static HashSet<Guid> UserIdsOf(IEnumerable<MemberInfo> members) =>
        members.Select(m => Guid.Parse(m.UserId)).ToHashSet();

    public async Task<bool> IsClubStaffAsync(Guid userId, Guid clubId)
    {
        var response = await CheckClubRolesAsync(userId, clubId);
        return response != null
            && response.IsMember
            && response.Roles.Any(ClubStaffRoles.Contains);
    }

    public async Task<bool> IsUnitStaffAsync(Guid userId, ContextType contextType, Guid contextId)
    {
        var response = await GetUnitMembershipAsync(userId, contextType, contextId);
        return response != null
            && response.IsMember
            && response.Roles.Any(UnitStaffRoles.Contains);
    }

    public async Task<Guid?> ResolveClubIdAsync(ContextType contextType, Guid contextId)
    {
        try
        {
            var response = await _grpcClient.ResolveClubIdAsync(new ResolveClubIdRequest
            {
                ContextType = contextType.ToString(),
                ContextId = contextId.ToString()
            });

            return response.Found && Guid.TryParse(response.ClubId, out var clubId) ? clubId : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve the club of {ContextType} {ContextId} via gRPC",
                contextType, contextId);
            return null;
        }
    }

    private async Task<CheckUserClubRolesResponse?> CheckClubRolesAsync(Guid userId, Guid clubId)
    {
        try
        {
            return await _grpcClient.CheckUserClubRolesAsync(new CheckUserClubRolesRequest
            {
                UserId = userId.ToString(),
                ClubId = clubId.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check club roles via gRPC for user {UserId}, club {ClubId}",
                userId, clubId);
            return null;
        }
    }

    private async Task<GetMembershipResponse?> GetUnitMembershipAsync(
        Guid userId, ContextType contextType, Guid contextId)
    {
        try
        {
            return await _grpcClient.GetMembershipAsync(new GetMembershipRequest
            {
                ContextType = contextType.ToString(),
                ContextId = contextId.ToString(),
                UserId = userId.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to check {ContextType} membership via gRPC for user {UserId}, context {ContextId}",
                contextType, userId, contextId);
            return null;
        }
    }

    private static SkillMatrixInfo MapToSkillMatrixInfo(GetSkillMatrixResponse response)
    {
        return new SkillMatrixInfo(
            Guid.Parse(response.MatrixId),
            response.Skills.Select(s => new SkillMatrixInfo.SkillInfo(
                Guid.Parse(s.SkillId),
                s.Name,
                s.SkillKey,
                s.Bands.Select(b => new SkillMatrixInfo.BandInfo(
                    Guid.Parse(b.Id),
                    b.Order,
                    b.Label,
                    (decimal)b.MinScore,
                    (decimal)b.MaxScore
                )).ToList()
            )).ToList()
        );
    }
}
