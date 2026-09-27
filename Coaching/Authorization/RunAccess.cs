namespace Coaching.Authorization;

/// <summary>What a caller may do with an event's training run, as its authority answers.</summary>
public enum RunAccess
{
    /// <summary>Watch the run, including live in its room.</summary>
    Read,
}
