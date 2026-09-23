using Coaching.Application.DTOs.Evaluation;

namespace Coaching.Application.Interfaces.Services;

public interface IEvaluationExerciseService
{
    Task<EvaluationExerciseDto> CreateAsync(CreateEvaluationExerciseDto request, Guid userId);
    /// <summary>
    /// One exercise, for any reader: the public library's open to all, a club's to its author and
    /// staff. A refusal raises what a missing exercise does — not-found when signed in, a request
    /// to sign in when not.
    /// </summary>
    Task<EvaluationExerciseDto> GetByIdForUserAsync(Guid id, Guid? userId);

    /// <summary>A club's exercises for its staff; anyone else gets the empty list a club with none gives.</summary>
    Task<IEnumerable<EvaluationExerciseDto>> GetByClubIdAsync(Guid clubId, Guid userId);
    Task<ExerciseListResponseDto> GetPublicExercisesAsync(int page = 1, int pageSize = 20);
    Task<ExerciseListResponseDto> GetUserExercisesAsync(Guid userId, int page = 1, int pageSize = 20);
    Task<EvaluationExerciseDto> UpdateAsync(Guid id, UpdateEvaluationExerciseDto request, Guid userId);
    Task DeleteAsync(Guid id, Guid userId);

    // Metrics
    Task<EvaluationExerciseDto> AddMetricAsync(Guid exerciseId, AddMetricDto request, Guid userId);
    Task<EvaluationExerciseDto> UpdateMetricAsync(Guid exerciseId, Guid metricId, UpdateEvaluationMetricDto request, Guid userId);
    Task<EvaluationExerciseDto> RemoveMetricAsync(Guid exerciseId, Guid metricId, Guid userId);
    Task<EvaluationExerciseDto> UpdateMetricSkillWeightsAsync(Guid exerciseId, Guid metricId, List<CreateMetricSkillWeightDto> weights, Guid userId);
}
