using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Microsoft.EntityFrameworkCore;
using Shared.Exceptions;
using Shared.Services.Analytics;

namespace Coaching.Application.Services;

public class RunService : IRunService
{
    private const int SecondsPerMinute = 60;

    private readonly ITrainingPlanRunRepository _runRepository;
    private readonly ITrainingPlanRunItemRepository _runItemRepository;
    private readonly IRunStationRepository _stationRepository;
    private readonly ITrainingPlanRepository _planRepository;
    private readonly IRunBroadcaster _broadcaster;
    private readonly IEventsGrpcClient _eventsGrpcClient;
    private readonly TimeProvider _timeProvider;
    private readonly IAnalyticsCapture _analytics;

    public RunService(
        ITrainingPlanRunRepository runRepository,
        ITrainingPlanRunItemRepository runItemRepository,
        IRunStationRepository stationRepository,
        ITrainingPlanRepository planRepository,
        IRunBroadcaster broadcaster,
        IEventsGrpcClient eventsGrpcClient,
        TimeProvider timeProvider,
        IAnalyticsCapture analytics)
    {
        _runRepository = runRepository;
        _runItemRepository = runItemRepository;
        _stationRepository = stationRepository;
        _planRepository = planRepository;
        _broadcaster = broadcaster;
        _eventsGrpcClient = eventsGrpcClient;
        _timeProvider = timeProvider;
        _analytics = analytics;
    }

    public async Task<RunDto?> GetByEventIdAsync(Guid eventId, Guid requestingUserId)
    {
        // Gate BEFORE touching the run table: resolving the plan creator first and checking it
        // against requestingUserId lets a legitimate creator skip the events-service round trip,
        // but an unauthorized caller must get the identical response (403/404) whether or not a
        // run — or even a plan — exists yet, so nothing about the run's existence leaks. Mirrors
        // TrainingPlanService.GetByEventIdAsync's gate-before-fetch order for the same reason.
        var creatorId = await InstancePlanQuery(eventId)
            .Select(p => (Guid?)p.CreatedByUserId)
            .FirstOrDefaultAsync();
        var isCreator = creatorId == requestingUserId;

        if (!isCreator)
            await EnsureCanReadRunAsync(eventId, requestingUserId);

        var run = await _runRepository.GetByEventIdWithDetailsAsync(eventId);
        return run == null ? null : MapToDto(run, isCreator);
    }

    // Mirrors TrainingPlanService.GetByEventIdAsync's participant/eventExists check (the sibling
    // read for the same event-attached plan) exactly, extended with the event-admin/host check
    // already used elsewhere in coaching-service (FeedbackAuthorizationService,
    // TrainingPlanService.PromoteToTemplateAsync) for the case where a host isn't in the
    // events-service participant roster.
    private async Task EnsureCanReadRunAsync(Guid eventId, Guid userId)
    {
        var (eventExists, onTheEvent) = await StandingOnEventAsync(eventId, userId);
        if (!eventExists)
            throw new EntityNotFoundException("Event not found");

        if (!onTheEvent)
            throw new ForbiddenException("Only event participants, hosts, or the plan creator can view this run");
    }

    public async Task<bool> CanReadRunAsync(Guid eventId, Guid userId) =>
        await InstancePlanQuery(eventId).AnyAsync(p => p.CreatedByUserId == userId)
        || (await StandingOnEventAsync(eventId, userId)).OnTheEvent;

    /// <summary>A participant of any status or a host; a host need not be on the roster.</summary>
    private async Task<(bool EventExists, bool OnTheEvent)> StandingOnEventAsync(Guid eventId, Guid userId)
    {
        var (isParticipant, eventExists) = await _eventsGrpcClient.IsEventParticipantAsync(eventId, userId);
        return (eventExists, eventExists && (isParticipant || await _eventsGrpcClient.IsEventAdminAsync(eventId, userId)));
    }

