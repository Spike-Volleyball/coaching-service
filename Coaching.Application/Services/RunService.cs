using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shared.Exceptions;
using Shared.Services.Analytics;

namespace Coaching.Application.Services;

public class RunService : IRunService
{
    private const int SecondsPerMinute = 60;

    /// <summary>How far back a tap queued on a phone with no signal may say it happened.</summary>
    private static readonly TimeSpan QueuedTapWindow = TimeSpan.FromMinutes(15);

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
        var isCreator = await PlanCreatorIdAsync(eventId) == requestingUserId;
        var canControl = isCreator || await EnsureCanReadRunAsync(eventId, requestingUserId);

        var run = await _runRepository.GetByEventIdWithDetailsAsync(eventId);
        if (run == null)
            return null;

        // A step whose time ran out on a run moving on by itself has been left, whether or not the
        // sweep has written it yet. A read never writes; the sweep, or the next control, does.
        RunAutoAdvance.CatchUp(run, Now());
        return MapToDto(run, canControl);
    }

    /// <summary>
    /// Mirrors TrainingPlanService.GetByEventIdAsync's participant/eventExists check (the sibling
    /// read for the same event-attached plan), extended with the event-admin check for a host who
    /// isn't in the events-service participant roster. Both are asked at once: whether the reader
    /// is an admin is also whether they may control the run.
    /// </summary>
    /// <returns>Whether the reader is an event admin.</returns>
    private async Task<bool> EnsureCanReadRunAsync(Guid eventId, Guid userId)
    {
        var participant = _eventsGrpcClient.IsEventParticipantAsync(eventId, userId);
        var admin = _eventsGrpcClient.IsEventAdminAsync(eventId, userId);
        await Task.WhenAll(participant, admin);

        var (isParticipant, eventExists) = await participant;
        if (!eventExists)
            throw new EntityNotFoundException("Event not found");

        var isAdmin = await admin;
        if (!isParticipant && !isAdmin)
            throw new ForbiddenException("Only event participants, hosts, or the plan creator can view this run");

        return isAdmin;
    }

    public async Task<bool> CanReadRunAsync(Guid eventId, Guid userId) =>
        await PlanCreatorIdAsync(eventId) == userId
        || (await StandingOnEventAsync(eventId, userId)).OnTheEvent;

    public async Task<bool> CanControlRunAsync(Guid eventId, Guid userId) =>
        await CanControlAsync(eventId, userId, await PlanCreatorIdAsync(eventId));

    /// <param name="planCreatorId">The creator of the event's plan, when the caller already has it.</param>
    private async Task<bool> CanControlAsync(Guid eventId, Guid userId, Guid? planCreatorId) =>
        planCreatorId == userId || await _eventsGrpcClient.IsEventAdminAsync(eventId, userId);

    private async Task EnsureCanControlAsync(Guid eventId, Guid userId, Guid? planCreatorId)
    {
        if (!await CanControlAsync(eventId, userId, planCreatorId))
            throw new ForbiddenException("Only the plan creator or an event admin can control the run");
    }

    /// <summary>A participant of any status or a host; a host need not be on the roster.</summary>
    private async Task<(bool EventExists, bool OnTheEvent)> StandingOnEventAsync(Guid eventId, Guid userId)
    {
        var (isParticipant, eventExists) = await _eventsGrpcClient.IsEventParticipantAsync(eventId, userId);
        return (eventExists, eventExists && (isParticipant || await _eventsGrpcClient.IsEventAdminAsync(eventId, userId)));
    }

    public async Task<RunDto> StartAsync(Guid eventId, Guid requestingUserId, bool restart = false, bool? autoAdvance = null)
    {
        var plan = await GetInstancePlanOrThrowAsync(eventId);
        await EnsureCanControlAsync(eventId, requestingUserId, plan.CreatedByUserId);

        var run = await _runRepository.GetByEventIdWithDetailsAsync(eventId);

        // A second phone's Start, or a Start Over tapped on a stale screen, put a live session back
        // to step 1 (09-28). Starting over a session in progress is now said out loud; a finished
        // one still starts over without it, which is what the builds in the field send.
        if (run is { Status: RunStatus.Running or RunStatus.Paused } && !restart)
            throw new ConflictException("This session is already running. Start it over to begin again from the first step.");

        var isRestart = run != null;
        var now = Now();
        var orderedItems = plan.Items.OrderBy(i => i.Order).ToList();
        var firstItem = orderedItems.FirstOrDefault();

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

        // Unsaid, a new run starts without it and a run started over keeps it: the builds in the
        // field know nothing of auto-advance, and their Start Over must not switch it off.
        if (autoAdvance is { } moveOnByItself)
            run.AutoAdvance = moveOnByItself;

        // A restart is a start: the coach is running the practice again, and the run rows were
        // just reset to the plan as it stands now.
        return await CommitAsync(eventId, run, movedOn: [], () =>
            _analytics.Capture(requestingUserId, AnalyticsEventNames.PracticeRunStarted, new Dictionary<string, object?>
            {
                ["event_id"] = eventId,
                ["plan_id"] = plan.Id,
                ["item_count"] = run.Items.Count,
                ["is_restart"] = isRestart
            }));
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

    public async Task<RunDto> PauseAsync(Guid eventId, Guid requestingUserId, DateTimeOffset? occurredAt = null)
    {
        var (run, movedOn) = await LoadForControlAsync(eventId, requestingUserId);
        if (run.Status != RunStatus.Running)
            return MapToDto(run, canControl: true);

        run.CurrentItemPausedElapsedSeconds = RunSteps.ElapsedSeconds(run, TapTime(run, movedOn, occurredAt));
        run.CurrentItemStartedAtUtc = null;
        run.Status = RunStatus.Paused;

        return await CommitAsync(eventId, run, movedOn);
    }

    public async Task<RunDto> ResumeAsync(Guid eventId, Guid requestingUserId, DateTimeOffset? occurredAt = null)
    {
        var (run, movedOn) = await LoadForControlAsync(eventId, requestingUserId);
        if (run.Status != RunStatus.Paused)
            return MapToDto(run, canControl: true);

        var at = TapTime(run, movedOn, occurredAt);
        run.CurrentItemStartedAtUtc = at.AddSeconds(-run.CurrentItemPausedElapsedSeconds);
        run.Status = RunStatus.Running;

        return await CommitAsync(eventId, run, [.. movedOn, .. RunAutoAdvance.Arm(run, at)]);
    }

    public async Task<RunDto> AdvanceAsync(Guid eventId, Guid fromItemId, Guid requestingUserId, DateTimeOffset? occurredAt = null)
    {
        var (run, movedOn) = await LoadForControlAsync(eventId, requestingUserId);
        var steps = RunSteps.InOrder(run);
        var from = steps.FindIndex(s => s.PlanItemId == fromItemId);

        // A double tap, or one queued on a screen that has since moved on — by hand, or by itself.
        if (run.CurrentItemId != fromItemId || from < 0)
            return MapToDto(run, canControl: true);

        var at = TapTime(run, movedOn, occurredAt);
        var played = RunSteps.ElapsedSeconds(run, at);
        var left = steps[from];
        RunSteps.Finish(left, played, at);

        var next = from + 1 < steps.Count ? steps[from + 1] : null;
        if (next == null)
            RunSteps.End(run, at);
        else
            RunSteps.Enter(run, next, at);

        // Advancing off the end of the plan is the run finishing, and that is the same fact the
        // finish button records — so it is the same event, once, from whichever path got there.
        return await CommitAsync(eventId, run, movedOn, () =>
        {
            if (next == null)
                _analytics.CapturePracticeRunCompleted(run, requestingUserId);
            else
                _analytics.CapturePracticeRunStepChanged(run, left, next, RunStepDirection.Next, played, requestingUserId);
        });
    }

    public async Task<RunDto> GoToAsync(Guid eventId, Guid fromItemId, Guid toItemId, Guid requestingUserId, DateTimeOffset? occurredAt = null)
    {
        var (run, movedOn) = await LoadForControlAsync(eventId, requestingUserId);
        var steps = RunSteps.InOrder(run);
        var from = steps.FindIndex(s => s.PlanItemId == fromItemId);
        var to = steps.FindIndex(s => s.PlanItemId == toItemId);

        // A stale tap, as for advance — and a target that is no step of this run, or the step
        // already current, has nowhere to go.
        if (run.CurrentItemId != fromItemId || from < 0 || to < 0 || to == from)
            return MapToDto(run, canControl: true);

        var at = TapTime(run, movedOn, occurredAt);
        var played = RunSteps.ElapsedSeconds(run, at);
        var (left, target) = (steps[from], steps[to]);
        if (to < from)
            RunSteps.Discard(left);
        else
            RunSteps.Finish(left, played, at);
        RunSteps.Enter(run, target, at);

        return await CommitAsync(eventId, run, movedOn, () =>
            _analytics.CapturePracticeRunStepChanged(run, left, target, Direction(from, to), played, requestingUserId));
    }

    public async Task<RunDto> CompleteAsync(Guid eventId, Guid requestingUserId, DateTimeOffset? occurredAt = null)
    {
        var (run, movedOn) = await LoadForControlAsync(eventId, requestingUserId);
        if (run.Status == RunStatus.Completed)
            return MapToDto(run, canControl: true);

        var at = TapTime(run, movedOn, occurredAt);
        if (run.Items.FirstOrDefault(s => s.PlanItemId == run.CurrentItemId) is { } current)
            RunSteps.Finish(current, RunSteps.ElapsedSeconds(run, at), at);
        RunSteps.End(run, at);

        return await CommitAsync(eventId, run, movedOn, () => _analytics.CapturePracticeRunCompleted(run, requestingUserId));
    }

    public async Task<RunDto> ReopenAsync(Guid eventId, Guid requestingUserId, DateTimeOffset? occurredAt = null)
    {
        var (run, movedOn) = await LoadForControlAsync(eventId, requestingUserId);
        if (run.Status != RunStatus.Completed)
            return MapToDto(run, canControl: true);

        // The step that was current when the run ended ended with it. A run ended before one clock
        // read stamped both has only its last started step to go by.
        var steps = RunSteps.InOrder(run);
        var resumed = steps.FindLast(s => s.CompletedAtUtc is { } ended && ended == run.CompletedAtUtc)
            ?? steps.FindLast(s => s.StartedAtUtc != null);
        if (resumed == null)
            return MapToDto(run, canControl: true);

        var at = TapTime(run, movedOn, occurredAt);
        run.CompletedAtUtc = null;
        RunSteps.Enter(run, resumed, at);

        return await CommitAsync(eventId, run, movedOn);
    }

    public async Task<RunDto> SetAutoAdvanceAsync(Guid eventId, bool enabled, Guid requestingUserId)
    {
        var (run, movedOn) = await LoadForControlAsync(eventId, requestingUserId);
        if (run.AutoAdvance == enabled)
            return MapToDto(run, canControl: true);

        run.AutoAdvance = enabled;
        return await CommitAsync(eventId, run, [.. movedOn, .. RunAutoAdvance.Arm(run, Now())]);
    }

    public async Task<IReadOnlyList<Guid>> GetRunIdsDueToAutoAdvanceAsync(CancellationToken cancellationToken) =>
        await _runRepository.GetIdsDueToAutoAdvanceAsync(Now(), cancellationToken);

    public async Task AutoAdvanceAsync(Guid runId)
    {
        var run = await _runRepository.GetWithDetailsAsync(runId);
        if (run == null)
            return;

        // Due when the sweep asked, but a coach may have paused it, switched it off or moved it on since.
        var movedOn = RunAutoAdvance.CatchUp(run, Now());
        if (movedOn.Count == 0)
            return;

        try
        {
            await _runRepository.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // A coach's write landed between the read and this one, and it stands: it saved the run
            // caught up as of its own tap. The next sweep reads the run again.
            return;
        }

        CaptureMovedOn(run, movedOn);
        await _broadcaster.BroadcastRunUpdatedAsync(run.EventId, MapToDto(run, canControl: true));
    }

    /// <summary>A move to the neighbouring step either way, or past it, by position in the run.</summary>
    private static string Direction(int from, int to) => (to - from) switch
    {
        1 => RunStepDirection.Next,
        -1 => RunStepDirection.Previous,
        _ => RunStepDirection.Jump,
    };

    /// <summary>
    /// When a control was tapped, for the clock math. A tap queued offline says when it was made,
    /// and is believed back to the run's last change — nothing can have happened before that — but
    /// no further than <see cref="QueuedTapWindow"/>, and never ahead of now. The last change is the
    /// run's last save, or a step it has just been moved through by itself (<paramref name="movedOn"/>)
    /// that no save has recorded yet.
    /// </summary>
    private DateTime TapTime(TrainingPlanRun run, IReadOnlyList<AutoAdvancedStep> movedOn, DateTimeOffset? occurredAt)
    {
        var now = Now();
        if (occurredAt is not { } tapped)
            return now;

        var lastChange = movedOn.Select(step => (DateTime?)step.At).Append(run.UpdatedAt).Max();
        var windowStart = now - QueuedTapWindow;
        var earliest = lastChange > windowStart ? lastChange.Value : windowStart;
        var at = tapped.UtcDateTime < earliest ? earliest : tapped.UtcDateTime;
        return at > now ? now : at;
    }

    /// <summary>
    /// The run, for a caller who may control it. The rule is asked before the run is read, so a
    /// caller who may not control it is refused alike whether or not a session was started. It
    /// comes as it stands now: a run moving on by itself is first moved past every step whose time
    /// ran out (<c>MovedOn</c>), so a tap is judged against the step the run is on, and those moves
    /// are saved with the control's own write, if it makes one.
    /// </summary>
    private async Task<(TrainingPlanRun Run, IReadOnlyList<AutoAdvancedStep> MovedOn)> LoadForControlAsync(
        Guid eventId, Guid requestingUserId)
    {
        await EnsureCanControlAsync(eventId, requestingUserId, await PlanCreatorIdAsync(eventId));

        var run = await _runRepository.GetByEventIdWithDetailsAsync(eventId)
            ?? throw new EntityNotFoundException("No run has been started for this event");
        return (run, RunAutoAdvance.CatchUp(run, Now()));
    }

    private IQueryable<TrainingPlan> InstancePlanQuery(Guid eventId) =>
        _planRepository.Query().Where(p => p.EventId == eventId && p.PlanType == PlanType.Instance && !p.IsDeleted);

    private Task<Guid?> PlanCreatorIdAsync(Guid eventId) =>
        InstancePlanQuery(eventId).Select(p => (Guid?)p.CreatedByUserId).FirstOrDefaultAsync();

    private async Task<TrainingPlan> GetInstancePlanOrThrowAsync(Guid eventId)
    {
        var plan = await InstancePlanQuery(eventId)
            .Include(p => p.Items)
                .ThenInclude(i => i.Stations)
                    .ThenInclude(s => s.Items)
            // A single chain: its rows are the plan's leaves, a sum rather than a product.
            .AsSingleQuery()
            .FirstOrDefaultAsync()
            ?? throw new EntityNotFoundException("No training plan is attached to this event");

        return plan;
    }

    /// <summary>
    /// The one save of a control write, then word of it to every device watching the run; the
    /// controller who made it is answered with the same. Losing a race to another phone is not an
    /// error: the write that landed first stands, nothing of this one is kept, and the caller is
    /// told what the run is now — the answer a stale tap gets. <paramref name="saved"/> records
    /// what only a write that landed may.
    /// </summary>
    /// <param name="movedOn">
    /// The steps the run moved through by itself before and as the control acted, saved with it.
    /// The run is caught up again before the save, since the write itself can leave it due — a tap
    /// queued offline acts minutes back, and the step it entered then may have run out since — and
    /// no reply or broadcast may show a step the run has left.
    /// </param>
    private async Task<RunDto> CommitAsync(
        Guid eventId, TrainingPlanRun run, IReadOnlyList<AutoAdvancedStep> movedOn, Action? saved = null)
    {
        var movedOnAfter = RunAutoAdvance.CatchUp(run, Now());

        try
        {
            await _runRepository.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException
            || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            var current = await _runRepository.GetByEventIdWithDetailsNoTrackingAsync(eventId)
                ?? throw new EntityNotFoundException("No run has been started for this event");
            RunAutoAdvance.CatchUp(current, Now());
            return MapToDto(current, canControl: true);
        }

        CaptureMovedOn(run, movedOn);
        saved?.Invoke();
        CaptureMovedOn(run, movedOnAfter);

        var dto = MapToDto(run, canControl: true);
        await _broadcaster.BroadcastRunUpdatedAsync(eventId, dto);
        return dto;
    }

    /// <summary>
    /// Counts each step the run moved through by itself, once it is saved. Nobody tapped, so each
    /// is counted against whoever started the session.
    /// </summary>
    private void CaptureMovedOn(TrainingPlanRun run, IReadOnlyList<AutoAdvancedStep> movedOn)
    {
        foreach (var step in movedOn)
        {
            _analytics.CapturePracticeRunStepChanged(
                run, step.From, step.To, RunStepDirection.Auto, step.PlayedSeconds, run.StartedByUserId);
        }
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
        AutoAdvance = run.AutoAdvance,
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
