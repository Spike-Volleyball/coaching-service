using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Coaching.Tests.Unit.Data;

/// <summary>
/// Two phones control one run. A write made from a read the other phone has since overtaken must
/// not land over it, so the run row carries Postgres's own row version as a concurrency token.
/// </summary>
[TestFixture]
[Category("Unit")]
public class TrainingPlanRunModelTests
{
    private IEntityType _run = null!;

    [OneTimeSetUp]
    public void BuildModel()
    {
        var options = new DbContextOptionsBuilder<CoachingDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only")
            .Options;

        using var context = new CoachingDbContext(options);
        _run = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TrainingPlanRun))!;
    }

    [Test]
    public void ARunIsSavedOnlyOverTheVersionItWasReadAt()
    {
        // Act
        var version = _run.FindProperty(nameof(TrainingPlanRun.RowVersion))!;

        // Assert
        version.IsConcurrencyToken.Should().BeTrue();
        version.ValueGenerated.Should().Be(ValueGenerated.OnAddOrUpdate);
    }

    [Test]
    public void TheVersionIsPostgresOwnRowVersion()
    {
        // Act — xmin is a system column: nothing to add to the table, nothing to maintain.
        var version = _run.FindProperty(nameof(TrainingPlanRun.RowVersion))!;

        // Assert
        version.GetColumnName().Should().Be("xmin");
        version.GetColumnType().Should().Be("xid");
    }
}