    public async Task<RunDto> StartAsync(Guid eventId, Guid requestingUserId)
    {
        var plan = await GetInstancePlanOrThrowAsync(eventId);
        EnsureCreator(plan, requestingUserId);

        var now = Now();
        var orderedItems = plan.Items.OrderBy(i => i.Order).ToList();
        var firstItem = orderedItems.FirstOrDefault();

        var run = await _runRepository.GetByEventIdWithDetailsAsync(eventId);

        TrainingPlanRunItem NewRunItem(PlanItem item) => new()
        {
            RunId = run!.Id,
            PlanItemId = item.Id,
            Kind = item.Kind,
            Title = item.Title,
            DrillId = item.DrillId,
            Order = item.Order,
            PlannedDurationSeconds = item.Duration * SecondsPerMinute,
            ActualElapsedSeconds = 0,
            StartedAtUtc = item.Id == firstItem?.Id ? now : null,
            CompletedAtUtc = null,
            Stations = SnapshotStations(item),
        };

        if (run == null)
        {
            run = new TrainingPlanRun
            {
                PlanId = plan.Id,
                EventId = eventId,
                StartedByUserId = requestingUserId
            };
            _runRepository.Add(run);

            foreach (var item in orderedItems)
            {
                run.Items.Add(NewRunItem(item));
            }
        }
        else
        {
            // Restart in place, reconciled to the CURRENT plan (it may have been edited on web
            // between runs): reset rows whose plan item still exists, add rows for new plan items,
            // drop rows whose plan item is gone. Reusing existing rows keeps them to plain UPDATEs;
            // a blanket clear + re-add orphaned every child and made EF emit deletes that hit 0
            // rows (DbUpdateConcurrencyException).
            //
            // The run is tracked here, and BaseEntity assigns an Id in its constructor — so a row
            // merely added to run.Items reads to EF as an existing row and is saved as an UPDATE
            // matching nothing. Every add and delete below therefore also states itself against
            // the child's own set; the collection is kept in step for the response mapping.
            var planItemIds = orderedItems.Select(i => i.Id).ToHashSet();
            foreach (var stale in run.Items.Where(ri => !planItemIds.Contains(ri.PlanItemId)).ToList())
            {
                _runItemRepository.Delete(stale);
                run.Items.Remove(stale);
            }

            foreach (var item in orderedItems)
            {
                var runItem = run.Items.FirstOrDefault(ri => ri.PlanItemId == item.Id);
                if (runItem == null)
                {
                    var added = NewRunItem(item);
                    run.Items.Add(added);
                    _runItemRepository.Add(added);
                }
                else
                {
                    runItem.Kind = item.Kind;
                    runItem.Title = item.Title;
                    runItem.DrillId = item.DrillId;
                    runItem.Order = item.Order;
                    runItem.PlannedDurationSeconds = item.Duration * SecondsPerMinute;
                    runItem.ActualElapsedSeconds = 0;
                    runItem.StartedAtUtc = item.Id == firstItem?.Id ? now : null;
                    runItem.CompletedAtUtc = null;
                    ResnapshotStations(runItem, item);
                }
            }
        }

        run.StartedByUserId = requestingUserId;
        run.Status = RunStatus.Running;
        run.StartedAtUtc = now;
        run.CompletedAtUtc = null;
        run.CurrentItemId = firstItem?.Id;
        run.CurrentItemStartedAtUtc = firstItem != null ? now : null;
        run.CurrentItemPausedElapsedSeconds = 0;

        await _runRepository.SaveChangesAsync();

        // A restart is a start: the coach is running the practice again, and the run rows were
        // just reset to the plan as it stands now.
        _analytics.Capture(requestingUserId, AnalyticsEventNames.PracticeRunStarted, new Dictionary<string, object?>
        {
            ["event_id"] = eventId,
            ["plan_id"] = plan.Id,
            ["item_count"] = run.Items.Count
        });

        return await BroadcastAsync(eventId, run, requestingUserId == plan.CreatedByUserId);
    }

    /// <summary>
    /// The run's own copy of a Stations row's groups, in seconds. Taken here rather than read
    /// from the plan on every request for the same reason the drill id is: the plan can be
    /// edited — or the block deleted — while the practice is running, and the coach on the
    /// court is running what they started, not what the plan says now.
    /// </summary>
    private static List<RunStation> SnapshotStations(PlanItem item) =>
        item.Stations
            .OrderBy(st => st.Order)
            .Select(st => new RunStation
            {
                Name = st.Name,
                Order = st.Order,
                Items = st.Items
                    .OrderBy(r => r.Order)
                    .Select(r => new RunStationItem
                    {
                        Kind = r.Kind,
                        DrillId = r.DrillId,
                        Title = r.Title,
                        Order = r.Order,
                        DurationSeconds = r.Duration * SecondsPerMinute,
                        Notes = r.Notes
                    })
                    .ToList()
            })
            .ToList();

