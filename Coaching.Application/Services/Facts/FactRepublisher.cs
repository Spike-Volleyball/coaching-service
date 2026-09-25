using Coaching.Application.DTOs.Facts;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Models.Feedback;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Coaching.Application.Services.Facts;

public class FactRepublisher(
    IFeedbackRepository feedbackRepository,
    IPublishEndpoint publishEndpoint,
    TimeProvider timeProvider) : IFactRepublisher
{
    /// <summary>
    /// Each batch is published and then saved, and the save is what sends it: the outbox writes a
    /// publish down only with a save, and one save for the whole history would hold every row of it
    /// in a single unit of work.
    /// </summary>
    private const int BatchSize = 50;

    public async Task<FactsRepublishedDto> RepublishAsync(CancellationToken ct = default)
    {
        var published = 0;

        while (await SharedFeedbackAsync(published, ct) is { Count: > 0 } batch)
        {
            foreach (var feedback in batch)
            {
                var snapshot = PraiseFact.Of(feedback).Snapshot(feedback, timeProvider.GetUtcNow().UtcDateTime);
                await publishEndpoint.Publish(snapshot, ct);
            }

            await feedbackRepository.SaveChangesAsync();
            published += batch.Count;
        }

        return new FactsRepublishedDto { Praise = published };
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
}
