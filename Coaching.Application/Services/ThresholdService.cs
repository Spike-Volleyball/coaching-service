using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class ThresholdService(
    IRepository<EvaluationThreshold> thresholdRepository,
    IEvaluationAccess access,
    IMapper mapper) : IThresholdService
{
    public async Task<EvaluationThresholdDto> CreateAsync(Guid clubId, CreateThresholdDto request, Guid userId)
    {
        // Check for existing threshold
        var existing = await thresholdRepository.Query()
            .FirstOrDefaultAsync(t => t.ClubId == clubId && t.Skill == request.Skill && !t.IsDeleted);

        if (existing != null)
            throw new ConflictException("Threshold already exists for this skill");

        var threshold = new EvaluationThreshold
        {
            ClubId = clubId,
            Skill = request.Skill,
            MinScore = request.MinScore,
            Description = request.Description,
            IsActive = true,
            CreatedByUserId = userId
        };

        thresholdRepository.Add(threshold);
        await thresholdRepository.SaveChangesAsync();

        return mapper.Map<EvaluationThresholdDto>(threshold);
    }

    public async Task<IEnumerable<EvaluationThresholdDto>> GetByClubIdAsync(Guid clubId, Guid userId)
    {
        // Someone without standing sees what a club that set no thresholds shows.
        if (!await access.MayReadClubAsync(clubId, userId))
            return [];

        var thresholds = await thresholdRepository.Query()
            .Where(t => t.ClubId == clubId && !t.IsDeleted)
            .OrderBy(t => t.Skill)
            .ToListAsync();

        return mapper.Map<IEnumerable<EvaluationThresholdDto>>(thresholds);
    }

    public async Task<EvaluationThresholdDto> UpdateAsync(Guid id, UpdateThresholdDto request, Guid userId)
    {
        var threshold = await thresholdRepository.GetByIdAsync(id);
        if (threshold == null)
            throw new EntityNotFoundException("Threshold not found");

        if (threshold.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can update this threshold");

        if (request.MinScore.HasValue) threshold.MinScore = request.MinScore.Value;
        if (request.IsActive.HasValue) threshold.IsActive = request.IsActive.Value;
        if (request.Description != null) threshold.Description = request.Description;

        thresholdRepository.Update(threshold);
        await thresholdRepository.SaveChangesAsync();

        return mapper.Map<EvaluationThresholdDto>(threshold);
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var threshold = await thresholdRepository.GetByIdAsync(id);
        if (threshold == null)
            throw new EntityNotFoundException("Threshold not found");

        if (threshold.CreatedByUserId != userId)
            throw new ForbiddenException("Only the creator can delete this threshold");

        threshold.IsDeleted = true;
        thresholdRepository.Update(threshold);
        await thresholdRepository.SaveChangesAsync();
    }

    public async Task<ThresholdCheckResult> CheckPlayerAsync(Guid clubId, PlayerEvaluationDto evaluation)
    {
        var thresholds = await thresholdRepository.Query()
            .Where(t => t.ClubId == clubId && t.IsActive && !t.IsDeleted)
            .ToListAsync();

        var result = new ThresholdCheckResult
        {
            Passed = true,
            SkillResults = new List<SkillThresholdResult>()
        };

        // Check per-skill thresholds
        foreach (var skillScore in evaluation.SkillScores)
        {
            var threshold = thresholds.FirstOrDefault(t => t.Skill == skillScore.Skill);
            if (threshold == null) continue;

            var skillResult = new SkillThresholdResult
            {
                Skill = skillScore.Skill,
                Score = skillScore.Score,
                MinRequired = threshold.MinScore,
                Passed = skillScore.Score >= threshold.MinScore
            };

            result.SkillResults.Add(skillResult);

            if (!skillResult.Passed)
                result.Passed = false;
        }

        // Check overall threshold if exists
        var overallThreshold = thresholds.FirstOrDefault(t => t.Skill == null);
        if (overallThreshold != null)
        {
            var averageScore = evaluation.SkillScores.Any()
                ? evaluation.SkillScores.Average(s => s.Score)
                : 0;

            if (averageScore < overallThreshold.MinScore)
                result.Passed = false;
        }

        result.SuggestedOutcome = result.Passed ? "Pass" : "Fail";
        return result;
    }
}
