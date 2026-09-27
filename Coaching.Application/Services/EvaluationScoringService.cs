using AutoMapper;
using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Enums;
using Shared.Exceptions;
using Shared.Services.Analytics;

namespace Coaching.Application.Services;

public class EvaluationScoringService(
    IEvaluationSessionRepository sessionRepository,
    IPlayerExerciseScoreRepository exerciseScoreRepository,
    IEvaluationGroupRepository groupRepository,
    IEvaluationPlanRepository planRepository,
    IPlayerEvaluationRepository evaluationRepository,
    IRepository<PlayerMetricScore> metricScoreRepository,
    IScoreCalculationService scoreCalculationService,
    IAnalyticsCapture analytics,
    IEvaluationAccess access,
    IMapper mapper) : IEvaluationScoringService
{
    public async Task<PlayerExerciseScoreDto> SubmitExerciseScoresAsync(Guid sessionId, SubmitExerciseScoresDto dto, Guid userId)
    {
        var session = await sessionRepository.GetByIdWithParticipantsAsync(sessionId);
        if (session == null)
            throw new EntityNotFoundException("Evaluation session not found");

        if (session.Status != EvaluationSessionStatus.Running)
            throw new BadRequestException("Scores can only be submitted for a running session", ErrorCodeEnum.ValidationError);

        // The session's coach scores anyone. An evaluator scores the players of the groups they
        // evaluate, which the session load carries, and nobody else's.
        if (session.CoachUserId != userId)
        {
            var evaluated = session.Groups.Where(g => g.EvaluatorUserId == userId).ToList();
            if (evaluated.Count == 0)
                throw new ForbiddenException("Only the session coach or a group evaluator can submit scores");

            if (!evaluated.Any(g => g.Players.Any(p => p.PlayerId == dto.PlayerId)))
                throw new ForbiddenException("An evaluator scores only the players in their own groups");
        }

        // Verify the player is a participant and get their evaluation
        var participant = session.Participants.FirstOrDefault(p => p.PlayerId == dto.PlayerId);
        if (participant == null)
            throw new BadRequestException("Player is not a participant in this session", ErrorCodeEnum.ValidationError);

        // Get the player's evaluation (created at session start)
        var evaluation = await evaluationRepository.GetByParticipantIdAsync(participant.Id);
        if (evaluation == null)
            throw new BadRequestException("Player evaluation not found. Ensure the session has been started.", ErrorCodeEnum.ValidationError);

        // Validate the exercise belongs to the session's plan
        if (!session.EvaluationPlanId.HasValue)
            throw new BadRequestException("Session has no evaluation plan", ErrorCodeEnum.ValidationError);

        var plan = await planRepository.GetByIdWithItemsAsync(session.EvaluationPlanId.Value);
        if (plan == null)
            throw new BadRequestException("Evaluation plan not found", ErrorCodeEnum.ValidationError);

        var planExercise = plan.Items.FirstOrDefault(i => i.ExerciseId == dto.ExerciseId);
        if (planExercise == null)
            throw new BadRequestException("Exercise is not part of the evaluation plan", ErrorCodeEnum.ValidationError);

        // Find or create the PlayerExerciseScore record
        var exerciseScore = await exerciseScoreRepository.GetBySessionPlayerExerciseAsync(
            sessionId, dto.PlayerId, dto.ExerciseId);

        if (exerciseScore == null)
        {
            exerciseScore = new PlayerExerciseScore
            {
                SessionId = sessionId,
                PlayerId = dto.PlayerId,
                ExerciseId = dto.ExerciseId,
                Status = EvaluationScoreStatus.Pending
            };
            exerciseScoreRepository.Add(exerciseScore);
            await exerciseScoreRepository.SaveChangesAsync();
        }

        // Upsert metric scores
        var exerciseMetrics = planExercise.Exercise.Metrics.ToList();

        foreach (var scoreDto in dto.Scores)
        {
            var metric = exerciseMetrics.FirstOrDefault(m => m.Id == scoreDto.MetricId);
            if (metric == null) continue;

            var normalizedScore = scoreCalculationService.NormalizeMetricValue(metric, scoreDto.Value);

            var existingMetricScore = exerciseScore.MetricScores
                .FirstOrDefault(m => m.MetricId == scoreDto.MetricId);

            if (existingMetricScore != null)
            {
                existingMetricScore.RawValue = scoreDto.Value;
                existingMetricScore.NormalizedScore = normalizedScore;
                existingMetricScore.Notes = scoreDto.Notes;
                metricScoreRepository.Update(existingMetricScore);
            }
            else
            {
                var newMetricScore = new PlayerMetricScore
                {
                    EvaluationId = evaluation.Id,
                    MetricId = scoreDto.MetricId,
                    RawValue = scoreDto.Value,
                    NormalizedScore = normalizedScore,
                    PlayerExerciseScoreId = exerciseScore.Id,
                    Notes = scoreDto.Notes
                };
                metricScoreRepository.Add(newMetricScore);
            }
        }

        // Mark as scored
        exerciseScore.Status = EvaluationScoreStatus.Scored;
        exerciseScore.ScoredAt = DateTime.UtcNow;
        exerciseScore.EvaluatorUserId = userId;

        exerciseScoreRepository.Update(exerciseScore);
        await exerciseScoreRepository.SaveChangesAsync();

        analytics.CapturePlayerEvaluationScored(sessionId, evaluation.Id, userId, dto.Scores.Count);

        // Reload and return
        var updatedScore = await exerciseScoreRepository.GetBySessionPlayerExerciseAsync(
            sessionId, dto.PlayerId, dto.ExerciseId);

        return mapper.Map<PlayerExerciseScoreDto>(updatedScore);
    }

    public async Task<IEnumerable<PlayerExerciseScoreDto>> GetSessionScoresAsync(Guid sessionId, Guid userId)
    {
        await access.EnsureMayReadSessionAsync(await sessionRepository.GetByIdAsync(sessionId), userId);

        var scores = await exerciseScoreRepository.GetBySessionIdAsync(sessionId);
        return mapper.Map<IEnumerable<PlayerExerciseScoreDto>>(scores);
    }

    public async Task<IEnumerable<PlayerExerciseScoreDto>> GetGroupExerciseScoresAsync(
        Guid sessionId, Guid groupId, Guid exerciseId, Guid userId)
    {
        await access.EnsureMayReadSessionAsync(await sessionRepository.GetByIdAsync(sessionId), userId);

        var group = await groupRepository.GetByIdWithPlayersAsync(groupId);
        if (group == null || group.SessionId != sessionId)
            throw new EntityNotFoundException("Group not found in this session");

        // Get the player IDs from this group
        var groupPlayerIds = group.Players.Select(p => p.PlayerId).ToHashSet();

        // Get all scores for this exercise in this session
        var exerciseScores = await exerciseScoreRepository.GetBySessionAndExerciseAsync(sessionId, exerciseId);

        // Filter to only group members
        var groupScores = exerciseScores.Where(s => groupPlayerIds.Contains(s.PlayerId));

        return mapper.Map<IEnumerable<PlayerExerciseScoreDto>>(groupScores);
    }
}
