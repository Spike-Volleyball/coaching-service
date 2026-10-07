using System.Linq.Expressions;
using Coaching.Domain.Enums;
using Shared.Models;

namespace Coaching.Domain.Models.Templates;

public class TrainingPlan : BaseEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 10000;

    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid CreatedByUserId { get; set; }
    public virtual UserProfile? Creator { get; set; }
    public Guid? ClubId { get; set; }
    public TemplateVisibility Visibility { get; set; } = TemplateVisibility.Private;
    public DifficultyLevel Level { get; set; } = DifficultyLevel.Intermediate;
    public PlanType PlanType { get; set; } = PlanType.Template;
    public Guid? EventId { get; set; }       // Cross-service ref to events-service, no FK
    public Guid? SourceTemplateId { get; set; } // No FK - analytics only

    // Denormalized aggregates
    public int TotalDuration { get; set; }

    /// <summary>
    /// Total minus breaks and meetings. Kept beside TotalDuration because a coach budgets
    /// against both: the slot the gym is booked for, and the time actually spent coaching.
    /// </summary>
    public int CoachedDuration { get; set; }
    public int LikeCount { get; set; }
    public int UsageCount { get; set; }

    // Navigation properties
    public virtual ICollection<PlanSection> Sections { get; set; } = new List<PlanSection>();
    public virtual ICollection<PlanItem> Items { get; set; } = new List<PlanItem>();
    public virtual ICollection<PlanLike> Likes { get; set; } = new List<PlanLike>();
    public virtual ICollection<PlanBookmark> Bookmarks { get; set; } = new List<PlanBookmark>();
    public virtual ICollection<PlanComment> Comments { get; set; } = new List<PlanComment>();

    /// <summary>
    /// The coaches working this plan. Only an event's plan has them: a template is a shape to
    /// reuse, and who runs it is not known until there is an event to run it at.
    /// </summary>
    public virtual ICollection<PlanCoach> Coaches { get; set; } = new List<PlanCoach>();
    /// What this plan's uses of a drill decided its dials should say. Held by the plan rather
    /// than by the items, which a save deletes and recreates. See <see cref="PlanItemDialValue"/>.
    /// </summary>
    public virtual ICollection<PlanItemDialValue> DialValues { get; set; } = new List<PlanItemDialValue>();
    /// <summary>The courts this session has, per venue. Only an event's plan has any.</summary>
    public virtual ICollection<PlanCourtBooking> CourtBookings { get; set; } = new List<PlanCourtBooking>();

    /// <summary>Where each activity happens, per venue. Only an event's plan has any.</summary>
    public virtual ICollection<PlanItemPlacement> Placements { get; set; } = new List<PlanItemPlacement>();

    /// <summary>
    /// Who may read a template: anyone when it is public; when it is private, its author and the
    /// members of its club, who read the club's private drills the same way (SPI-6900). Its likes,
    /// bookmarks and comments follow the same rule, and so do the lists built from them, so a
    /// template taken private leaves everyone else's (SPI-6801).
    /// </summary>
    /// <param name="memberClubIds">The reader's clubs, which only clubs-service knows.</param>
    public static Expression<Func<TrainingPlan, bool>> TemplateReadableBy(Guid userId, IReadOnlyCollection<Guid> memberClubIds) =>
        plan => !plan.IsDeleted
            && plan.PlanType == PlanType.Template
            && (plan.Visibility == TemplateVisibility.Public
                || plan.CreatedByUserId == userId
                || (plan.ClubId != null && memberClubIds.Contains(plan.ClubId.Value)));
}
