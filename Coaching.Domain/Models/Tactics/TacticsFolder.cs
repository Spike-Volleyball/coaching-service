using Coaching.Domain.Enums;
using Shared.Models;

namespace Coaching.Domain.Models.Tactics;

/// <summary>
/// A folder on one shelf. Folders do not travel between shelves — a club's folder is part of the
/// club's library, and moving it would move every board filed in it.
///
/// They do nest, and they keep the order a coach put them in: a season's filing is a shape the
/// coach chose, and alphabetical order is not that shape. Nesting stops at <see cref="MaxDepth"/>
/// levels, which is as deep as the rail can indent and still read.
/// </summary>
public class TacticsFolder : BaseEntity
{
    public const int NameMaxLength = 80;

    /// <summary>Levels of nesting allowed, counting the top level as one.</summary>
    public const int MaxDepth = 3;

    public required string Name { get; set; }

    public TacticsScope Scope { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? ClubId { get; set; }
    public Guid? TeamId { get; set; }

    /// <summary>The folder this one sits inside, or null for the top level of its shelf.</summary>
    public Guid? ParentFolderId { get; set; }
    public virtual TacticsFolder? ParentFolder { get; set; }
    public virtual ICollection<TacticsFolder> Children { get; set; } = new List<TacticsFolder>();

    /// <summary>Where it sits among its siblings. Dense from zero, rewritten on every move.</summary>
    public int Position { get; set; }

    public virtual ICollection<TacticsBoard> Boards { get; set; } = new List<TacticsBoard>();
}
