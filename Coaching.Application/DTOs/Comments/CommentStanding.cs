namespace Coaching.Application.DTOs.Comments;

/// <summary>
/// What one user may do with the comments of one drill or plan. Not existing and not being allowed
/// are different answers, and neither is an error.
/// </summary>
public readonly record struct CommentStanding(bool Exists, bool CanRead, bool CanModerate)
{
    public static readonly CommentStanding Missing = new(false, false, false);
}
