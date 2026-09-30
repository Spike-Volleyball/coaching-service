using System.ComponentModel.DataAnnotations;
using Coaching.Domain.Enums;

namespace Coaching.Application.DTOs.Templates;

public class RunDto
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public Guid EventId { get; set; }
    public Guid StartedByUserId { get; set; }
    public RunStatus Status { get; set; }

    public Guid? CurrentItemId { get; set; }

    // Virtual start of the current item's timer; set while Running.
    public DateTime? CurrentItemStartedAt { get; set; }
    public int CurrentItemPausedElapsedSeconds { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Moves on by itself when a step's time is up, except from the last step.
    public bool AutoAdvance { get; set; }

    // Server "now" so each client computes a clock offset.
    public DateTime ServerTime { get; set; }

    // Whether the reader may control the run: the plan's creator or an event admin.
    public bool CanControl { get; set; }

    public List<RunItemDto> Items { get; set; } = new();

    /// <summary>The same run as told to someone who may, or may not, control it.</summary>
    public RunDto WithCanControl(bool canControl)
    {
        var copy = (RunDto)MemberwiseClone();
        copy.CanControl = canControl;
        return copy;
    }
}

public class RunItemDto
{
    public Guid Id { get; set; }
    public Guid PlanItemId { get; set; }

    // What the row is, snapshotted with the run: a client reading a run never has to fetch the
    // plan to find out whether it is looking at a drill, a break or a block of stations.
    public ItemKind Kind { get; set; }
    public string? Title { get; set; }

    public Guid? DrillId { get; set; }
    public int Order { get; set; }
    public int PlannedDurationSeconds { get; set; }
    public int ActualElapsedSeconds { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>The groups running side by side. Only a Stations row has any.</summary>
    public List<RunStationDto> Stations { get; set; } = new();
}

public class RunStationDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public List<RunStationItemDto> Items { get; set; } = new();
}

public class RunStationItemDto
{
    public Guid Id { get; set; }
    public ItemKind Kind { get; set; }
    public Guid? DrillId { get; set; }
    public string? Title { get; set; }
    public int Order { get; set; }
    public int DurationSeconds { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Optional body for POST .../run/pause, /resume, /complete and /reopen. <paramref name="OccurredAt"/>
/// is when the tap was made, for one queued offline; absent means now.
/// </summary>
public record RunTapDto(DateTimeOffset? OccurredAt = null);

/// <summary>
/// Body for POST .../run/advance. <paramref name="FromItemId"/> is the plan item id the caller saw
/// current, which guards against a double or stale tap.
/// </summary>
public record AdvanceRunDto(Guid FromItemId, DateTimeOffset? OccurredAt = null);

/// <summary>
/// Body for POST .../run/goto: from the plan item id the caller saw current (guarded as for
/// advance) to any other of the run's plan item ids.
/// </summary>
public record GoToRunDto(Guid FromItemId, Guid ToItemId, DateTimeOffset? OccurredAt = null);

/// <summary>
/// Optional body for POST .../run/start. A run that is Running or Paused is only started over
/// when <paramref name="Restart"/> says so; a finished one starts over either way.
/// <paramref name="AutoAdvance"/> sets whether the run moves on by itself; absent, a new run
/// starts without it and a run started over keeps what it had.
/// </summary>
public record StartRunDto(bool Restart = false, bool? AutoAdvance = null);

/// <summary>
/// Body for POST .../run/auto-advance: whether the run moves on by itself from now on. Required,
/// so a body that says nothing is refused rather than read as off.
/// </summary>
public record RunAutoAdvanceDto([Required] bool? Enabled);

/// <summary>What the caller may do with an event's run, answerable before any run exists.</summary>
public record RunPermissionsDto(bool CanControl);
