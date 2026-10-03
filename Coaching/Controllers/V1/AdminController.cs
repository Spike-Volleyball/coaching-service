using Asp.Versioning;
using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Shared.Security.Authorization;

namespace Coaching.Controllers.V1;

/// <summary>
/// Endpoints the admin console calls. Not for user-facing clients.
/// </summary>
/// <remarks>
/// The gateway proxies every coaching-service path under <c>/coaching/</c>, so these are reachable
/// from the internet whatever the routing says. Being unrouted is not a control; the key check is.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("v{version:apiVersion}/[controller]")]
[AdminConsoleOnly]
public class AdminController(IFactRepublisher republisher, IImportedProseRebuilder proseRebuilder) : ControllerBase
{
    /// <summary>
    /// Publishes the praise snapshot of every piece of feedback a player can see again, and the
    /// snapshot of every tactics board somebody drew, for a consumer to backfill from. Answers with
    /// how many of each went out.
    /// </summary>
    [HttpPost("facts/republish")]
    public async Task<IActionResult> RepublishFacts(CancellationToken ct) =>
        Ok(await republisher.RepublishAsync(ct));

    /// <summary>
    /// Gives one coach's drills that an import flattened the structure an import gives now — only
    /// where a field is still exactly what the flat import stored, and never on a drill with
    /// dials. Answers with each drill it changes, before and after; writes only when told to apply.
    /// </summary>
    [HttpPost("drills/imported-prose/rebuild")]
    public async Task<IActionResult> RebuildImportedProse([FromBody] RebuildImportedProseDto request, CancellationToken ct) =>
        Ok(await proseRebuilder.RebuildAsync(request, ct));
}
