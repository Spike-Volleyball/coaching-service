using Coaching.Application.DTOs.Evaluation;

namespace Coaching.Application.Interfaces.Services;

public interface IEvaluationSessionLifecycleService
{
    Task<EvaluationSessionDto> StartSessionAsync(Guid sessionId, Guid userId);
    Task<EvaluationSessionDto> PauseSessionAsync(Guid sessionId, Guid userId);
    Task<EvaluationSessionDto> ResumeSessionAsync(Guid sessionId, Guid userId);
    Task<EvaluationSessionDto> CompleteSessionAsync(Guid sessionId, Guid userId);
    Task<SessionProgressDto> GetSessionProgressAsync(Guid sessionId, Guid userId);
    Task UpdateSharingAsync(Guid sessionId, UpdateSharingDto dto, Guid userId);
    Task UpdatePlayerSharingAsync(Guid sessionId, Guid evaluationId, UpdatePlayerSharingDto dto, Guid userId);
}
