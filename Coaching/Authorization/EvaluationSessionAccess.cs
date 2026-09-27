namespace Coaching.Authorization;

/// <summary>What a caller may do with an evaluation session, as its authority answers.</summary>
public enum EvaluationSessionAccess
{
    /// <summary>See the session and its scores, including live in its room.</summary>
    Read,
}
