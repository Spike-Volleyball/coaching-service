namespace Coaching.Application.DTOs.Drills;

/// <summary>
/// Where a reader met a drill: in the plan of an event, or on a piece of feedback. Either can open
/// a drill the reader may not open on its own, when it really holds the drill and the reader may
/// read it. Both absent is a plain read of the drill.
/// </summary>
public record DrillReadContext(Guid? EventId, Guid? FeedbackId);
