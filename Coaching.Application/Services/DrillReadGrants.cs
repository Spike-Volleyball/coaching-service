using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coaching.Application.Services;

public class DrillReadGrants(
    ITrainingPlanRepository planRepository,
    IFeedbackRepository feedbackRepository,
    IEventsGrpcClient eventsClient) : IDrillReadGrants
{
    public async Task<bool> GrantsAsync(Guid drillId, Guid userId, DrillReadContext context) =>
        (context.EventId is { } eventId && await InEventPlanTheReaderMayReadAsync(drillId, eventId, userId))
        || (context.FeedbackId is { } feedbackId && await OnFeedbackTheReaderMayReadAsync(drillId, feedbackId, userId));

    /// <summary>
    /// The plan is asked first: a drill that is not in it is refused without a call to events-service.
    /// </summary>
    private async Task<bool> InEventPlanTheReaderMayReadAsync(Guid drillId, Guid eventId, Guid userId) =>
        await planRepository.QueryNoTracking().AnyAsync(p =>
            p.EventId == eventId && p.PlanType == PlanType.Instance && !p.IsDeleted
            && p.Items.Any(item => item.DrillId == drillId
                || item.Stations.Any(station => station.Items.Any(row => row.DrillId == drillId))))
        && (await EventPlanAccess.StandingAsync(eventsClient, eventId, userId)).MayRead;

    /// <summary>
    /// Taking a drill or a point off feedback only marks its row deleted, so only live rows count.
    /// </summary>
    private async Task<bool> OnFeedbackTheReaderMayReadAsync(Guid drillId, Guid feedbackId, Guid userId)
    {
        var feedback = await feedbackRepository.QueryNoTracking().FirstOrDefaultAsync(f =>
            f.Id == feedbackId && !f.IsDeleted
            && f.ImprovementPoints.Any(point => !point.IsDeleted
                && point.AttachedDrills.Any(link => !link.IsDeleted && link.DrillId == drillId)));

        return feedback is not null && feedback.IsReadableBy(userId);
    }
}
