using Coaching.Domain.Enums;
using Shared.Models;

namespace Coaching.Domain.Models.Tactics;

/// <summary>
/// A folder on one shelf. Folders do not nest and do not travel between shelves — a club's folder
/// is part of the club's library, and moving it would move every board filed in it.
/// </summary>
public class TacticsFolder : BaseEntity
{
    public const int NameMaxLength = 80;

    public required string Name { get; set; }

    public TacticsScope Scope { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid? ClubId { get; set; }
    public Guid? TeamId { get; set; }

    public virtual ICollection<TacticsBoard> Boards { get; set; } = new List<TacticsBoard>();
}
