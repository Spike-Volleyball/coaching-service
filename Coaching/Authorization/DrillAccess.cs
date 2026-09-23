namespace Coaching.Authorization;

/// <summary>What a caller may do with a drill, as its authority answers.</summary>
public enum DrillAccess
{
    /// <summary>See the drill and what hangs off it: comments, likes, attachments. Acting on any of them starts here too.</summary>
    Read,
}
