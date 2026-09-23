using Coaching.Domain.Enums;
using Shared.Enums;

namespace Coaching.Application.DTOs.Feedback;

public class FeedbackDto
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public string? RecipientName { get; set; }
    public string? RecipientImageUrl { get; set; }
    public Guid CoachUserId { get; set; }
    public string? CoachName { get; set; }
    public string? CoachImageUrl { get; set; }
    public Guid? EventId { get; set; }

    /// <summary>
    /// The session this feedback was given at, when events-service could name it. Filled per page
    /// by the service rather than the mapper; null when the row has no event or the event is gone.
    /// EventId stays so a client can still open the event, or fetch it, without the summary.
    /// </summary>
    public FeedbackEventDto? Event { get; set; }
    public string? Comment { get; set; }
    public bool SharedWithPlayer { get; set; }
    public DateTime? SeenAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public List<ImprovementPointDto> ImprovementPoints { get; set; } = new();
    public List<FeedbackMediaDto> Attachments { get; set; } = new();
    public PraiseDto? Praise { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class FeedbackEventDto
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTime StartTime { get; set; }
    public required string Type { get; set; }
}

public class ImprovementPointDto
{
    public Guid Id { get; set; }
    public required string Description { get; set; }
    public int Order { get; set; }
    public List<AttachedDrillReferenceDto> AttachedDrills { get; set; } = new();
    public List<ImprovementPointMediaDto> MediaLinks { get; set; } = new();
}

public class AttachedDrillReferenceDto
{
    public Guid DrillId { get; set; }
    public string? Name { get; set; }
    public DrillCategory? Category { get; set; }
    public DrillIntensity? Intensity { get; set; }
}

public class ImprovementPointMediaDto
{
    public Guid Id { get; set; }
    public required string Url { get; set; }
    public FeedbackMediaType Type { get; set; }
    public string? Title { get; set; }
    public FeedbackMediaSource Source { get; set; }
}

public class FeedbackMediaDto
{
    public Guid Id { get; set; }
    public required string Url { get; set; }
    public FeedbackMediaType Type { get; set; }
    public string? Title { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// A file uploaded to our bucket, or a pasted link. Not stored on the row: the stored URL
    /// answers it, and an editor needs it to put a file in the uploader and a link in the link dialog.
    /// </summary>
    public FeedbackMediaSource Source { get; set; }
}

public record CreateFeedbackMediaDto
{
    public required string Url { get; set; }
    public FeedbackMediaType Type { get; set; }
    public string? Title { get; set; }
}

public class PraiseDto
{
    public Guid Id { get; set; }
    public required string Message { get; set; }
    public BadgeType? BadgeType { get; set; }
}

public record CreateFeedbackDto
{
    public Guid RecipientUserId { get; set; }
    public Guid? EventId { get; set; }
    public Guid? ClubId { get; set; }

    /// <summary>
    /// The team or group the feedback is being given in, when it is not tied to an event. A club
    /// alone cannot answer whether a team's coach may write to that team's players, because the
    /// coaching role lives on the unit and not on the club row.
    ///
    /// Both fields travel together and only Team and Group mean anything here; anything else
    /// falls through to <see cref="ClubId"/>. The club is resolved from the unit rather than read
    /// from the request, so the pair cannot be used to claim a club the unit does not belong to.
    /// </summary>
    public ContextType? ContextType { get; set; }
    public Guid? ContextId { get; set; }
    public string? Content { get; set; }
    public string? Comment { get; set; }
    public bool SharedWithPlayer { get; set; }
    public List<CreateImprovementPointDto>? ImprovementPoints { get; set; }
    public List<CreateFeedbackMediaDto>? Attachments { get; set; }
    public CreatePraiseDto? Praise { get; set; }
}

public record CreateImprovementPointDto
{
    public required string Description { get; set; }
    public List<Guid>? DrillIds { get; set; }
    public List<CreateImprovementPointMediaDto>? MediaLinks { get; set; }
}

public record CreateImprovementPointMediaDto
{
    public required string Url { get; set; }
    public FeedbackMediaType Type { get; set; }
    public string? Title { get; set; }
    public FeedbackMediaSource Source { get; set; }
}

public record CreatePraiseDto
{
    public required string Message { get; set; }
    public BadgeType? BadgeType { get; set; }
}

public record UpdateFeedbackDto
{
    public string? Content { get; set; }
    public string? Comment { get; set; }
    public bool? SharedWithPlayer { get; set; }

    /// <summary>
    /// The feedback's own attachments as they should stand after the edit, in order; null leaves
    /// them as they are. An entry naming one already on this feedback keeps it, a new one arrives
    /// without an id, and any left out is removed.
    /// </summary>
    public List<UpdateFeedbackMediaDto>? Attachments { get; set; }
}

/// <summary>
/// One attachment in an edit: <see cref="CreateFeedbackMediaDto"/> plus the id of the attachment it
/// keeps. A kept attachment takes its title, type and position from the entry but never its URL —
/// a read hands out presigned URLs, and writing one back would store a link that expires, taking
/// the player's file with it. A changed link is therefore a new entry, and the old one is left out.
/// </summary>
public record UpdateFeedbackMediaDto : CreateFeedbackMediaDto
{
    public Guid? Id { get; set; }
}

public record AddImprovementPointDto
{
    public required string Description { get; set; }
    public int? Order { get; set; }
    public List<Guid>? DrillIds { get; set; }
    public List<CreateImprovementPointMediaDto>? MediaLinks { get; set; }
}

public record UpdateImprovementPointDto
{
    public string? Description { get; set; }
}

public record UpdatePraiseDto
{
    public string? Message { get; set; }
    public BadgeType? BadgeType { get; set; }
}

public class FeedbackMediaUploadRequestDto
{
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required long FileSize { get; set; }
}

public class FeedbackMediaUploadResponseDto
{
    public string UploadUrl { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
}

public class FeedbackListResponseDto
{
    public IEnumerable<FeedbackDto> Items { get; set; } = new List<FeedbackDto>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
