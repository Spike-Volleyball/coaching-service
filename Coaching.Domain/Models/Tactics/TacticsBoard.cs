using Coaching.Domain.Enums;
using Shared.Models;

namespace Coaching.Domain.Models.Tactics;

/// <summary>
/// A tactics board: the scenes a coach drew, stored whole as the JSON document the editor holds,
/// with the fields the library needs to list it and the server needs to authorise it alongside.
///
/// Keeping the document opaque is deliberate. The board format grows every time the editor gains
/// a tool, and none of those changes is the database's business.
/// </summary>
public class TacticsBoard : BaseEntity
{
    public const int TitleMaxLength = 120;
    public const int CategoryMaxLength = 60;
    public const int SystemMaxLength = 20;

    public required string Title { get; set; }
    public required string Category { get; set; }

    /// <summary>The rotation system the board was drawn for — "5-1", "4-2", "6-2".</summary>
    public required string System { get; set; }

    public TacticsScope Scope { get; set; }

    /// <summary>Who created it. On a personal shelf this is also the only person who may read it.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Set on club and team boards; a team's club is stored too, so listing needs no gRPC hop.</summary>
    public Guid? ClubId { get; set; }
    public Guid? TeamId { get; set; }

    public Guid? FolderId { get; set; }
    public virtual TacticsFolder? Folder { get; set; }

    public bool IsFavorite { get; set; }

    /// <summary>Denormalised from the document so the rail can say "4 scenes" without parsing it.</summary>
    public int FrameCount { get; set; }

    /// <summary>
    /// Bumped on every write. A save that names an older version is refused rather than allowed to
    /// overwrite whatever the other coach did in the meantime.
    /// </summary>
    public int Version { get; set; }

    /// <summary>The whole board as JSON, exactly as the editor holds it.</summary>
    public required string Document { get; set; }
}
