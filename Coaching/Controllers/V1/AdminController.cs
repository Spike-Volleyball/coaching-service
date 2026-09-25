using Asp.Versioning;
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
public class AdminController(IFactRepublisher republisher) : ControllerBase
{
    /// <summary>
    /// Publishes the praise snapshot of every piece of feedback a player can see again, for a
    /// consumer to backfill from. Answers with how many went out.
    /// </summary>
    [HttpPost("facts/republish")]
    public async Task<IActionResult> RepublishFacts(CancellationToken ct) =>
        Ok(await republisher.RepublishAsync(ct));
}
