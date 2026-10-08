using Coaching.Domain.Enums;

namespace Coaching.Application.DTOs.Drills;

// =============================================================================
// LIKES
// =============================================================================

public class DrillLikeStatusDto
{
    public bool IsLiked { get; set; }
    public int LikeCount { get; set; }
}

// =============================================================================
// BOOKMARKS
// =============================================================================

public class DrillBookmarkStatusDto
{
    public bool IsBookmarked { get; set; }
}

public class BookmarkedDrillDto
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DrillCategory Category { get; set; }
    public DrillIntensity Intensity { get; set; }
    public int LikeCount { get; set; }
    public DateTime BookmarkedAt { get; set; }
}

// =============================================================================
// COMMENTS
// =============================================================================

public class DrillCommentAuthorDto
{
    public Guid Id { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? AvatarUrl { get; set; }
}

public class DrillCommentDto
{
    public Guid Id { get; set; }
    public Guid DrillId { get; set; }
    public Guid UserId { get; set; }
    public required string Content { get; set; }
    public Guid? ParentCommentId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DrillCommentAuthorDto? Author { get; set; }
    public ICollection<DrillCommentDto> Replies { get; set; } = new List<DrillCommentDto>();
}

public class DrillCommentsResponseDto
{
    public ICollection<DrillCommentDto> Items { get; set; } = new List<DrillCommentDto>();
    public Guid? NextCursor { get; set; }
    public bool HasMore { get; set; }
}
