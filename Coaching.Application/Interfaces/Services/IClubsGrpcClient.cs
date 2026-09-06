using Shared.Enums;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// Club information returned from clubs-service
/// </summary>
public record ClubInfo(string Name, string? LogoUrl);

/// <summary>
/// gRPC client for fetching club information from clubs-service.
/// </summary>
public interface IClubsGrpcClient
{
    /// <summary>
    /// Get club info for multiple club IDs in a single batch request.
    /// </summary>
    Task<IDictionary<Guid, ClubInfo>> GetClubInfoAsync(IEnumerable<Guid> clubIds);

    /// <summary>
    /// Get a single club info by ID.
    /// </summary>
    Task<ClubInfo?> GetClubInfoAsync(Guid clubId);

    /// <summary>
    /// Get the default skill matrix for a club via gRPC from clubs-service.
    /// Returns null if no default matrix exists.
    /// </summary>
    Task<SkillMatrixInfo?> GetDefaultSkillMatrixAsync(Guid clubId);

    /// <summary>
    /// Get a skill matrix by ID via gRPC from clubs-service.
    /// Returns null if the matrix doesn't exist.
    /// </summary>
    Task<SkillMatrixInfo?> GetSkillMatrixByIdAsync(Guid matrixId);

    /// <summary>
    /// Check whether a user is an active member of a club (any role).
    /// </summary>
    Task<bool> IsUserClubMemberAsync(Guid userId, Guid clubId);

    /// <summary>
    /// Check whether a user is an active member with a coaching role
    /// (HeadCoach, Admin, or Owner) in a club.
    /// </summary>
    Task<bool> IsUserCoachInClubAsync(Guid userId, Guid clubId);

    /// <summary>
    /// Whether a user's club roles let them give feedback anywhere in that club.
    /// </summary>
    Task<bool> CanGiveFeedbackInClubAsync(Guid userId, Guid clubId);

    /// <summary>
    /// Whether a user's roles on one team or group let them give feedback to its players.
    /// Reads that unit's own roles only — club standing is a separate question, asked of
    /// <see cref="CanGiveFeedbackInClubAsync"/> against the club that owns the unit.
    /// </summary>
    Task<bool> CanGiveFeedbackInUnitAsync(Guid userId, ContextType contextType, Guid contextId);

    /// <summary>
    /// Check whether a user belongs to one team or group.
    /// </summary>
    Task<bool> IsUserUnitMemberAsync(Guid userId, ContextType contextType, Guid contextId);

    /// <summary>
    /// Whether a user is staff of a club — someone who runs or coaches it, rather than plays for it.
    /// This is the question coaching material asks before showing itself.
    /// </summary>
    Task<bool> IsClubStaffAsync(Guid userId, Guid clubId);

    /// <summary>
    /// Whether a user is staff of one team or group. Wider than
    /// <see cref="CanGiveFeedbackInUnitAsync"/>: a manager runs the team without coaching its
    /// players, which is authority enough to read its plays and not enough to appraise them.
    /// </summary>
    Task<bool> IsUnitStaffAsync(Guid userId, ContextType contextType, Guid contextId);

    /// <summary>
    /// The club that owns a team or group. Null when the unit does not exist.
    /// </summary>
    Task<Guid?> ResolveClubIdAsync(ContextType contextType, Guid contextId);
}

/// <summary>
/// Skill matrix information returned from clubs-service gRPC
/// </summary>
public record SkillMatrixInfo(
    Guid MatrixId,
    List<SkillMatrixInfo.SkillInfo> Skills)
{
    public record SkillInfo(
        Guid Id,
        string Name,
        string SkillKey,
        List<BandInfo> Bands);

    public record BandInfo(
        Guid Id,
        int Order,
        string Label,
        decimal MinScore,
        decimal MaxScore);
}
