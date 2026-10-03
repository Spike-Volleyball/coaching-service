using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.RichText;
using Microsoft.EntityFrameworkCore;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class ImportedProseRebuilder(IDrillRepository drillRepository) : IImportedProseRebuilder
{
    public async Task<ImportedProseRebuildDto> RebuildAsync(RebuildImportedProseDto request, CancellationToken ct = default)
    {
        if (request.UserId == Guid.Empty)
            throw new BadRequestException("Name the coach whose drills to rebuild", ErrorCodeEnum.ValidationError);

        // A dial is an edit whatever the prose still says, so a drill with one is never a candidate.
        var query = drillRepository.Query().Where(d => d.CreatedByUserId == request.UserId && !d.Dials.Any());
        if (request.DrillIds is { Length: > 0 } ids)
            query = query.Where(d => ids.Contains(d.Id));

        var drills = await query.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(ct);
        var rebuilt = new List<RebuiltDrillDto>();

        foreach (var drill in drills)
        {
            var instructions = Rebuild(drill.InstructionsHtml, drill.Instructions, ordered: true,
                lines => ImportedProse.Instructions(lines, request.Style));
            var coachingPoints = Rebuild(drill.CoachingPointsHtml, drill.CoachingPoints, ordered: false,
                ImportedProse.CoachingPoints);

            if (instructions is null && coachingPoints is null) continue;

            rebuilt.Add(new RebuiltDrillDto(
                drill.Id,
                drill.Name,
                drill.CreatedAt,
                instructions is { } i ? new ProseChangeDto(drill.InstructionsHtml, i.Html) : null,
                coachingPoints is { } c ? new ProseChangeDto(drill.CoachingPointsHtml, c.Html) : null));

            if (!request.Apply) continue;

            if (instructions is { } newInstructions)
            {
                drill.InstructionsHtml = newInstructions.Html;
                drill.Instructions = newInstructions.Lines;
            }

            if (coachingPoints is { } newCoachingPoints)
            {
                drill.CoachingPointsHtml = newCoachingPoints.Html;
                drill.CoachingPoints = newCoachingPoints.Lines;
            }
        }

        if (request.Apply && rebuilt.Count > 0)
            await drillRepository.SaveChangesAsync();

        return new ImportedProseRebuildDto(request.Apply, rebuilt);
    }

    /// <summary>
    /// The field as the import rules build it now, or null when it is not the flat import's any
    /// more — its HTML is no longer exactly the one list the import made of its stored lines, so
    /// someone wrote it since — or when the rules would give it back unchanged.
    /// </summary>
    private static (string? Html, string[] Lines)? Rebuild(
        string? html, string[] lines, bool ordered, Func<string[], (string? Html, string[] Lines)> build)
    {
        if (html != DrillRichText.FromLines(lines, ordered)) return null;

        var rebuilt = build(lines);

        // The flat import never went through the sanitizer, which spells some characters
        // differently; compared unsanitized, an apostrophe alone would read as a change.
        return rebuilt.Html == DrillRichText.Sanitize(html) ? null : rebuilt;
    }
}
