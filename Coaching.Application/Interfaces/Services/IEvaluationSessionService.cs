using Coaching.Application.DTOs.Evaluation;

namespace Coaching.Application.Interfaces.Services;

public interface IEvaluationSessionService
{
    Task<EvaluationSessionDto> CreateAsync(CreateEvaluationSessionDto request, Guid coachUserId);
    /// <summary>
    /// One session, for a signed-in reader; one they may not read raises the same not-found as one
    /// that is not there.
    /// </summary>
    Task<EvaluationSessionDto> GetByIdForUserAsync(Guid id, Guid userId);

    /// <summary>A club's sessions for its staff; anyone else gets the empty list a club with none gives.</summary>
    Task<IEnumerable<EvaluationSessionDto>> GetByClubIdAsync(Guid clubId, Guid userId, int page = 1, int pageSize = 20);
    Task<IEnumerable<EvaluationSessionDto>> GetMySessionsAsync(Guid coachUserId, int page = 1, int pageSize = 20);
    Task<EvaluationSessionDto> UpdateAsync(Guid id, UpdateEvaluationSessionDto request, Guid userId);
    Task DeleteAsync(Guid id, Guid userId);

    // Participants
    Task<EvaluationSessionDto> AddParticipantsAsync(Guid sessionId, AddParticipantsDto request, Guid userId);
    Task<EvaluationSessionDto> RemoveParticipantAsync(Guid sessionId, Guid participantId, Guid userId);
}