    /// <summary>
    /// A kept run item's groups are snapshot and nothing else — they hold no elapsed time and no
    /// progress — so a restart replaces them wholesale instead of reconciling them row by row.
    /// The run item itself is still reused, which is the point of the reconcile above: its
    /// timings belong to the run, and only the plan's shape is being re-read.
    /// </summary>
    private void ResnapshotStations(TrainingPlanRunItem runItem, PlanItem item)
    {
        foreach (var existing in runItem.Stations.ToList())
        {
            _stationRepository.Delete(existing);
        }
        runItem.Stations.Clear();

        var replacements = SnapshotStations(item);
        foreach (var station in replacements)
        {
            station.RunItemId = runItem.Id;
            runItem.Stations.Add(station);
        }
        _stationRepository.AddRange(replacements);
    }

    public async Task<RunDto> PauseAsync(Guid eventId, Guid requestingUserId)
    {
        var (run, isCreator) = await LoadForControlAsync(eventId, requestingUserId);

        if (run.Status == RunStatus.Running)
        {
            run.CurrentItemPausedElapsedSeconds = ElapsedSeconds(run, Now());
            run.CurrentItemStartedAtUtc = null;
            run.Status = RunStatus.Paused;
            await _runRepository.SaveChangesAsync();
            return await BroadcastAsync(eventId, run, isCreator);
        }

        return MapToDto(run, isCreator);
    }

    public async Task<RunDto> ResumeAsync(Guid eventId, Guid requestingUserId)
    {
        var (run, isCreator) = await LoadForControlAsync(eventId, requestingUserId);

        if (run.Status == RunStatus.Paused)
        {
            run.CurrentItemStartedAtUtc = Now().AddSeconds(-run.CurrentItemPausedElapsedSeconds);
            run.Status = RunStatus.Running;
            await _runRepository.SaveChangesAsync();
            return await BroadcastAsync(eventId, run, isCreator);
        }

        return MapToDto(run, isCreator);
    }

    public async Task<RunDto> AdvanceAsync(Guid eventId, Guid fromItemId, Guid requestingUserId)
    {
        var (run, isCreator) = await LoadForControlAsync(eventId, requestingUserId);

        // Guard against double-tap / concurrent advance.
        if (run.CurrentItemId != fromItemId)
            return MapToDto(run, isCreator);

        FinalizeCurrentItem(run);

        var ordered = run.Items.OrderBy(i => i.Order).ToList();
        var currentOrder = ordered.First(c => c.PlanItemId == fromItemId).Order;
        var nextItem = ordered.FirstOrDefault(i => i.Order > currentOrder
            && i.CompletedAtUtc == null);

        var now = Now();
        if (nextItem == null)
        {
            run.Status = RunStatus.Completed;
            run.CurrentItemId = null;
            run.CurrentItemStartedAtUtc = null;
            run.CompletedAtUtc = now;
        }
        else
        {
            run.CurrentItemId = nextItem.PlanItemId;
            run.CurrentItemStartedAtUtc = now;
            run.CurrentItemPausedElapsedSeconds = 0;
            nextItem.StartedAtUtc = now;
        }

        await _runRepository.SaveChangesAsync();

        // Advancing off the end of the plan is the run finishing, and that is the same fact the
        // finish button records — so it is the same event, once, from whichever path got there.
        if (nextItem == null)
            _analytics.CapturePracticeRunCompleted(run, requestingUserId);

        return await BroadcastAsync(eventId, run, isCreator);
    }

    public async Task<RunDto> CompleteAsync(Guid eventId, Guid requestingUserId)
    {
        var (run, isCreator) = await LoadForControlAsync(eventId, requestingUserId);

        if (run.Status == RunStatus.Completed)
            return MapToDto(run, isCreator);

        FinalizeCurrentItem(run);
        run.Status = RunStatus.Completed;
        run.CurrentItemId = null;
        run.CurrentItemStartedAtUtc = null;
        run.CompletedAtUtc = Now();

        await _runRepository.SaveChangesAsync();

        _analytics.CapturePracticeRunCompleted(run, requestingUserId);

        return await BroadcastAsync(eventId, run, isCreator);
    }

    private void FinalizeCurrentItem(TrainingPlanRun run)
    {
        if (run.CurrentItemId == null) return;
        var current = run.Items.FirstOrDefault(i => i.PlanItemId == run.CurrentItemId);
        if (current == null) return;

        var now = Now();
        current.ActualElapsedSeconds = ElapsedSeconds(run, now);
        current.CompletedAtUtc = now;
    }

