using Coaching.Application.DTOs.Evaluation;

namespace Coaching.Application.Interfaces.Services;

public interface IEvaluationScoringService
{
    Task<PlayerExerciseScoreDto> SubmitExerciseScoresAsync(Guid sessionId, SubmitExerciseScoresDto dto, Guid userId);
    Task<IEnumerable<PlayerExerciseScoreDto>> GetSessionScoresAsync(Guid sessionId, Guid userId);
    Task<IEnumerable<PlayerExerciseScoreDto>> GetGroupExerciseScoresAsync(Guid sessionId, Guid groupId, Guid exerciseId, Guid userId);
}
