namespace Coaching.BackgroundServices;

public sealed class RunAutoAdvanceOptions
{
    public const string SectionName = "RunAutoAdvance";

    /// <summary>
    /// How often the sweep looks for runs whose step's time is up. A phone that is looking flips at
    /// 0:00 by itself; this is how late one that is not — a locked one — hears the run moved on.
    /// </summary>
    public double IntervalSeconds { get; set; } = 2;

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);
}
