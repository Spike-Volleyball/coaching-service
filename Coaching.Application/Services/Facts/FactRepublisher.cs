using Coaching.Application.DTOs.Facts;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Models.Feedback;
using Coaching.Domain.Models.Tactics;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories.Interfaces;

namespace Coaching.Application.Services.Facts;

public class FactRepublisher(
    IFeedbackRepository feedbackRepository,
    IRepository<TacticsBoard> boards,
    IPublishEndpoint publishEndpoint,
    TimeProvider timeProvider) : IFactRepublisher
{
    /// <summary>
    /// Each batch is published and then saved, and the save is what sends it: the outbox writes a
    /// publish down only with a save, and one save for the whole history would hold every row of it
    /// in a single unit of work.
    /// </summary>
    private const int BatchSize = 50;

    public async Task<FactsRepublishedDto> RepublishAsync(CancellationToken ct = default) => new()
    {
        Praise = await RepublishAsync(
            SharedFeedbackAsync,
            feedback => PraiseFact.Of(feedback).Snapshot(feedback, timeProvider.GetUtcNow().UtcDateTime),
            feedbackRepository.SaveChangesAsync,
            ct),
        Boards = await RepublishAsync(
            DrawnBoardsAsync,
            board => DrawnBoardFact.Snapshot(board, board.DrawnAt!.Value, timeProvider.GetUtcNow().UtcDateTime),
            boards.SaveChangesAsync,
            ct),
    };

    /// <summary>Publishes the snapshot of everything <paramref name="page"/> finds, a batch at a time, and says how many went out.</summary>
    private async Task<int> RepublishAsync<T>(
        Func<int, CancellationToken, Task<List<T>>> page, Func<T, object> snapshot, Func<Task> save, CancellationToken ct)
    {
        var published = 0;

        while (await page(published, ct) is { Count: > 0 } batch)
        {
            foreach (var item in batch)
                await publishEndpoint.Publish(snapshot(item), ct);

            await save();
            published += batch.Count;
        }

        return published;
    }

    /// Untracked, so a long history does not pile up in the change tracker batch after batch.
    private Task<List<Feedback>> SharedFeedbackAsync(int skip, CancellationToken ct) =>
        feedbackRepository.QueryNoTracking()
            .Include(f => f.Praise)
            .Where(f => f.SharedWithPlayer && !f.IsDeleted)
            .OrderBy(f => f.Id)
            .Skip(skip)
            .Take(BatchSize)
            .ToListAsync(ct);

    /// Only a board somebody made is ever given a first-drawn time, so having one is the whole test.
    private Task<List<TacticsBoard>> DrawnBoardsAsync(int skip, CancellationToken ct) =>
        boards.QueryNoTracking()
            .Where(b => b.DrawnAt != null)
            .OrderBy(b => b.Id)
            .Skip(skip)
            .Take(BatchSize)
            .ToListAsync(ct);
}
