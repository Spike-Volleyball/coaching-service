using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Services;
using Microsoft.AspNetCore.SignalR;

namespace Coaching.Hubs;

public class SignalRRunBroadcaster : IRunBroadcaster
{
    private const string RunUpdated = "RunUpdated";

    private readonly IHubContext<TrainingRunHub> _hubContext;

    public SignalRRunBroadcaster(IHubContext<TrainingRunHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task BroadcastRunUpdatedAsync(Guid eventId, RunDto run) =>
        Task.WhenAll(
            _hubContext.Clients.Group(TrainingRunHub.ControllersGroup(eventId)).SendAsync(RunUpdated, run.WithCanControl(true)),
            _hubContext.Clients.Group(TrainingRunHub.ViewersGroup(eventId)).SendAsync(RunUpdated, run.WithCanControl(false)));
}
