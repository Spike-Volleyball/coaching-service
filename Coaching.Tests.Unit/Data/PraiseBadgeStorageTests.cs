using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using Coaching.Infrastructure.Data.Context;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Coaching.Tests.Unit.Data;

/// <summary>
/// A praise's badge is stored as the enum's number, so adding a badge needs no migration, and
/// renumbering one would silently turn every stored praise into a different badge.
/// </summary>
[TestFixture]
[Category("Unit")]
public class PraiseBadgeStorageTests
{
    [Test]
    public void BadgeType_KeepsTheNumberEveryBadgeIsStoredAs()
    {
        // Act
        var numbers = Enum.GetValues<BadgeType>().ToDictionary(b => b.ToString(), b => (int)b);

        // Assert
        numbers.Should().Equal(new Dictionary<string, int>
        {
            ["Star"] = 0,
            ["Improvement"] = 1,
            ["Teamwork"] = 2,
            ["Effort"] = 3,
            ["Skill"] = 4,
            ["Leadership"] = 5,
            ["Consistency"] = 6,
            ["Breakthrough"] = 7,
            ["Hustle"] = 8,
            ["GameIq"] = 9,
            ["LoudAndClear"] = 10,
            ["FairPlay"] = 11,
            ["Clutch"] = 12,
            ["GoodEnergy"] = 13,
            ["BraveCall"] = 14,
            ["Coachable"] = 15,
        });
    }

    [Test]
    public void PraiseBadge_IsStoredAsItsNumber()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<CoachingDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only")
            .Options;
        using var context = new CoachingDbContext(options);

        // Act
        var column = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(Praise))!
            .FindProperty(nameof(Praise.BadgeType))!;

        // Assert
        column.GetProviderClrType().Should().Be(typeof(int));
        column.IsNullable.Should().BeTrue();
    }
}
