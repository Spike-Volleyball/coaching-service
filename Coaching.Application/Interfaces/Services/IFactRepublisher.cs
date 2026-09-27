using Coaching.Application.DTOs.Facts;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// Publishes every fact the service holds again — each piece of feedback a player can see, as its
/// praise snapshot — through the path live changes take, so a consumer that starts late can backfill.
/// Each copy carries a new snapshot time and the same content, so a consumer that already holds it
/// changes nothing.
/// </summary>
public interface IFactRepublisher
{
    Task<FactsRepublishedDto> RepublishAsync(CancellationToken ct = default);
}
