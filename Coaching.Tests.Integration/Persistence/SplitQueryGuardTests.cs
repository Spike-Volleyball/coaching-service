using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shared.Models;

namespace Coaching.Tests.Integration.Persistence;

/// <summary>
/// A query that loads two collections in one statement returns their cartesian product, so
/// outside production the context refuses it. The test host replaces the context's options and
/// still carries the refusal from Startup, because EF applies every configuration registered for
/// the context, not only the last one.
/// </summary>
[TestFixture]
[Category("Integration")]
public class SplitQueryGuardTests
{
    private const int ChildrenPerCollection = 3;

    private CoachingApiFactory _factory = null!;
    private WebApplicationFactory<Program> _production = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        // This host only hands out contexts. Its hosted services would bind the metrics port the
        // default host already holds.
        _production = _factory.WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .ConfigureTestServices(s => s.RemoveAll<IHostedService>()));

        // Each host migrates as it starts, which fails once a reset has emptied the history.
        _ = _factory.Services;
        _ = _production.Services;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _production.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [TearDown]
    public async Task TearDown() => await _factory.DatabaseResetter.ResetAsync();

    [Test]
    public async Task Query_TwoCollectionsInOneStatement_IsRefused()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        // Act
        var act = () => TwoCollectionsInOneStatement(db).ToListAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{nameof(RelationalEventId.MultipleCollectionIncludeWarning)}*");
    }

    [Test]
    public async Task Query_TwoCollectionsInOneStatement_InProduction_Runs()
    {
        // Arrange
        using var scope = _production.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        // Act
        var act = () => TwoCollectionsInOneStatement(db).ToListAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task GetByIdWithDetailsAsync_DrillWithEveryCollection_LoadsEachWhole()
    {
        // Arrange
        var drillId = await SeedDrillWithEveryCollectionAsync();
        using var scope = _factory.Services.CreateScope();
        var drills = scope.ServiceProvider.GetRequiredService<IDrillRepository>();

        // Act
        var drill = await drills.GetByIdWithDetailsAsync(drillId);

        // Assert
        drill!.Attachments.Select(a => a.Order).Should().Equal(0, 1, 2);
        drill.Equipment.Select(e => e.Order).Should().Equal(0, 1, 2);
        drill.Dials.Select(d => d.Order).Should().Equal(0, 1, 2);
    }

    private static IQueryable<Drill> TwoCollectionsInOneStatement(CoachingDbContext db) =>
        db.Drills.Include(d => d.Attachments).Include(d => d.Equipment);

    private async Task<Guid> SeedDrillWithEveryCollectionAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        var creator = new UserProfile { Id = Guid.NewGuid(), Name = "Split", Surname = "Guard", Email = "split.guard@test.local", IsActive = true };
        var drill = new Drill
        {
            Name = "Split guard",
            CreatedByUserId = creator.Id,
            Visibility = DrillVisibility.Private,
            Skills = [],
            Instructions = [],
            CoachingPoints = []
        };
        // Reversed, so an order that came from insertion rather than from the query would show.
        for (var i = ChildrenPerCollection - 1; i >= 0; i--)
        {
            drill.Attachments.Add(new DrillAttachment
            {
                FileName = $"sheet-{i}.pdf", FileUrl = $"https://cdn.test/sheet-{i}.pdf", FileType = DrillAttachmentType.Document, Order = i
            });
            drill.Equipment.Add(new DrillEquipment { Name = $"Item {i}", Order = i });
            drill.Dials.Add(new DrillDial { Name = $"dial{i}", Kind = DialKind.Number, DefaultValue = "1", Order = i });
        }

        db.Add(creator);
        db.Drills.Add(drill);
        await db.SaveChangesAsync();
        return drill.Id;
    }
}
