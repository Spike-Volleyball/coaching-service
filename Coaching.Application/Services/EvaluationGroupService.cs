using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class EvaluationGroupService(
    IEvaluationSessionRepository sessionRepository,
    IEvaluationGroupRepository groupRepository,
    IEvaluationParticipantRepository participantRepository,
    IRepository<EvaluationGroupPlayer> groupPlayerRepository,
    IEvaluationAccess access,
    IMapper mapper) : IEvaluationGroupService
{
    public async Task<EvaluationGroupDto> CreateGroupAsync(Guid sessionId, CreateGroupDto dto, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);
        ValidateSessionModifiable(session);

        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new BadRequestException("Group name is required", ErrorCodeEnum.ValidationError);

        if (dto.EvaluatorUserId is { } evaluatorId)
            await EnsureMayEvaluateAsync(session, evaluatorId);

        // Determine next order number
        var existingGroups = await groupRepository.GetBySessionIdAsync(sessionId);
        var nextOrder = existingGroups.Any() ? existingGroups.Max(g => g.Order) + 1 : 0;

        var group = new EvaluationGroup
        {
            SessionId = sessionId,
            Name = dto.Name,
            EvaluatorUserId = dto.EvaluatorUserId,
            Order = nextOrder
        };

        groupRepository.Add(group);
        await groupRepository.SaveChangesAsync();

        // Add initial players if provided
        if (dto.PlayerIds is { Count: > 0 })
        {
            foreach (var playerId in dto.PlayerIds)
            {
                await AddPlayerToGroupInternal(session, group.Id, playerId);
            }
            await groupPlayerRepository.SaveChangesAsync();
        }

        return await GetGroupDtoAsync(group.Id);
    }

    public async Task<EvaluationGroupDto> UpdateGroupAsync(Guid sessionId, Guid groupId, UpdateGroupDto dto, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);
        ValidateSessionModifiable(session);

        var group = await groupRepository.GetByIdWithPlayersAsync(groupId);
        if (group == null || group.SessionId != sessionId)
            throw new EntityNotFoundException("Group not found");

        if (dto.ClearEvaluator)
        {
            if (dto.EvaluatorUserId.HasValue)
                throw new ValidationException("evaluatorUserId", "CLEAR_OR_ASSIGN",
                    "Assign an evaluator or clear the group's evaluator, not both");

            // A session starts only once every group has an evaluator, and a group of a running
            // session left without one could be scored by nobody but the coach.
            if (session.Status != EvaluationSessionStatus.Draft)
                throw new ValidationException("clearEvaluator", "SESSION_STARTED",
                    "A group's evaluator can only be removed before the session starts");

            group.EvaluatorUserId = null;
        }
        else if (dto.EvaluatorUserId is { } evaluatorId && evaluatorId != group.EvaluatorUserId)
        {
            await EnsureMayEvaluateAsync(session, evaluatorId);
            group.EvaluatorUserId = evaluatorId;
        }

        if (dto.Name != null) group.Name = dto.Name;

        groupRepository.Update(group);
        await groupRepository.SaveChangesAsync();

        return await GetGroupDtoAsync(groupId);
    }

    public async Task DeleteGroupAsync(Guid sessionId, Guid groupId, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);
        ValidateSessionModifiable(session);

        var group = await groupRepository.GetByIdWithPlayersAsync(groupId);
        if (group == null || group.SessionId != sessionId)
            throw new EntityNotFoundException("Group not found");

        // Soft-delete group players
        foreach (var player in group.Players)
        {
            player.IsDeleted = true;
            groupPlayerRepository.Update(player);
        }

        // Soft-delete the group
        group.IsDeleted = true;
        groupRepository.Update(group);
        await groupRepository.SaveChangesAsync();
    }

    public async Task<IEnumerable<EvaluationGroupDto>> AutoSplitGroupsAsync(Guid sessionId, AutoSplitGroupsDto dto, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);
        ValidateSessionModifiable(session);

        if (dto.NumberOfGroups < 2)
            throw new BadRequestException("Number of groups must be at least 2", ErrorCodeEnum.ValidationError);

        // Get all participants
        var participants = (await participantRepository.GetBySessionIdAsync(sessionId)).ToList();
        if (participants.Count == 0)
            throw new BadRequestException("No participants in this session to split into groups", ErrorCodeEnum.ValidationError);

        if (dto.NumberOfGroups > participants.Count)
            throw new BadRequestException(
                $"Cannot create {dto.NumberOfGroups} groups with only {participants.Count} participants",
                ErrorCodeEnum.ValidationError);

        // Delete existing groups
        var existingGroups = await groupRepository.GetBySessionIdAsync(sessionId);
        foreach (var existingGroup in existingGroups)
        {
            var groupWithPlayers = await groupRepository.GetByIdWithPlayersAsync(existingGroup.Id);
            if (groupWithPlayers == null) continue;

            foreach (var player in groupWithPlayers.Players)
            {
                player.IsDeleted = true;
                groupPlayerRepository.Update(player);
            }

            groupWithPlayers.IsDeleted = true;
            groupRepository.Update(groupWithPlayers);
        }
        await groupRepository.SaveChangesAsync();

        // Create new groups and distribute players evenly
        var groupNames = GenerateGroupNames(dto.NumberOfGroups);
        var newGroups = new List<EvaluationGroup>();

        for (var i = 0; i < dto.NumberOfGroups; i++)
        {
            var group = new EvaluationGroup
            {
                SessionId = sessionId,
                Name = groupNames[i],
                Order = i
            };
            groupRepository.Add(group);
            newGroups.Add(group);
        }
        await groupRepository.SaveChangesAsync();

        // Distribute participants across groups in round-robin fashion
        for (var i = 0; i < participants.Count; i++)
        {
            var groupIndex = i % dto.NumberOfGroups;
            var groupPlayer = new EvaluationGroupPlayer
            {
                GroupId = newGroups[groupIndex].Id,
                PlayerId = participants[i].PlayerId
            };
            groupPlayerRepository.Add(groupPlayer);
        }
        await groupPlayerRepository.SaveChangesAsync();

        // Return all newly created groups
        var result = new List<EvaluationGroupDto>();
        foreach (var group in newGroups)
        {
            result.Add(await GetGroupDtoAsync(group.Id));
        }
        return result;
    }

    public async Task<EvaluationGroupDto> AddPlayerToGroupAsync(Guid sessionId, Guid groupId, AssignPlayerToGroupDto dto, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);
        ValidateSessionModifiable(session);

        var group = await groupRepository.GetByIdWithPlayersAsync(groupId);
        if (group == null || group.SessionId != sessionId)
            throw new EntityNotFoundException("Group not found");

        await AddPlayerToGroupInternal(session, groupId, dto.PlayerId);
        await groupPlayerRepository.SaveChangesAsync();

        return await GetGroupDtoAsync(groupId);
    }

    public async Task RemovePlayerFromGroupAsync(Guid sessionId, Guid groupId, Guid playerId, Guid userId)
    {
        await GetSessionAndValidateOwnership(sessionId, userId);

        var group = await groupRepository.GetByIdWithPlayersAsync(groupId);
        if (group == null || group.SessionId != sessionId)
            throw new EntityNotFoundException("Group not found");

        var groupPlayer = group.Players.FirstOrDefault(p => p.PlayerId == playerId);
        if (groupPlayer == null)
            throw new EntityNotFoundException("Player not found in this group");

        groupPlayer.IsDeleted = true;
        groupPlayerRepository.Update(groupPlayer);
        await groupPlayerRepository.SaveChangesAsync();
    }

    public async Task MovePlayerAsync(Guid sessionId, MovePlayerDto dto, Guid userId)
    {
        var session = await GetSessionAndValidateOwnership(sessionId, userId);
        ValidateSessionModifiable(session);

        // Find the player's current group
        var allGroups = await groupRepository.GetBySessionIdAsync(sessionId);
        EvaluationGroupPlayer? currentGroupPlayer = null;

        foreach (var group in allGroups)
        {
            var groupWithPlayers = await groupRepository.GetByIdWithPlayersAsync(group.Id);
            currentGroupPlayer = groupWithPlayers?.Players.FirstOrDefault(p => p.PlayerId == dto.PlayerId);
            if (currentGroupPlayer != null) break;
        }

        if (currentGroupPlayer == null)
            throw new EntityNotFoundException("Player is not assigned to any group in this session");

        // Verify target group exists and belongs to this session
        var targetGroup = await groupRepository.GetByIdWithPlayersAsync(dto.TargetGroupId);
        if (targetGroup == null || targetGroup.SessionId != sessionId)
            throw new EntityNotFoundException("Target group not found");

        // Check player is not already in the target group
        if (targetGroup.Players.Any(p => p.PlayerId == dto.PlayerId))
            throw new BadRequestException("Player is already in the target group", ErrorCodeEnum.ValidationError);

        // Remove from current group
        currentGroupPlayer.IsDeleted = true;
        groupPlayerRepository.Update(currentGroupPlayer);

        // Add to target group
        var newGroupPlayer = new EvaluationGroupPlayer
        {
            GroupId = dto.TargetGroupId,
            PlayerId = dto.PlayerId
        };
        groupPlayerRepository.Add(newGroupPlayer);
        await groupPlayerRepository.SaveChangesAsync();
    }

    private async Task<EvaluationSession> GetSessionAndValidateOwnership(Guid sessionId, Guid userId)
    {
        var session = await sessionRepository.GetByIdWithParticipantsAsync(sessionId);
        if (session == null)
            throw new EntityNotFoundException("Evaluation session not found");

        if (session.CoachUserId != userId)
            throw new ForbiddenException("Only the session coach can manage groups");

        return session;
    }

    /// <summary>
    /// An evaluator appraises the players of their group, so they need the standing giving
    /// feedback in the session's club asks for; any user id used to be taken as one.
    /// </summary>
    private async Task EnsureMayEvaluateAsync(EvaluationSession session, Guid evaluatorId)
    {
        if (!await access.MayEvaluateInClubAsync(session.ClubId, evaluatorId))
            throw new ValidationException("evaluatorUserId", "NOT_ELIGIBLE",
                "This person cannot evaluate players in this club");
    }

    private static void ValidateSessionModifiable(EvaluationSession session)
    {
        if (session.Status == EvaluationSessionStatus.Completed)
            throw new BadRequestException("Cannot modify groups on a completed session", ErrorCodeEnum.ValidationError);
    }

    private async Task AddPlayerToGroupInternal(EvaluationSession session, Guid groupId, Guid playerId)
    {
        // Verify player is a session participant
        var participant = session.Participants.FirstOrDefault(p => p.PlayerId == playerId);
        if (participant == null)
            throw new BadRequestException(
                "Player is not a participant in this session. Add them as a participant first.",
                ErrorCodeEnum.ValidationError);

        // Ensure player is not already assigned to another group in this session
        var allGroups = await groupRepository.GetBySessionIdAsync(session.Id);
        foreach (var existingGroup in allGroups)
        {
            var groupWithPlayers = await groupRepository.GetByIdWithPlayersAsync(existingGroup.Id);
            if (groupWithPlayers?.Players.Any(p => p.PlayerId == playerId) == true)
                throw new BadRequestException(
                    $"Player is already assigned to group '{existingGroup.Name}'. Remove them first or use move.",
                    ErrorCodeEnum.ValidationError);
        }

        var groupPlayer = new EvaluationGroupPlayer
        {
            GroupId = groupId,
            PlayerId = playerId
        };
        groupPlayerRepository.Add(groupPlayer);
    }

    private async Task<EvaluationGroupDto> GetGroupDtoAsync(Guid groupId)
    {
        var group = await groupRepository.GetByIdWithPlayersAsync(groupId);
        return mapper.Map<EvaluationGroupDto>(group);
    }

    private static List<string> GenerateGroupNames(int count)
    {
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            // Generate names: Group A, Group B, ..., Group Z, Group AA, Group AB, ...
            var name = "Group " + GetLetterLabel(i);
            names.Add(name);
        }
        return names;
    }

    private static string GetLetterLabel(int index)
    {
        var result = string.Empty;
        do
        {
            result = (char)('A' + index % 26) + result;
            index = index / 26 - 1;
        } while (index >= 0);

        return result;
    }
}
