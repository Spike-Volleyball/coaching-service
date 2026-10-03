using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.RichText;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using FluentAssertions;
using MockQueryable;
using NSubstitute;
using Shared.Exceptions;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// SPI-6502: drills imported before the import kept a cell's structure hold it flattened — dashes
/// as text inside numbered steps. The admin rebuild gives those drills the structure an import
/// gives now, but only where the prose is still exactly what the flat import made of its lines: a
/// drill its author has reworked is theirs, and it is never touched.
/// </summary>
[TestFixture]
[Category("Unit")]
public class ImportedProseRebuilderTests : UnitTestBase
{
    private static readonly Guid ImporterId = Guid.NewGuid();

    private static readonly string[] FlatSteps = ["Split into 2 teams", "Variations:", "- Line", "- Sharp cross"];
    private const string StructuredSteps =
        "<ol><li><p>Split into 2 teams</p></li>" +
        "<li><p>Variations:</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul></li></ol>";

    private List<Drill> _drills = null!;
    private IDrillRepository _drillRepository = null!;
    private ImportedProseRebuilder _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _drills = [];
        _drillRepository = Substitute.For<IDrillRepository>();
        _drillRepository.Query().Returns(_ => _drills.BuildMock());
        _sut = new ImportedProseRebuilder(_drillRepository);
    }

    [Test]
    public async Task RebuildAsync_ByDefault_OnlyReportsWhatARebuildWouldChange()
    {
        // Arrange
        var drill = ImportedFlat("Dot Shots", FlatSteps, ["- Eyes on the hitter"]);

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId));

        // Assert
        result.Applied.Should().BeFalse();
        var reported = result.Drills.Should().ContainSingle().Subject;
        reported.DrillId.Should().Be(drill.Id);
        reported.Name.Should().Be("Dot Shots");
        reported.Instructions.Should().Be(new ProseChangeDto(DrillRichText.FromLines(FlatSteps, ordered: true), StructuredSteps));
        reported.CoachingPoints!.After.Should().Be("<ul><li><p>Eyes on the hitter</p></li></ul>");

        drill.InstructionsHtml.Should().Be(DrillRichText.FromLines(FlatSteps, ordered: true));
        drill.Instructions.Should().Equal(FlatSteps);
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task RebuildAsync_WhenApplied_RewritesBothColumnsOfEachFieldAndSavesOnce()
    {
        // Arrange
        var first = ImportedFlat("Dot Shots", FlatSteps, ["- Eyes on the hitter", "- Stay low"]);
        var second = ImportedFlat("Pepper", ["- Pass", "- Set", "- Hit"], []);

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId, Apply: true));

        // Assert
        result.Applied.Should().BeTrue();
        result.Drills.Select(d => d.DrillId).Should().Equal(first.Id, second.Id);

        first.InstructionsHtml.Should().Be(StructuredSteps);
        first.Instructions.Should().Equal("Split into 2 teams", "Variations:", "Line", "Sharp cross");
        first.CoachingPointsHtml.Should().Be("<ul><li><p>Eyes on the hitter</p></li><li><p>Stay low</p></li></ul>");
        first.CoachingPoints.Should().Equal("Eyes on the hitter", "Stay low");
        second.InstructionsHtml.Should().Be("<ul><li><p>Pass</p></li><li><p>Set</p></li><li><p>Hit</p></li></ul>");
        await _drillRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task RebuildAsync_NeverTouchesProseItsAuthorHasReworked()
    {
        // Arrange — "Sheet Defense", rebuilt by hand in the editor after the import.
        const string reworked =
            "<p>Two blockers hold the sheet.</p><ul><li><p>Variations:</p><ul><li><p>hold it higher</p></li></ul></li></ul>";
        var drill = ImportedFlat("Sheet Defense", ["- hold it higher"], []);
        drill.InstructionsHtml = reworked;
        drill.Instructions = ["Two blockers hold the sheet.", "Variations:", "hold it higher"];

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId, Apply: true));

        // Assert
        result.Drills.Should().BeEmpty();
        drill.InstructionsHtml.Should().Be(reworked);
    }

    [Test]
    public async Task RebuildAsync_NeverTouchesADrillItsAuthorHasGivenDials()
    {
        // Arrange — a dial is an edit, whatever the prose still looks like.
        var drill = ImportedFlat("Serve {reps}", ["- Serve {reps} times"], []);
        drill.Dials.Add(new DrillDial { DrillId = drill.Id, Name = "reps", Kind = DialKind.Number });

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId, Apply: true));

        // Assert
        result.Drills.Should().BeEmpty();
        drill.Instructions.Should().Equal("- Serve {reps} times");
    }

    [Test]
    public async Task RebuildAsync_LeavesOtherCoachesDrillsAlone()
    {
        // Arrange
        var someoneElses = ImportedFlat("Dot Shots", FlatSteps, []);
        someoneElses.CreatedByUserId = Guid.NewGuid();

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId, Apply: true));

        // Assert
        result.Drills.Should().BeEmpty();
        someoneElses.Instructions.Should().Equal(FlatSteps);
    }

    [Test]
    public async Task RebuildAsync_SkipsADrillTheRulesGiveBackUnchanged()
    {
        // Arrange — plain steps come out numbered, as they went in.
        ImportedFlat("Butterfly", ["Toss", "Pass", "Follow your pass"], []);

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId, Apply: true));

        // Assert
        result.Drills.Should().BeEmpty();
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task RebuildAsync_DoesNotCountASpellingOfTheSameCharacterAsAChange()
    {
        // Arrange — the flat import stored &#39; where the sanitizer writes an apostrophe.
        ImportedFlat("Butterfly", ["Don't reach", "Call \"mine\""], []);

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId));

        // Assert
        result.Drills.Should().BeEmpty();
    }

    [Test]
    public async Task RebuildAsync_InTheBulletsStyle_ListsPlainStepsAsBullets()
    {
        // Arrange
        var drill = ImportedFlat("Butterfly", ["Toss", "Pass"], []);

        // Act
        await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId, DirectionsStyle.Bullets, Apply: true));

        // Assert
        drill.InstructionsHtml.Should().Be("<ul><li><p>Toss</p></li><li><p>Pass</p></li></ul>");
    }

    [Test]
    public async Task RebuildAsync_RebuildsCoachingPointsAloneWhenOnlyTheyAreAsImported()
    {
        // Arrange — the author rewrote the steps, not the points.
        const string reworked = "<p>Serve, then chase it down.</p>";
        var drill = ImportedFlat("Chase", ["- Serve"], ["- Stay low", "- Talk"]);
        drill.InstructionsHtml = reworked;
        drill.Instructions = ["Serve, then chase it down."];

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId, Apply: true));

        // Assert
        var reported = result.Drills.Should().ContainSingle().Subject;
        reported.Instructions.Should().BeNull();
        reported.CoachingPoints!.After.Should().Be("<ul><li><p>Stay low</p></li><li><p>Talk</p></li></ul>");
        drill.InstructionsHtml.Should().Be(reworked);
        drill.CoachingPoints.Should().Equal("Stay low", "Talk");
    }

    [Test]
    public async Task RebuildAsync_NamingDrills_RebuildsOnlyThose()
    {
        // Arrange — the drills a dry run showed, and nothing it did not.
        var named = ImportedFlat("Dot Shots", FlatSteps, []);
        var unnamed = ImportedFlat("Pepper", ["- Pass", "- Set"], []);

        // Act
        var result = await _sut.RebuildAsync(new RebuildImportedProseDto(ImporterId, Apply: true, DrillIds: [named.Id]));

        // Assert
        result.Drills.Select(d => d.DrillId).Should().Equal(named.Id);
        unnamed.Instructions.Should().Equal("- Pass", "- Set");
    }

    [Test]
    public async Task RebuildAsync_WithoutAUser_IsRejected()
    {
        // Act
        var act = () => _sut.RebuildAsync(new RebuildImportedProseDto(Guid.Empty));

        // Assert
        (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be(Shared.Enums.ErrorCodeEnum.ValidationError);
    }

    /// <summary>A drill exactly as the import before SPI-6502 stored it: the lines, and one flat list of them.</summary>
    private Drill ImportedFlat(string name, string[] instructions, string[] coachingPoints)
    {
        var drill = new Drill
        {
            Name = name,
            CreatedByUserId = ImporterId,
            Visibility = DrillVisibility.Private,
            Instructions = instructions,
            InstructionsHtml = DrillRichText.FromLines(instructions, ordered: true),
            CoachingPoints = coachingPoints,
            CoachingPointsHtml = DrillRichText.FromLines(coachingPoints, ordered: false),
            CreatedAt = Now.AddMinutes(_drills.Count)
        };
        _drills.Add(drill);
        return drill;
    }
}
