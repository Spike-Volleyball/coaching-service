namespace Coaching.Application.DTOs.Facts;

/// <summary>What a republish of history sent.</summary>
public class FactsRepublishedDto
{
    /// <summary>Shared feedback whose praise snapshot was published again.</summary>
    public int Praise { get; set; }

    /// <summary>Tactics boards somebody made and drew on, whose snapshot was published again.</summary>
    public int Boards { get; set; }
}
