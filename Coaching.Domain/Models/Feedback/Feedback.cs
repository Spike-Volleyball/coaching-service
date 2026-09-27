using Coaching.Domain.Models.Evaluation;
using Shared.Models;

namespace Coaching.Domain.Models.Feedback;

public class Feedback : BaseEntity
{
    public Guid RecipientUserId { get; set; }
    public Guid CoachUserId { get; set; }
    public Guid? EventId { get; set; }           // Cross-service reference, no FK
    public Guid? ClubId { get; set; }            // Club context for standalone feedback
    public Guid? EvaluationId { get; set; }      // Link to PlayerEvaluation (future use)
    public string? Content { get; set; }          // HTML rich text from Tiptap (sanitized)
    public string? ContentPlainText { get; set; } // Stripped text for search/preview
    public bool SharedWithPlayer { get; set; }

    // Set once when the recipient first opens shared feedback (read receipt). Null = unseen.
    /// <summary>First time the recipient opened it — the read receipt, never overwritten.</summary>
    public DateTime? SeenAt { get; set; }

    /// <summary>Most recent time the recipient opened it.</summary>
    public DateTime? LastSeenAt { get; set; }

    // Phase A backward compat: Comment column kept in DB, mapped by EF.
    // Code writes to both Content and Comment to keep them in sync.
    // The Comment column will be dropped in Phase B (next release).
    public string? Comment { get; set; }

    public virtual PlayerEvaluation? Evaluation { get; set; }
    public virtual ICollection<ImprovementPoint> ImprovementPoints { get; set; } = new List<ImprovementPoint>();
    public virtual ICollection<FeedbackMedia> Media { get; set; } = new List<FeedbackMedia>();
    public virtual Praise? Praise { get; set; }

    /// <summary>
    /// The praise the feedback carries now. Removing praise only marks its row deleted, so
    /// <see cref="Praise"/> alone can still hold one that was taken back.
    /// </summary>
    public Praise? LivePraise() => Praise is { IsDeleted: false } praise ? praise : null;
}
