using System.Runtime.Serialization;

namespace Coaching.Domain.Enums;

/// <summary>
/// Which shelf a board or folder sits on. A board belongs to exactly one, and the scope decides
/// who may see it: your own, a club's staff, or one team's staff.
/// </summary>
public enum TacticsScope
{
    // The wire values are the client's, lowercase, because they are also written into every
    // exported board file and cannot be renamed without breaking the files already in the wild.
    [EnumMember(Value = "personal")]
    Personal = 0,

    [EnumMember(Value = "club")]
    Club = 1,

    [EnumMember(Value = "team")]
    Team = 2
}
