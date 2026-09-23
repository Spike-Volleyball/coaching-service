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

public class EvaluationSessionLifecycleService(
    IEvaluationSessionRepository sessionRepository,
    IEvaluationGroupRepository groupRepository,
    IEvaluationParticipantRepository participantRepository,
    IPlayerExerciseScoreRepository exerciseScoreRepository,
    IPlayerEvaluationRepository evaluationRepository,
    IEvaluationPlanRepository planRepository,
    IRepository<PlayerMetricScore> metricScoreRepository,
    IRepository<PlayerSkillScore> skillScoreRepository,
    IClubsGrpcClient clubsGrpcClient,
    IScoreCalculationService scoreCalculationService,
    IAnalyticsCapture analytics,
    IEvaluationAccess access,
    IMapper mapper) : IEvaluationSessionLifecycleService
{
    public async Task<EvaluationSessionDto> StartSessionAsync(Guid sessionId, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);

        if (session.Status != EvaluationSessionStatus.Draft)
            throw new BadRequestException("Session can only be started from Draft status", ErrorCodeEnum.ValidationError);

        // Validate plan is selected
        if (!session.EvaluationPlanId.HasValue)
            throw new BadRequestException("An evaluation plan must be selected before starting the session", ErrorCodeEnum.ValidationError);

        var plan = await planRepository.GetByIdWithItemsAsync(session.EvaluationPlanId.Value);
        if (plan == null)
            throw new BadRequestException("The linked evaluation plan was not found", ErrorCodeEnum.ValidationError);

        if (plan.Items.Count == 0)
            throw new BadRequestException("The evaluation plan has no exercises", ErrorCodeEnum.ValidationError);

        // Validate participants exist
        var participants = (await participantRepository.GetBySessionIdAsync(sessionId)).ToList();
        if (participants.Count == 0)
            throw new BadRequestException("At least one participant is required to start the session", ErrorCodeEnum.ValidationError);

        // Validate all participants are assigned to groups
        var groups = (await groupRepository.GetBySessionIdAsync(sessionId)).ToList();
        if (groups.Count == 0)
            throw new BadRequestException("At least one group must be created before starting the session", ErrorCodeEnum.ValidationError);

        var allGroupPlayerIds = groups
            .SelectMany(g => g.Players.Select(p => p.PlayerId))
            .ToHashSet();

        var unassignedParticipants = participants
            .Where(p => !allGroupPlayerIds.Contains(p.PlayerId))
            .ToList();

        if (unassignedParticipants.Count > 0)
            throw new BadRequestException(
                $"{unassignedParticipants.Count} participant(s) are not assigned to any group",
                ErrorCodeEnum.ValidationError);

        // Validate all groups have evaluators
        var groupsWithoutEvaluator = groups.Where(g => !g.EvaluatorUserId.HasValue).ToList();
        if (groupsWithoutEvaluator.Count > 0)
            throw new BadRequestException(
                $"{groupsWithoutEvaluator.Count} group(s) do not have an evaluator assigned",
                ErrorCodeEnum.ValidationError);

        // Create PlayerEvaluation records for each participant (needed for metric score FK)
        foreach (var participant in participants)
        {
            var existingEvaluation = await evaluationRepository.GetByParticipantIdAsync(participant.Id);
            if (existingEvaluation == null)
            {
                var evaluation = new PlayerEvaluation
                {
                    EvaluationParticipantId = participant.Id,
                    PlayerId = participant.PlayerId,
                    EvaluatedByUserId = session.CoachUserId,
                    Outcome = EvaluationOutcome.Pending,
                    SessionId = sessionId
                };
                evaluationRepository.Add(evaluation);
            }
        }
        await evaluationRepository.SaveChangesAsync();

        // Create PlayerExerciseScore records for every participant x exercise combination
        var exercises = plan.Items.Select(i => i.Exercise).ToList();

        foreach (var participant in participants)
        {
            foreach (var exercise in exercises)
            {
                var existingScore = await exerciseScoreRepository.GetBySessionPlayerExerciseAsync(
                    sessionId, participant.PlayerId, exercise.Id);

                if (existingScore == null)
                {
                    var score = new PlayerExerciseScore
                    {
                        SessionId = sessionId,
                        PlayerId = participant.PlayerId,
                        ExerciseId = exercise.Id,
                        Status = EvaluationScoreStatus.Pending
                    };
                    exerciseScoreRepository.Add(score);
                }
            }
        }

        session.Status = EvaluationSessionStatus.Running;
        session.StartedAt = DateTime.UtcNow;

        sessionRepository.Update(session);
        await sessionRepository.SaveChangesAsync();

        analytics.Capture(userId, AnalyticsEventNames.EvaluationSessionStarted, new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["participant_count"] = participants.Count,
            ["exercise_count"] = plan.Items.Count
        });

        return mapper.Map<EvaluationSessionDto>(
            await sessionRepository.GetByIdWithParticipantsAsync(sessionId));
    }

    public async Task<EvaluationSessionDto> PauseSessionAsync(Guid sessionId, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);

        if (session.Status != EvaluationSessionStatus.Running)
            throw new BadRequestException("Session can only be paused when Running", ErrorCodeEnum.ValidationError);

        session.Status = EvaluationSessionStatus.Paused;
        session.PausedAt = DateTime.UtcNow;

        sessionRepository.Update(session);
        await sessionRepository.SaveChangesAsync();

        return mapper.Map<EvaluationSessionDto>(
            await sessionRepository.GetByIdWithParticipantsAsync(sessionId));
    }

    public async Task<EvaluationSessionDto> ResumeSessionAsync(Guid sessionId, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);

        if (session.Status != EvaluationSessionStatus.Paused)
            throw new BadRequestException("Session can only be resumed when Paused", ErrorCodeEnum.ValidationError);

        session.Status = EvaluationSessionStatus.Running;
        session.PausedAt = null;

        sessionRepository.Update(session);
        await sessionRepository.SaveChangesAsync();

        return mapper.Map<EvaluationSessionDto>(
            await sessionRepository.GetByIdWithParticipantsAsync(sessionId));
    }

    public async Task<EvaluationSessionDto> CompleteSessionAsync(Guid sessionId, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);

        if (session.Status != EvaluationSessionStatus.Running && session.Status != EvaluationSessionStatus.Paused)
            throw new BadRequestException("Session can only be completed when Running or Paused", ErrorCodeEnum.ValidationError);

        if (!session.EvaluationPlanId.HasValue)
            throw new BadRequestException("Session has no evaluation plan", ErrorCodeEnum.ValidationError);

        var plan = await planRepository.GetByIdWithItemsAsync(session.EvaluationPlanId.Value);
        if (plan == null)
            throw new BadRequestException("Evaluation plan not found", ErrorCodeEnum.ValidationError);

        // Calculate final results for each participant
        var (evaluatedCount, scoredCount) = await CalculateFinalResults(session, plan);

        session.Status = EvaluationSessionStatus.Completed;
        session.CompletedAt = DateTime.UtcNow;

        sessionRepository.Update(session);
        await sessionRepository.SaveChangesAsync();

        analytics.Capture(userId, AnalyticsEventNames.EvaluationSessionCompleted, new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["evaluated_count"] = evaluatedCount,
            ["scored_count"] = scoredCount,
            ["duration_seconds"] = (int)((session.CompletedAt - session.StartedAt)?.TotalSeconds ?? 0)
        });

        return mapper.Map<EvaluationSessionDto>(
            await sessionRepository.GetByIdWithParticipantsAsync(sessionId));
    }

    public async Task<SessionProgressDto> GetSessionProgressAsync(Guid sessionId, Guid userId)
    {
        var session = await access.EnsureMayReadSessionAsync(
            await sessionRepository.GetByIdWithParticipantsAsync(sessionId), userId);

        var groups = (await groupRepository.GetBySessionIdAsync(sessionId)).ToList();
        var allScores = (await exerciseScoreRepository.GetBySessionIdAsync(sessionId)).ToList();

        // Load plan once (used for exercise count and progress tracking)
        EvaluationPlan? plan = null;
        var totalExercises = 0;
        if (session.EvaluationPlanId.HasValue)
        {
            plan = await planRepository.GetByIdWithItemsAsync(session.EvaluationPlanId.Value);
            totalExercises = plan?.Items.Count ?? 0;
        }

        var totalPlayers = session.Participants.Count;
        var totalPossible = totalPlayers * totalExercises;
        var totalScored = allScores.Count(s => s.Status == EvaluationScoreStatus.Scored);

        var groupProgressList = new List<GroupProgressDto>();

        foreach (var group in groups)
        {
            var groupPlayerIds = group.Players.Select(p => p.PlayerId).ToHashSet();
            var groupScores = allScores.Where(s => groupPlayerIds.Contains(s.PlayerId)).ToList();

            var groupTotalPlayers = groupPlayerIds.Count;

            // Determine exercises completed (all players in group scored for that exercise)
            var exercisesCompleted = 0;
            if (totalExercises > 0)
            {
                var exerciseIds = groupScores.Select(s => s.ExerciseId).Distinct();
                foreach (var exerciseId in exerciseIds)
                {
                    var scoresForExercise = groupScores
                        .Where(s => s.ExerciseId == exerciseId && s.Status == EvaluationScoreStatus.Scored)
                        .ToList();
                    if (scoresForExercise.Count >= groupTotalPlayers)
                        exercisesCompleted++;
                }
            }

            // Find current exercise (first not fully scored)
            string? currentExerciseName = null;
            if (plan != null)
            {
                foreach (var item in plan.Items.OrderBy(i => i.Order))
                {
                    var scoredForExercise = groupScores
                        .Count(s => s.ExerciseId == item.ExerciseId && s.Status == EvaluationScoreStatus.Scored);
                    if (scoredForExercise < groupTotalPlayers)
                    {
                        currentExerciseName = item.Exercise.Name;
                        break;
                    }
                }
            }

            groupProgressList.Add(new GroupProgressDto
            {
                GroupId = group.Id,
                GroupName = group.Name,
                EvaluatorUserId = group.EvaluatorUserId,
                CurrentExerciseName = currentExerciseName,
                PlayersScored = groupScores
                    .Where(s => s.Status == EvaluationScoreStatus.Scored)
                    .Select(s => s.PlayerId)
                    .Distinct()
                    .Count(),
                TotalPlayers = groupTotalPlayers,
                ExercisesCompleted = exercisesCompleted,
                TotalExercises = totalExercises
            });
        }

        return new SessionProgressDto
        {
            SessionId = sessionId,
            Status = session.Status,
            TotalPlayers = totalPlayers,
            TotalExercises = totalExercises,
            TotalScored = totalScored,
            TotalPossible = totalPossible,
            OverallProgress = totalPossible > 0 ? Math.Round((decimal)totalScored / totalPossible * 100, 1) : 0,
            Groups = groupProgressList
        };
    }

    public async Task UpdateSharingAsync(Guid sessionId, UpdateSharingDto dto, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);

        if (dto.ShareFeedback.HasValue) session.ShareFeedback = dto.ShareFeedback.Value;
        if (dto.ShareMetrics.HasValue) session.ShareMetrics = dto.ShareMetrics.Value;

        sessionRepository.Update(session);
        await sessionRepository.SaveChangesAsync();
    }

    public async Task UpdatePlayerSharingAsync(Guid sessionId, Guid evaluationId, UpdatePlayerSharingDto dto, Guid userId)
    {
        await GetSessionAndValidateOwnership(sessionId, userId);

        var evaluation = await evaluationRepository.GetByIdWithScoresAsync(evaluationId);
        if (evaluation == null)
            throw new EntityNotFoundException("Player evaluation not found");

        // Verify the evaluation belongs to this session
        var participant = await participantRepository.GetByIdAsync(evaluation.EvaluationParticipantId);
        if (participant == null || participant.EvaluationSessionId != sessionId)
            throw new EntityNotFoundException("Evaluation does not belong to this session");

        evaluation.SharedWithPlayer = dto.SharedWithPlayer;
        evaluationRepository.Update(evaluation);
        await evaluationRepository.SaveChangesAsync();

        analytics.CapturePlayerEvaluationShared(evaluationId, sessionId, userId, dto.SharedWithPlayer);
    }

    /// <summary>
    /// Returns how many players were given a result and how many player-by-exercise cells were
    /// actually scored — both already read here, so the completion event costs no extra query.
    /// </summary>
    private async Task<(int Evaluated, int Scored)> CalculateFinalResults(EvaluationSession session, EvaluationPlan plan)
    {
        var participants = (await participantRepository.GetBySessionIdAsync(session.Id)).ToList();
        var allScores = (await exerciseScoreRepository.GetBySessionIdAsync(session.Id)).ToList();

        // Get club skill matrix for level lookups
        ClubSkillMatrix? matrix = null;
        var matrixInfo = await clubsGrpcClient.GetDefaultSkillMatrixAsync(session.ClubId);
        if (matrixInfo != null)
        {
            matrix = MapSkillMatrixInfoToDomain(matrixInfo);
        }

        foreach (var participant in participants)
        {
            // Find or create PlayerEvaluation for this participant
            var evaluation = await evaluationRepository.GetByParticipantIdAsync(participant.Id);
            if (evaluation == null)
            {
                evaluation = new PlayerEvaluation
                {
                    EvaluationParticipantId = participant.Id,
                    PlayerId = participant.PlayerId,
                    EvaluatedByUserId = session.CoachUserId,
                    Outcome = EvaluationOutcome.Pending,
                    SessionId = session.Id
                };
                evaluationRepository.Add(evaluation);
                await evaluationRepository.SaveChangesAsync();
            }

            // Aggregate exercise-level metric scores into evaluation-level metric scores
            var playerScores = allScores.Where(s => s.PlayerId == participant.PlayerId).ToList();

            foreach (var exerciseScore in playerScores)
            {
                foreach (var metricScore in exerciseScore.MetricScores)
                {
                    // Check if this metric score already exists on the evaluation
                    var existingEvalMetricScore = evaluation.MetricScores
                        .FirstOrDefault(m => m.MetricId == metricScore.MetricId && m.PlayerExerciseScoreId == exerciseScore.Id);

                    if (existingEvalMetricScore != null)
                    {
                        existingEvalMetricScore.RawValue = metricScore.RawValue;
                        existingEvalMetricScore.NormalizedScore = metricScore.NormalizedScore;
                        metricScoreRepository.Update(existingEvalMetricScore);
                    }
                    else
                    {
                        var newMetricScore = new PlayerMetricScore
                        {
                            EvaluationId = evaluation.Id,
                            MetricId = metricScore.MetricId,
                            RawValue = metricScore.RawValue,
                            NormalizedScore = metricScore.NormalizedScore,
                            PlayerExerciseScoreId = exerciseScore.Id,
                            Notes = metricScore.Notes
                        };
                        metricScoreRepository.Add(newMetricScore);
                    }
                }
            }
            await metricScoreRepository.SaveChangesAsync();

            // Reload evaluation with all metric scores for skill calculation
            evaluation = await evaluationRepository.GetByIdWithScoresAsync(evaluation.Id);
            if (evaluation == null) continue;

            // Calculate skill scores
            var earnedPoints = scoreCalculationService.CalculateSkillPoints(evaluation, plan);
            var maxPoints = scoreCalculationService.CalculateMaxSkillPoints(plan);

            // Delete existing skill scores
            foreach (var skillScore in evaluation.SkillScores.ToList())
            {
                skillScore.IsDeleted = true;
                skillScoreRepository.Update(skillScore);
            }
            await skillScoreRepository.SaveChangesAsync();

            // Create new skill scores
            foreach (var skill in Enum.GetValues<VolleyballSkill>())
            {
                var earned = earnedPoints[skill];
                var max = maxPoints[skill];

                if (max <= 0) continue;

                var percentage = earned / max;
                var score = percentage * 10m;

                var newSkillScore = new PlayerSkillScore
                {
                    EvaluationId = evaluation.Id,
                    Skill = skill,
                    EarnedPoints = earned,
                    MaxPoints = max,
                    Score = Math.Round(score, 2),
                    Level = matrix != null
                        ? scoreCalculationService.GetLevelForScore(score, skill, matrix)
                        : null
                };

                skillScoreRepository.Add(newSkillScore);
            }
            await skillScoreRepository.SaveChangesAsync();
        }

        return (participants.Count, allScores.Count(s => s.Status == EvaluationScoreStatus.Scored));
    }

    private async Task<EvaluationSession> GetSessionAndValidateOwnership(Guid sessionId, Guid userId)
    {
        var session = await sessionRepository.GetByIdWithParticipantsAsync(sessionId);
        if (session == null)
            throw new EntityNotFoundException("Evaluation session not found");

        if (session.CoachUserId != userId)
            throw new ForbiddenException("Only the session coach can manage this session");

        return session;
    }

    private static ClubSkillMatrix MapSkillMatrixInfoToDomain(SkillMatrixInfo info)
    {
        return new ClubSkillMatrix
        {
            Id = info.MatrixId,
            Name = "Remote Matrix",
            Skills = info.Skills.Select(s =>
            {
                if (!Enum.TryParse<VolleyballSkill>(s.SkillKey, out var skill))
                    skill = VolleyballSkill.Passing;

                return new MatrixSkill
                {
                    Id = s.Id,
                    Skill = skill,
                    Bands = s.Bands.Select(b => new SkillBand
                    {
                        Id = b.Id,
                        Order = b.Order,
                        Label = b.Label,
                        MinScore = b.MinScore,
                        MaxScore = b.MaxScore
                    }).ToList()
                };
            }).ToList()
        };
    }
}
