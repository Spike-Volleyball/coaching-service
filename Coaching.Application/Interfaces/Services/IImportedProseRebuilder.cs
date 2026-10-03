using Coaching.Application.DTOs.Drills;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// Gives the drills an import flattened, before the import kept a cell's structure, the structure
/// an import gives now — only where the prose is still exactly what the flat import made of its
/// lines, so no drill its author has reworked is touched.
/// </summary>
public interface IImportedProseRebuilder
{
    Task<ImportedProseRebuildDto> RebuildAsync(RebuildImportedProseDto request, CancellationToken ct = default);
}
