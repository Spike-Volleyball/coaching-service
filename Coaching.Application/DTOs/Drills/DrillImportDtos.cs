using Coaching.Domain.Enums;

namespace Coaching.Application.DTOs.Drills;

/// <summary>
/// One spreadsheet row, already parsed and coerced by the client. The row number is the
/// caller's own line reference — it is echoed back untouched so a failure can be pointed
/// at the row the coach is looking at.
/// </summary>
public record ImportDrillRowDto(
    int RowNumber,
    string Name,
    string? Description,
    DrillCategory Category,
    DrillIntensity Intensity,
    DrillSkill[] Skills,
    int? Duration,
    int? MinPlayers,
    int? MaxPlayers,
    string[] Instructions,
    string[] CoachingPoints,
    DrillEquipmentInput[] Equipment,
    string? VideoUrl
);

/// <summary>
/// Destination and payload for one import. Visibility and club are chosen once for the whole
/// batch rather than per row — a spreadsheet is imported into one library at a time.
/// </summary>
/// <param name="DirectionsStyle">
/// How the instructions' plain lines are listed. Lines written with a marker of their own keep
/// it whatever this says. Absent, as from clients that predate it, is Auto.
/// </param>
public record ImportDrillsDto(
    Guid? ClubId,
    DrillVisibility Visibility,
    List<ImportDrillRowDto> Drills,
    DirectionsStyle DirectionsStyle = DirectionsStyle.Auto
);

public record ImportDrillResultDto(
    int RowNumber,
    string Name,
    Guid? DrillId,
    string? Error
);

public record ImportDrillsResultDto(
    int Imported,
    int Failed,
    List<ImportDrillResultDto> Results
);