    private static int ElapsedSeconds(TrainingPlanRun run, DateTime now)
    {
        if (run.Status == RunStatus.Paused || run.CurrentItemStartedAtUtc == null)
            return run.CurrentItemPausedElapsedSeconds;

        var elapsed = (now - run.CurrentItemStartedAtUtc.Value).TotalSeconds;
        return elapsed < 0 ? 0 : (int)elapsed;
    }

    private async Task<(TrainingPlanRun run, bool isCreator)> LoadForControlAsync(Guid eventId, Guid requestingUserId)
    {
        var run = await _runRepository.GetByEventIdWithDetailsAsync(eventId)
            ?? throw new EntityNotFoundException("No run has been started for this event");

        var creatorId = await GetPlanCreatorIdAsync(run.PlanId);
        if (requestingUserId != creatorId && !await _eventsGrpcClient.IsEventAdminAsync(eventId, requestingUserId))
            throw new ForbiddenException("Only the plan creator or an event admin can control the run");

        return (run, true);
    }

    private IQueryable<TrainingPlan> InstancePlanQuery(Guid eventId) =>
        _planRepository.Query().Where(p => p.EventId == eventId && p.PlanType == PlanType.Instance && !p.IsDeleted);

    private async Task<TrainingPlan> GetInstancePlanOrThrowAsync(Guid eventId)
    {
        var plan = await InstancePlanQuery(eventId)
            .Include(p => p.Items)
                .ThenInclude(i => i.Stations)
                    .ThenInclude(s => s.Items)
            .FirstOrDefaultAsync()
            ?? throw new EntityNotFoundException("No training plan is attached to this event");

        return plan;
    }

    private async Task<Guid> GetPlanCreatorIdAsync(Guid planId)
    {
        var creatorId = await _planRepository.Query()
            .Where(p => p.Id == planId)
            .Select(p => (Guid?)p.CreatedByUserId)
            .FirstOrDefaultAsync()
            ?? throw new EntityNotFoundException("Training plan not found");

        return creatorId;
    }

    private static void EnsureCreator(TrainingPlan plan, Guid requestingUserId)
    {
        if (plan.CreatedByUserId != requestingUserId)
            throw new ForbiddenException("Only the plan creator can control the run");
    }

    private async Task<RunDto> BroadcastAsync(Guid eventId, TrainingPlanRun run, bool canControl)
    {
        var dto = MapToDto(run, canControl);
        await _broadcaster.BroadcastRunUpdatedAsync(eventId, dto);
        return dto;
    }

    private RunDto MapToDto(TrainingPlanRun run, bool canControl) => new()
    {
        Id = run.Id,
        PlanId = run.PlanId,
        EventId = run.EventId,
        StartedByUserId = run.StartedByUserId,
        Status = run.Status,
        CurrentItemId = run.CurrentItemId,
        CurrentItemStartedAt = run.CurrentItemStartedAtUtc,
        CurrentItemPausedElapsedSeconds = run.CurrentItemPausedElapsedSeconds,
        StartedAt = run.StartedAtUtc,
        CompletedAt = run.CompletedAtUtc,
        ServerTime = Now(),
        CanControl = canControl,
        Items = run.Items
            .OrderBy(i => i.Order)
            .Select(i => new RunItemDto
            {
                Id = i.Id,
                PlanItemId = i.PlanItemId,
                Kind = i.Kind,
                Title = i.Title,
                DrillId = i.DrillId,
                Order = i.Order,
                PlannedDurationSeconds = i.PlannedDurationSeconds,
                ActualElapsedSeconds = i.ActualElapsedSeconds,
                StartedAt = i.StartedAtUtc,
                CompletedAt = i.CompletedAtUtc,
                Stations = i.Stations
                    .OrderBy(s => s.Order)
                    .Select(s => new RunStationDto
                    {
                        Id = s.Id,
                        Name = s.Name,
                        Order = s.Order,
                        Items = s.Items
                            .OrderBy(r => r.Order)
                            .Select(r => new RunStationItemDto
                            {
                                Id = r.Id,
                                Kind = r.Kind,
                                DrillId = r.DrillId,
                                Title = r.Title,
                                Order = r.Order,
                                DurationSeconds = r.DurationSeconds,
                                Notes = r.Notes
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList()
    };

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;
}
