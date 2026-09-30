using Coaching.Domain.Enums;
using Shared.Models;

namespace Coaching.Domain.Models.Templates;

public class TrainingPlanRun : BaseEntity
{
    public Guid PlanId { get; set; }
    public Guid EventId { get; set; }
    public Guid StartedByUserId { get; set; }
    public RunStatus Status { get; set; }

    public Guid? CurrentItemId { get; set; }

    // Virtual start of the current item's timer; set while Running, cleared while Paused.
    public DateTime? CurrentItemStartedAtUtc { get; set; }

    // Elapsed seconds captured at pause; authoritative while Paused.
    public int CurrentItemPausedElapsedSeconds { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    // Moves on to the next step by itself when a step's planned time is up. The last step never
    // does: it runs into overtime until a coach finishes the session.
    public bool AutoAdvance { get; set; }

    // Postgres's own row version: a write made from a read another phone has since overtaken is refused.
    public uint RowVersion { get; set; }

    public virtual TrainingPlan Plan { get; set; } = null!;
    public virtual ICollection<TrainingPlanRunItem> Items { get; set; } = new List<TrainingPlanRunItem>();
}
