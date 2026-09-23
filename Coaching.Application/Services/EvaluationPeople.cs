using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Services;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Models;

namespace Coaching.Application.Services;

public class EvaluationPeople(IRepository<UserProfile> profiles) : IEvaluationPeople
{
    public async Task FillAsync(IReadOnlyCollection<EvaluationGroupDto> groups)
    {
        var people = await ProfilesAsync(groups
            .SelectMany(g => g.Players.Select(p => (Guid?)p.PlayerId).Append(g.EvaluatorUserId)));

        foreach (var group in groups)
        {
            group.EvaluatorName = NameOf(people, group.EvaluatorUserId);
            foreach (var player in group.Players)
            {
                player.PlayerName = NameOf(people, player.PlayerId);
                player.AvatarUrl = people.GetValueOrDefault(player.PlayerId)?.ImageUrl;
            }
        }
    }

    public async Task FillAsync(SessionProgressDto progress)
    {
        var people = await ProfilesAsync(progress.Groups.Select(g => g.EvaluatorUserId));

        foreach (var group in progress.Groups)
            group.EvaluatorName = NameOf(people, group.EvaluatorUserId);
    }

    /// <summary>One read for everyone the caller shows, however many groups they sit in.</summary>
    private async Task<Dictionary<Guid, UserProfile>> ProfilesAsync(IEnumerable<Guid?> userIds)
    {
        var ids = userIds.OfType<Guid>().Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        return await profiles.QueryNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);
    }

    private static string? NameOf(Dictionary<Guid, UserProfile> people, Guid? userId) =>
        userId is { } id && people.TryGetValue(id, out var profile) ? profile.FullName : null;
}
