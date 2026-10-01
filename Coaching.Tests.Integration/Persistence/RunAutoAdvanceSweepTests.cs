using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Services;
using Coaching.BackgroundServices;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shared.Models;

namespace Coaching.Tests.Integration.Persistence;

/// <summary>
/// The auto-advance sweep against a real Postgres. Which runs are due is asked in the database,
/// so an idle sweep reads no run; a due one is moved on, saved, and told to both rooms. Plans of
/// a 5-minute step and a 10-minute one unless a test says otherwise.
/// </summary>
[TestFixture]
[Category("Integration")]
public class RunAutoAdvanceSweepTests
{
    private static readonly Guid CreatorId = Guid.NewGuid();
    private static readonly TimeSpan Precision = TimeSpan.FromMilliseconds(1);

    private CoachingApiFactory _factory = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _factory.DisposeAsync();

    [SetUp]
    public async Task SetUp()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.Set<UserProfile>().Add(new UserProfile
        {
            Id = CreatorId,
            Name = "Coach",
            Surname = "Creator",
            Email = "coach.creator@test.com"
        });
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _factory.DatabaseResetter.ResetAsync();
        _factory.RunBroadcaster.ClearReceivedCalls();
    }

    [Test]
    public void TheService_RunsTheSweep()
    {
        // Assert — the test host takes it out so no sweep races a test; the service must have it
        _factory.RegistersTheAutoAdvanceSweep.Should().BeTrue();
    }

    [Test]
    public async Task TheDueRuns_AreOnlyThoseOnAStepWhoseTimeIsUpWithAStepAfterIt()
    {
        // Arrange
        var due = await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromSeconds(310));
        await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromSeconds(290));
        await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromSeconds(310), autoAdvance: false);
        await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromSeconds(310), status: RunStatus.Paused);
        await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromSeconds(310), status: RunStatus.Completed);
        await SeedRunAsync(currentStep: 1, elapsed: TimeSpan.FromMinutes(20));
        await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromHours(1), minutes: [0, 10]);

        // Act
        IReadOnlyList<Guid> ids;
        using (var scope = _factory.Services.CreateScope())
            ids = await scope.ServiceProvider.GetRequiredService<IRunService>().GetRunIdsDueToAutoAdvanceAsync(CancellationToken.None);

        // Assert — not yet due, by hand, paused, finished, on the last step, on a step with no time: none
        ids.Should().Equal(due.Run.Id);
    }

    [Test]
    public async Task ASweep_SavesADueRunOnTheNextStepFromWhenItsTimeRanOut()
    {
        // Arrange — ten seconds past the first step's five minutes.
        var (run, steps) = await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromSeconds(310));
        var ranOut = run.CurrentItemStartedAtUtc!.Value.AddSeconds(300);

        // Act
        await BuildSweep().SweepAsync(CancellationToken.None);

        // Assert
        var saved = await LoadAsync(run.Id);
        saved.CurrentItemId.Should().Be(steps[1]);
        saved.CurrentItemStartedAtUtc.Should().BeCloseTo(ranOut, Precision);

        var first = saved.Items.Single(i => i.PlanItemId == steps[0]);
        first.ActualElapsedSeconds.Should().Be(300);
        first.CompletedAtUtc.Should().BeCloseTo(ranOut, Precision);
        saved.Items.Single(i => i.PlanItemId == steps[1]).StartedAtUtc.Should().BeCloseTo(ranOut, Precision);
    }

    [Test]
    public async Task ASweep_TellsBothRoomsWhereTheRunIsNow()
    {
        // Arrange
        var (run, steps) = await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromSeconds(310));

        // Act
        await BuildSweep().SweepAsync(CancellationToken.None);

        // Assert — the broadcaster sends controllers and viewers each their own copy
        await _factory.RunBroadcaster.Received(1).BroadcastRunUpdatedAsync(
            run.EventId, Arg.Is<RunDto>(d => d.Id == run.Id && d.CurrentItemId == steps[1] && d.AutoAdvance));
    }

    [Test]
    public async Task ASweep_LeavesARunNotYetDueAsItWas()
    {
        // Arrange
        var (run, steps) = await SeedRunAsync(currentStep: 0, elapsed: TimeSpan.FromSeconds(290));

        // Act
        await BuildSweep().SweepAsync(CancellationToken.None);

        // Assert
        (await LoadAsync(run.Id)).CurrentItemId.Should().Be(steps[0]);
        await _factory.RunBroadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
    }

    /// <summary>Built from the host's own container, as it runs in the service.</summary>
    private RunAutoAdvanceService BuildSweep() =>
        ActivatorUtilities.CreateInstance<RunAutoAdvanceService>(_factory.Services);

    private async Task<TrainingPlanRun> LoadAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        return await db.TrainingPlanRuns.AsNoTracking().Include(r => r.Items).SingleAsync(r => r.Id == runId);
    }

    /// <summary>
    /// An event plan of steps of <paramref name="minutes"/> each, and a run of it on the step at
    /// <paramref name="currentStep"/>, entered <paramref name="elapsed"/> ago.
    /// </summary>
    /// <returns>The run, and its steps' plan item ids in order.</returns>
    private async Task<(TrainingPlanRun Run, List<Guid> Steps)> SeedRunAsync(
        int currentStep,
        TimeSpan elapsed,
        bool autoAdvance = true,
        RunStatus status = RunStatus.Running,
        int[]? minutes = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        var plan = new TrainingPlan
        {
            Name = "Instance Plan",
            CreatedByUserId = CreatorId,
            PlanType = PlanType.Instance,
            EventId = Guid.NewGuid(),
            Visibility = TemplateVisibility.Private,
            Items = (minutes ?? [5, 10])
                .Select((duration, i) => new PlanItem { Kind = ItemKind.Break, Title = $"Step {i + 1}", Order = i + 1, Duration = duration })
                .ToList()
        };
        db.TrainingPlans.Add(plan);

        var items = plan.Items
            .OrderBy(i => i.Order)
            .Select(i => new TrainingPlanRunItem
            {
                PlanItemId = i.Id,
                Kind = i.Kind,
                Title = i.Title,
                Order = i.Order,
                PlannedDurationSeconds = i.Duration * 60
            })
            .ToList();
        var entered = DateTime.UtcNow - elapsed;
        items[currentStep].StartedAtUtc = entered;

        var run = new TrainingPlanRun
        {
            PlanId = plan.Id,
            EventId = plan.EventId!.Value,
            StartedByUserId = CreatorId,
            StartedAtUtc = entered,
            AutoAdvance = autoAdvance,
            Status = status,
            CurrentItemId = status == RunStatus.Completed ? null : items[currentStep].PlanItemId,
            CurrentItemStartedAtUtc = status == RunStatus.Running ? entered : null,
            CurrentItemPausedElapsedSeconds = status == RunStatus.Paused ? (int)elapsed.TotalSeconds : 0,
            CompletedAtUtc = status == RunStatus.Completed ? DateTime.UtcNow : null,
            Items = items
        };
        db.TrainingPlanRuns.Add(run);

        await db.SaveChangesAsync();
        return (run, items.Select(i => i.PlanItemId).ToList());
    }
}
