using Coaching.Authorization;
using Microsoft.AspNetCore.SignalR;
using Shared.DataAccess.Providers.Interfaces;
using Shared.Microservices.Authorization;
using Shared.Security.Access;
using Shared.Security.Hubs;

namespace Coaching.Hubs;

/// <summary>
/// An event's run has two rooms: its controllers' — the plan's creator and the event's admins —
/// and its viewers', everyone else who may watch it. Each gets its own copy of every update, so
/// only a controller's is told it may control the run.
/// </summary>
public class TrainingRunHub(
    IServiceProvider services,
    IResourceAuthority<RunAccess> runs,
    IJwtPayloadProvider jwtPayloadProvider) : SecureHub(services)
{
    /// <summary>
    /// Which room is decided here, once: a coach made a host mid-session gets controls on their
    /// next join, as every client makes on reconnecting.
    /// </summary>
    public async Task JoinRun(Guid eventId)
    {
        var userId = CallerIdentity.UserId(Context.User ?? new(), jwtPayloadProvider);
        var controls = await runs.CanAsync(userId, eventId, RunAccess.Control, Context.ConnectionAborted);

        // SecureHub joins a room only through the room's authority, so the room's access is asked again.
        await JoinResourceGroupAsync(
            controls ? ControllersGroup(eventId) : ViewersGroup(eventId),
            eventId,
            controls ? RunAccess.Control : RunAccess.Read);
        await Clients.Caller.SendAsync("JoinedRun", eventId);
    }

    public Task LeaveRun(Guid eventId) =>
        Task.WhenAll(LeaveGroupAsync(ControllersGroup(eventId)), LeaveGroupAsync(ViewersGroup(eventId)));

    public static string ControllersGroup(Guid eventId) => $"run:{eventId}:controllers";

    public static string ViewersGroup(Guid eventId) => $"run:{eventId}:viewers";
}
