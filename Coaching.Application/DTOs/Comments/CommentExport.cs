namespace Coaching.Application.DTOs.Comments;

/// <summary>One stored comment of a drill or a plan, as handed over to social.</summary>
public record CommentExport(
    Guid Id,
    Guid ResourceId,
    Guid AuthorId,
    Guid? ParentCommentId,
    string Content,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    bool IsDeleted);
