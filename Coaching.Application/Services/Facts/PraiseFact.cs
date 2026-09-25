using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using Shared.Messaging.Contracts.Events.Coaching;

namespace Coaching.Application.Services.Facts;

/// <summary>
/// What a player holds from one piece of feedback, as rewards-service counts it: the feedback, once
/// it is shared with them, and the badge its praise carries, if any. Its snapshot is
/// <see cref="PraiseGivenEvent"/>, published whenever this changes.
/// </summary>
public readonly record struct PraiseFact(PraiseState State, BadgeType? Badge)
{
    /// <summary>Nothing held: the feedback is private, deleted, or not written yet.</summary>
    public static readonly PraiseFact None = new(PraiseState.Withdrawn, null);

    public static PraiseFact Of(Feedback feedback) =>
        feedback is { SharedWithPlayer: true, IsDeleted: false }
            ? new PraiseFact(PraiseState.Given, feedback.LivePraise()?.BadgeType)
            : None;

    public PraiseGivenEvent Snapshot(Feedback feedback, DateTime snapshotAt) => new()
    {
        FeedbackId = feedback.Id,
        CoachUserId = feedback.CoachUserId,
        PlayerUserId = feedback.RecipientUserId,
        State = State,
        Badge = Badge?.ToString(),
        GivenAt = feedback.CreatedAt ?? snapshotAt,
        SnapshotAt = snapshotAt,
    };
}
