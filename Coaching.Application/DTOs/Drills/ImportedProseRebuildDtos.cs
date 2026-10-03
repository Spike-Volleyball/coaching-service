using Coaching.Domain.Enums;

namespace Coaching.Application.DTOs.Drills;

/// <summary>Which drills an admin rebuild looks at, and whether it writes.</summary>
/// <param name="UserId">The coach who imported them.</param>
/// <param name="Style">How the rebuilt instructions list plain lines, as an import's choice would.</param>
/// <param name="Apply">False, the default, reports what a rebuild would change and writes nothing.</param>
/// <param name="DrillIds">Only these of the coach's drills — the ones a dry run showed and were checked.</param>
public record RebuildImportedProseDto(
    Guid UserId,
    DirectionsStyle Style = DirectionsStyle.Auto,
    bool Apply = false,
    Guid[]? DrillIds = null);

public record ImportedProseRebuildDto(bool Applied, List<RebuiltDrillDto> Drills);

/// <summary>One drill a rebuild changes. A field left as it was is null.</summary>
public record RebuiltDrillDto(
    Guid DrillId,
    string Name,
    DateTime? CreatedAt,
    ProseChangeDto? Instructions,
    ProseChangeDto? CoachingPoints);

public record ProseChangeDto(string? Before, string? After);
