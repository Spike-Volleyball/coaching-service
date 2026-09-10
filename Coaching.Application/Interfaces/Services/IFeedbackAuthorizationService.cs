using Coaching.Application.DTOs.Feedback;
using Shared.Enums;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// The place feedback is being given in: an event, a team or group, or a club. Everything the
/// rules ask about the caller depends on the scope alone, so one scope answers for every
/// recipient a roster screen asks about.
/// </summary>
public readonly record struct FeedbackScope(Guid? EventId, Guid? ClubId, ContextType? ContextType, Guid? ContextId)
{
    public static FeedbackScope Of(CreateFeedbackDto request) =>
        new(request.EventId, request.ClubId, request.ContextType, request.ContextId);
}

/// <summary>
/// Validates authorization for feedback operations.
/// Throws ForbiddenException with descriptive message if not authorized.
/// </summary>
public interface IFeedbackAuthorizationService
{
    /// <summary>
    /// Validates the user can create feedback for the given request.
    /// Returns the resolved ClubId — from the event's context when the feedback is event-linked,
    /// from the owning club when a team or group is named, and from the request when a club is
    /// all it carries — so FeedbackService can use it without making a duplicate gRPC call.
    /// </summary>
    Task<Guid?> ValidateCreateAsync(CreateFeedbackDto request, Guid userId);

    /// <summary>
    /// Which of these recipients the user may give feedback to in this scope, judged by the same
    /// rules as <see cref="ValidateCreateAsync"/>. The caller's standing and the roster are
    /// fetched once for the whole list; denial reasons are logged server-side. Duplicate ids
    /// answer once. More than <see cref="MaxRecipientsPerBatch"/> distinct ids is rejected
    /// with a validation error.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetEligibleRecipientsAsync(
        FeedbackScope scope, IReadOnlyCollection<Guid> recipientUserIds, Guid userId);

    const int MaxRecipientsPerBatch = 100;
}
