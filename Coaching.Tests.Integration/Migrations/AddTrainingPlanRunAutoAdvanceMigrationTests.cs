using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Configurations;
using Coaching.Infrastructure.Data.Context;
using Coaching.Infrastructure.Migrations;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shared.Models;

namespace Coaching.Tests.Integration.Migrations;

/// <summary>
/// Auto-advance arrives on runs started without it. A session already under way when the migration
/// runs goes on by hand, and the sweep's index holds only the runs going on their own. Steps the
/// migration down and up over this fixture's own database, reading it in plain SQL, since the
/// entities only know the schema the code is at.
/// </summary>
[TestFixture]
[Category("Integration")]
[NonParallelizable] // steps the schema down and back up
public class AddTrainingPlanRunAutoAdvanceMigrationTests
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
    public async Task Up_ARunUnderWayWhenItRuns_GoesOnByHand()
    {
        // Arrange — seeded moving on by itself, so only a column made afresh can read false
        var runId = await SeedRunningRunAsync(autoAdvance: true);
        await MigrateAsync(up: false);

        try
        {
            // Act
            await MigrateAsync(up: true);

            // Assert
            var autoAdvance = await ReadAsync(
                $"""SELECT "AutoAdvance" FROM "TrainingPlanRuns" WHERE "Id" = '{runId}'""",
                r => r.GetBoolean(0));
            autoAdvance.Should().Equal(false);
        }
        finally
        {
            await EnsureUpAsync();
        }
    }

    [Test]
    public async Task Up_TheSweepsIndex_HoldsOnlyRunsGoingOnTheirOwn()
    {
        // Act
        var definitions = await ReadAsync(
            $"""SELECT indexdef FROM pg_indexes WHERE indexname = '{TrainingPlanRunConfiguration.AutoAdvancingIndex}'""",
            r => r.GetString(0));

        // Assert
        definitions.Should().ContainSingle()
            .Which.Should().EndWith($"""WHERE ("AutoAdvance" AND ("Status" = {(int)RunStatus.Running}))""");
    }

    [Test]
    public async Task Down_DropsTheSettingAndItsIndex()
    {
        try
        {
            // Act
            await MigrateAsync(up: false);

            // Assert
            (await AutoAdvanceColumnAsync()).Should().BeEmpty();
            (await ReadAsync(
                $"""SELECT indexname FROM pg_indexes WHERE indexname = '{TrainingPlanRunConfiguration.AutoAdvancingIndex}'""",
                r => r.GetString(0))).Should().BeEmpty();
        }
        finally
        {
            await EnsureUpAsync();
        }
    }

    private async Task EnsureUpAsync()
    {
        if ((await AutoAdvanceColumnAsync()).Count == 0)
            await MigrateAsync(up: true);
    }

    private Task<List<string>> AutoAdvanceColumnAsync() =>
        ReadAsync(
            """SELECT column_name FROM information_schema.columns WHERE table_name = 'TrainingPlanRuns' AND column_name = 'AutoAdvance'""",
            r => r.GetString(0));

    private async Task MigrateAsync(bool up)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        var assembly = db.GetService<IMigrationsAssembly>();
        var id = assembly.Migrations.Keys.Single(k => k.EndsWith(nameof(AddTrainingPlanRunAutoAdvance)));
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.GetService<IDatabaseProvider>().Name);
        var commands = db.GetService<IMigrationsSqlGenerator>()
            .Generate(up ? migration.UpOperations : migration.DownOperations, db.Model);

        // One transaction, so a step that fails leaves the schema as it was rather than half-moved.
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var command in commands)
        {
            await using var sql = new NpgsqlCommand(command.CommandText, connection, transaction);
            await sql.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private async Task<List<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> row)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync()) rows.Add(row(reader));
        return rows;
    }

    /// <summary>An event plan of one break, and a run of it under way. Written while the schema is up.</summary>
    private async Task<Guid> SeedRunningRunAsync(bool autoAdvance)
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

        var plan = new TrainingPlan
        {
            Name = "Instance Plan",
            CreatedByUserId = CreatorId,
            PlanType = PlanType.Instance,
            EventId = Guid.NewGuid(),
            Visibility = TemplateVisibility.Private,
            Items = [new PlanItem { Kind = ItemKind.Break, Title = "Warm-up", Order = 1, Duration = 5 }]
        };
        db.TrainingPlans.Add(plan);

        var now = DateTime.UtcNow;
        var run = new TrainingPlanRun
        {
            PlanId = plan.Id,
            EventId = plan.EventId!.Value,
            StartedByUserId = CreatorId,
            Status = RunStatus.Running,
            StartedAtUtc = now,
            CurrentItemId = plan.Items.First().Id,
            CurrentItemStartedAtUtc = now,
            AutoAdvance = autoAdvance,
        };
        db.TrainingPlanRuns.Add(run);

        await db.SaveChangesAsync();
        return run.Id;
    }
}
