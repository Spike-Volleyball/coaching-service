using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Models.Evaluation;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class EvaluationExerciseService(
    IEvaluationExerciseRepository exerciseRepository,
    IRepository<EvaluationMetric> metricRepository,
    IRepository<MetricSkillWeight> weightRepository,
    IEvaluationAccess access,
    IMapper mapper) : IEvaluationExerciseService
{
    private const string ExerciseNotFound = "Exercise not found";

    public async Task<EvaluationExerciseDto> CreateAsync(CreateEvaluationExerciseDto request, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BadRequestException("Exercise name is required", ErrorCodeEnum.ValidationError);

        var exercise = mapper.Map<EvaluationExercise>(request);
        exercise.CreatedByUserId = userId;

        exerciseRepository.Add(exercise);
        await exerciseRepository.SaveChangesAsync();

        // Add metrics
        if (request.Metrics != null)
        {
            var order = 1;
            foreach (var metricDto in request.Metrics)
            {
                await AddMetricInternal(exercise.Id, metricDto, order++);
            }
        }

        return await FindAsync(exercise.Id) ?? throw new Exception("Failed to retrieve created exercise");
    }

    public async Task<EvaluationExerciseDto> GetByIdForUserAsync(Guid id, Guid? userId)
    {
        // JwtPayloadProvider hands an anonymous caller Guid.Empty rather than null; collapsing it
        // keeps an anonymous read from being asked about as a club question about nobody.
        var readerId = userId is { } user && user != Guid.Empty ? user : (Guid?)null;
        var exercise = await exerciseRepository.GetByIdWithMetricsAsync(id);

        // A refusal answers as a missing exercise does: a 404 for a signed-in reader, and for an
        // anonymous one the same 401 either way, so a public link can still prompt a sign-in.
        if (exercise == null || !await access.MayReadExerciseAsync(exercise, readerId))
            throw readerId.HasValue
                ? new EntityNotFoundException(ExerciseNotFound)
                : new UnauthorizedException(
                    "Only logged in users have access to this functionality. Please, login.",
                    ErrorCodeEnum.Unauthorized);

        return mapper.Map<EvaluationExerciseDto>(exercise);
    }

    public async Task<IEnumerable<EvaluationExerciseDto>> GetByClubIdAsync(Guid clubId, Guid userId)
    {
        // Someone without standing sees what a club with no exercises of its own shows.
        if (!await access.MayReadClubAsync(clubId, userId))
            return [];

        var exercises = await exerciseRepository.GetByClubIdAsync(clubId);
        return mapper.Map<IEnumerable<EvaluationExerciseDto>>(exercises);
    }

    public async Task<ExerciseListResponseDto> GetPublicExercisesAsync(int page = 1, int pageSize = 20)
    {
        var exercises = await exerciseRepository.GetPublicExercisesAsync(page, pageSize);
        var total = await exerciseRepository.Query()
            .CountAsync(e => e.ClubId == null && !e.IsDeleted);

        return new ExerciseListResponseDto
        {
            Items = mapper.Map<IEnumerable<EvaluationExerciseDto>>(exercises),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<ExerciseListResponseDto> GetUserExercisesAsync(Guid userId, int page = 1, int pageSize = 20)
    {
        var exercises = await exerciseRepository.GetUserExercisesAsync(userId, page, pageSize);
        var total = await exerciseRepository.Query()
            .CountAsync(e => e.CreatedByUserId == userId && !e.IsDeleted);

        return new ExerciseListResponseDto
        {
            Items = mapper.Map<IEnumerable<EvaluationExerciseDto>>(exercises),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<EvaluationExerciseDto> UpdateAsync(Guid id, UpdateEvaluationExerciseDto request, Guid userId)
    {
        var exercise = await exerciseRepository.GetByIdAsync(id);
        if (exercise == null)
            throw new EntityNotFoundException(ExerciseNotFound);

        if (exercise.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can update this exercise");

        if (request.Name != null) exercise.Name = request.Name;
        if (request.Description != null) exercise.Description = request.Description;
        if (request.Level.HasValue) exercise.Level = request.Level.Value;

        exerciseRepository.Update(exercise);
        await exerciseRepository.SaveChangesAsync();

        return await FindAsync(id) ?? throw new Exception("Failed to retrieve exercise");
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var exercise = await exerciseRepository.GetByIdAsync(id);
        if (exercise == null)
            throw new EntityNotFoundException(ExerciseNotFound);

        if (exercise.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can delete this exercise");

        exercise.IsDeleted = true;
        exerciseRepository.Update(exercise);
        await exerciseRepository.SaveChangesAsync();
    }

    public async Task<EvaluationExerciseDto> AddMetricAsync(Guid exerciseId, AddMetricDto request, Guid userId)
    {
        var exercise = await exerciseRepository.GetByIdWithMetricsAsync(exerciseId);
        if (exercise == null)
            throw new EntityNotFoundException(ExerciseNotFound);

        if (exercise.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can modify this exercise");

        ValidateSkillWeights(request.SkillWeights);

        var maxOrder = exercise.Metrics.Any() ? exercise.Metrics.Max(m => m.Order) : 0;
        var order = request.Order ?? maxOrder + 1;

        await AddMetricInternal(exerciseId, new CreateEvaluationMetricDto
        {
            Name = request.Name,
            Type = request.Type,
            MaxPoints = request.MaxPoints,
            Config = request.Config,
            SkillWeights = request.SkillWeights
        }, order);

        return await FindAsync(exerciseId) ?? throw new Exception("Failed to retrieve exercise");
    }

    public async Task<EvaluationExerciseDto> UpdateMetricAsync(Guid exerciseId, Guid metricId, UpdateEvaluationMetricDto request, Guid userId)
    {
        var exercise = await exerciseRepository.GetByIdAsync(exerciseId);
        if (exercise == null)
            throw new EntityNotFoundException(ExerciseNotFound);

        if (exercise.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can modify this exercise");

        var metric = await metricRepository.GetByIdAsync(metricId);
        if (metric == null || metric.ExerciseId != exerciseId)
            throw new EntityNotFoundException("Metric not found");

        if (request.Name != null) metric.Name = request.Name;
        if (request.Type.HasValue) metric.Type = request.Type.Value;
        if (request.MaxPoints.HasValue) metric.MaxPoints = request.MaxPoints.Value;
        if (request.Config != null) metric.Config = request.Config;

        metricRepository.Update(metric);
        await metricRepository.SaveChangesAsync();

        return await FindAsync(exerciseId) ?? throw new Exception("Failed to retrieve exercise");
    }

    public async Task<EvaluationExerciseDto> RemoveMetricAsync(Guid exerciseId, Guid metricId, Guid userId)
    {
        var exercise = await exerciseRepository.GetByIdAsync(exerciseId);
        if (exercise == null)
            throw new EntityNotFoundException(ExerciseNotFound);

        if (exercise.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can modify this exercise");

        var metric = await metricRepository.GetByIdAsync(metricId);
        if (metric == null || metric.ExerciseId != exerciseId)
            throw new EntityNotFoundException("Metric not found");

        metric.IsDeleted = true;
        metricRepository.Update(metric);
        await metricRepository.SaveChangesAsync();

        return await FindAsync(exerciseId) ?? throw new Exception("Failed to retrieve exercise");
    }

    public async Task<EvaluationExerciseDto> UpdateMetricSkillWeightsAsync(Guid exerciseId, Guid metricId, List<CreateMetricSkillWeightDto> weights, Guid userId)
    {
        var exercise = await exerciseRepository.GetByIdAsync(exerciseId);
        if (exercise == null)
            throw new EntityNotFoundException(ExerciseNotFound);

        if (exercise.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can modify this exercise");

        ValidateSkillWeights(weights);

        // Hard delete existing weights (unique constraint on MetricId+Skill prevents soft delete)
        var existingWeights = await weightRepository.Query()
            .Where(w => w.MetricId == metricId)
            .ToListAsync();

        foreach (var weight in existingWeights)
        {
            weightRepository.Delete(weight);
        }
        await weightRepository.SaveChangesAsync();

        // Add new weights
        foreach (var weightDto in weights)
        {
            var weight = mapper.Map<MetricSkillWeight>(weightDto);
            weight.MetricId = metricId;
            weightRepository.Add(weight);
        }
        await weightRepository.SaveChangesAsync();

        return await FindAsync(exerciseId) ?? throw new Exception("Failed to retrieve exercise");
    }

    private async Task AddMetricInternal(Guid exerciseId, CreateEvaluationMetricDto request, int order)
    {
        ValidateSkillWeights(request.SkillWeights);

        var metric = mapper.Map<EvaluationMetric>(request);
        metric.ExerciseId = exerciseId;
        metric.Order = order;

        metricRepository.Add(metric);
        await metricRepository.SaveChangesAsync();

        // Add skill weights
        foreach (var weightDto in request.SkillWeights)
        {
            var weight = mapper.Map<MetricSkillWeight>(weightDto);
            weight.MetricId = metric.Id;
            weightRepository.Add(weight);
        }
        await weightRepository.SaveChangesAsync();
    }

    private static void ValidateSkillWeights(List<CreateMetricSkillWeightDto> weights)
    {
        var totalPercentage = weights.Sum(w => w.Percentage);
        if (Math.Abs(totalPercentage - 100) > 0.01m)
            throw new BadRequestException($"Skill weights must sum to 100%, got {totalPercentage}%", ErrorCodeEnum.ValidationError);
    }

    /// <summary>The exercise a write just touched, for its answer: the writer is its author.</summary>
    private async Task<EvaluationExerciseDto?> FindAsync(Guid id)
    {
        var exercise = await exerciseRepository.GetByIdWithMetricsAsync(id);
        return exercise == null ? null : mapper.Map<EvaluationExerciseDto>(exercise);
    }
}
