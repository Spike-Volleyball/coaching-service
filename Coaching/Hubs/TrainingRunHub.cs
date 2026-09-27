using Coaching.Authorization;
using Microsoft.AspNetCore.SignalR;
using Shared.Security.Hubs;

namespace Coaching.Hubs;

/// <summary>An event's run room admits whoever may watch the run: its plan's creator, a participant or a host.</summary>
public class TrainingRunHub(IServiceProvider services) : SecureHub(services)
{
    public async Task JoinRun(Guid eventId)
    {
        await JoinResourceGroupAsync(GroupName(eventId), eventId, RunAccess.Read);
        await Clients.Caller.SendAsync("JoinedRun", eventId);
    }

    public Task LeaveRun(Guid eventId) => LeaveGroupAsync(GroupName(eventId));

    public static string GroupName(Guid eventId) => $"run_{eventId}";
}
