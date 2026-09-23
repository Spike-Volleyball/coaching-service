using Coaching.Application.DTOs.Evaluation;

namespace Coaching.Application.Interfaces.Services;

public interface IThresholdService
{
    Task<EvaluationThresholdDto> CreateAsync(Guid clubId, CreateThresholdDto request, Guid userId);
    /// <summary>A club's thresholds for its staff; anyone else gets the empty list a club with none gives.</summary>
    Task<IEnumerable<EvaluationThresholdDto>> GetByClubIdAsync(Guid clubId, Guid userId);
    Task<EvaluationThresholdDto> UpdateAsync(Guid id, UpdateThresholdDto request, Guid userId);
    Task DeleteAsync(Guid id, Guid userId);
    Task<ThresholdCheckResult> CheckPlayerAsync(Guid clubId, PlayerEvaluationDto evaluation);
}
