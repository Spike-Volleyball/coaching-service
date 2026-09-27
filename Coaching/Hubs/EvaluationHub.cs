using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Services;
using Coaching.Authorization;
using Microsoft.AspNetCore.SignalR;
using Shared.Security.Hubs;

namespace Coaching.Hubs;

/// <summary>A session's room carries every score submitted in it, so it admits whoever may read the session.</summary>
public class EvaluationHub(IServiceProvider services, IEvaluationScoringService scoringService) : SecureHub(services)
{
    public static string SessionGroup(Guid sessionId) => $"session_{sessionId}";

    public async Task JoinSession(Guid sessionId)
    {
        await JoinResourceGroupAsync(SessionGroup(sessionId), sessionId, EvaluationSessionAccess.Read);
        await Clients.Caller.SendAsync("JoinedSession", sessionId);
    }

    public Task LeaveSession(Guid sessionId) => LeaveGroupAsync(SessionGroup(sessionId));

    public async Task SubmitScores(Guid sessionId, SubmitExerciseScoresDto dto)
    {
        var userId = GetUserId();
        var result = await scoringService.SubmitExerciseScoresAsync(sessionId, dto, userId);

        await Clients.Group(SessionGroup(sessionId))
            .SendAsync("ScoresSubmitted", result);
    }

    private Guid GetUserId()
    {
        var claim = Context.User?.Claims.FirstOrDefault(c => c.Type == "userId");
        return claim != null ? Guid.Parse(claim.Value) : throw new HubException("User not authenticated");
    }
}
