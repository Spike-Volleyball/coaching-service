using Coaching.Application.DTOs.Evaluation;

namespace Coaching.Application.Interfaces.Services;

/// <summary>
/// The names and pictures an evaluation session shows beside its ids, from the profiles this
/// service mirrors: each group's evaluator, and each player seated in a group.
/// </summary>
public interface IEvaluationPeople
{
    Task FillAsync(IReadOnlyCollection<EvaluationGroupDto> groups);

    /// <summary>The dashboard's groups: each one's evaluator.</summary>
    Task FillAsync(SessionProgressDto progress);
}
