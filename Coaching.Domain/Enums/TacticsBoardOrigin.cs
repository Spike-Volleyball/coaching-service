namespace Coaching.Domain.Enums;

/// <summary>Where a tactics board came from.</summary>
public enum TacticsBoardOrigin
{
    /// <summary>Not recorded: every board from before origins were kept.</summary>
    Unrecorded = 0,

    /// <summary>One of the sample boards a new shelf is seeded with.</summary>
    Starter = 1,

    /// <summary>Somebody made it: a new board, a copy of one, an import.</summary>
    Made = 2
}
