using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.Models;

namespace Coaching.Tests.Integration.Persistence;

/// <summary>
/// The run row's concurrency token, against a real Postgres. Two phones control one run: a save
/// made from a read that another write has since overtaken is refused rather than landing over
/// it, and the run is then read as the other write left it.
/// </summary>
[TestFixture]
[Category("Integration")]
public class RunRowVersionTests
{
    private static readonly Guid CreatorId = Guid.NewGuid();

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

    [TearDown]
    public async Task TearDown() => await _factory.DatabaseResetter.ResetAsync();

    [Test]
    public async Task ASaveFromAReadAnotherWriteOvertook_IsRefused()
    {
        // Arrange
        var eventId = await SeedRunAsync();
        using var mine = _factory.Services.CreateScope();
        var runs = mine.ServiceProvider.GetRequiredService<ITrainingPlanRunRepository>();
        var run = (await runs.GetByEventIdWithDetailsAsync(eventId))!;
        await PauseFromAnotherPhoneAsync(eventId);

        // Act
        run.Status = RunStatus.Completed;
        var act = () => runs.SaveChangesAsync();

        // Assert
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Test]
    public async Task AfterARefusedSave_TheUntrackedReadIsTheRunTheOtherWriteLeft()
    {
        // Arrange — the refused write is still staged on this request's context.
        var eventId = await SeedRunAsync();
        using var mine = _factory.Services.CreateScope();
        var runs = mine.ServiceProvider.GetRequiredService<ITrainingPlanRunRepository>();
        var run = (await runs.GetByEventIdWithDetailsAsync(eventId))!;
        await PauseFromAnotherPhoneAsync(eventId);
        run.Status = RunStatus.Completed;
        run.Items.First().CompletedAtUtc = DateTime.UtcNow;
        var refused = () => runs.SaveChangesAsync();
        await refused.Should().ThrowAsync<DbUpdateConcurrencyException>();

        // Act
        var current = await runs.GetByEventIdWithDetailsNoTrackingAsync(eventId);

        // Assert
        current!.Status.Should().Be(RunStatus.Paused);
        current.Items.Should().HaveCount(2).And.OnlyContain(i => i.CompletedAtUtc == null);
    }

    [Test]
    public async Task SavesInARowFromOneRead_EachGoThrough()
    {
        // Arrange — each save reads the new version back, so a request that saves twice is not
        // mistaken for one that lost a race.
        var eventId = await SeedRunAsync();
        using var mine = _factory.Services.CreateScope();
        var runs = mine.ServiceProvider.GetRequiredService<ITrainingPlanRunRepository>();
        var run = (await runs.GetByEventIdWithDetailsAsync(eventId))!;
        run.Status = RunStatus.Paused;
        await runs.SaveChangesAsync();

        // Act
        run.Status = RunStatus.Running;
        var act = () => runs.SaveChangesAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    private async Task PauseFromAnotherPhoneAsync(Guid eventId)
    {
        using var theirs = _factory.Services.CreateScope();
        var runs = theirs.ServiceProvider.GetRequiredService<ITrainingPlanRunRepository>();
        var run = (await runs.GetByEventIdWithDetailsAsync(eventId))!;
        run.Status = RunStatus.Paused;
        await runs.SaveChangesAsync();
    }

    /// <summary>An event plan of two breaks, and a run of it on the first.</summary>
    private async Task<Guid> SeedRunAsync()
    {
        var eventId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        db.Set<UserProfile>().Add(new UserProfile
        {
            Id = CreatorId,
            Name = "Coach",
            Surname = "Creator",
            Email = "coach.creator@test.com"
        });

        var plan = new TrainingPlan
        {
            Name = "Instance Plan",
            CreatedByUserId = CreatorId,
            PlanType = PlanType.Instance,
            EventId = eventId,
            Visibility = TemplateVisibility.Private,
            Items =
            [
                new PlanItem { Kind = ItemKind.Break, Title = "Warm-up", Order = 1, Duration = 5 },
                new PlanItem { Kind = ItemKind.Break, Title = "Water", Order = 2, Duration = 5 }
            ]
        };
        db.TrainingPlans.Add(plan);

        var now = DateTime.UtcNow;
        db.TrainingPlanRuns.Add(new TrainingPlanRun
        {
            PlanId = plan.Id,
            EventId = eventId,
            StartedByUserId = CreatorId,
            Status = RunStatus.Running,
            StartedAtUtc = now,
            CurrentItemId = plan.Items.First().Id,
            CurrentItemStartedAtUtc = now,
            Items = plan.Items
                .Select(i => new TrainingPlanRunItem
                {
                    PlanItemId = i.Id,
                    Kind = i.Kind,
                    Title = i.Title,
                    Order = i.Order,
                    PlannedDurationSeconds = i.Duration * 60
                })
                .ToList()
        });

        await db.SaveChangesAsync();
        return eventId;
    }
}
