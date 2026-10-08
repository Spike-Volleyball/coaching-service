namespace Coaching.Application.DTOs.Templates;

public class PlanLikeStatusDto
{
    public bool IsLiked { get; set; }
    public int LikeCount { get; set; }
}

public class PlanBookmarkStatusDto
{
    public bool IsBookmarked { get; set; }
}

public class BookmarkedPlanDto
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public int TotalDuration { get; set; }
    public int DrillCount { get; set; }
    public int LikeCount { get; set; }
    public DateTime BookmarkedAt { get; set; }
}

public class PlanCommentDto
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public Guid UserId { get; set; }
    public required string Content { get; set; }
    public Guid? ParentCommentId { get; set; }
    public UserProfileDto? User { get; set; }
    public List<PlanCommentDto> Replies { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class PlanCommentsResponseDto
{
    public List<PlanCommentDto> Items { get; set; } = new();
    public Guid? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

public class UserProfileDto
{
    public Guid Id { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? AvatarUrl { get; set; }
}
