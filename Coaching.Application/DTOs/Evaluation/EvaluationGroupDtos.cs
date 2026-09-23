namespace Coaching.Application.DTOs.Evaluation;

public class EvaluationGroupDto
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? EvaluatorUserId { get; set; }
    public string? EvaluatorName { get; set; }
    public int Order { get; set; }
    public List<GroupPlayerDto> Players { get; set; } = new();
}

public class GroupPlayerDto
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string? PlayerName { get; set; }
    public string? AvatarUrl { get; set; }
}

public record CreateGroupDto
{
    public required string Name { get; set; }
    public Guid? EvaluatorUserId { get; set; }
    public List<Guid>? PlayerIds { get; set; }
}

public record UpdateGroupDto
{
    public string? Name { get; set; }

    /// <summary>The evaluator to put on the group; null leaves the current one.</summary>
    public Guid? EvaluatorUserId { get; set; }

    /// <summary>
    /// Takes the evaluator off the group, which only a draft allows: a session starts only when
    /// every group has one. A null <see cref="EvaluatorUserId"/> already means "leave it", so
    /// clearing needs its own word.
    /// </summary>
    public bool ClearEvaluator { get; set; }
}

public record AutoSplitGroupsDto
{
    public int NumberOfGroups { get; set; }
}

public record AssignPlayerToGroupDto
{
    public Guid PlayerId { get; set; }
}

public record MovePlayerDto
{
    public Guid PlayerId { get; set; }
    public Guid TargetGroupId { get; set; }
}
