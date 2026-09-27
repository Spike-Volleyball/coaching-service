using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Feedback;
using Coaching.Domain.Models.Templates;
using FluentAssertions;
using MockQueryable;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// A dial is one word of a drill's instructions the coach sets per use. The drill owns the word;
/// every plan that uses the drill owns an answer for it. These tests are about the two halves
/// staying in step: a promoted dial reaches the plans that already use the drill, a rename
/// carries their answers with it, and the prose is never stored disagreeing with the dial list.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DrillDialServiceTests : UnitTestBase
{
    private IDrillRepository _drillRepository = null!;
    private IRepository<DrillDial> _dialRepository = null!;
    private IPlanItemRepository _itemRepository = null!;
    private IRepository<PlanStationItem> _stationItemRepository = null!;
    private IRepository<PlanItemDialValue> _valueRepository = null!;
    private IRepository<DrillVariation> _variationRepository = null!;
    private IRepository<ImprovementPointDrill> _pointDrillRepository = null!;
    private IClubsGrpcClient _clubs = null!;
    private DrillDialService _sut = null!;
    private DrillDialReconciler _reconciler = null!;

    private readonly Dictionary<Guid, Drill> _drills = [];
    private readonly List<PlanItem> _spine = [];
    private readonly List<PlanStationItem> _grouped = [];
    private readonly List<PlanItemDialValue> _values = [];
    private readonly List<DrillVariation> _variations = [];
    private readonly List<ImprovementPointDrill> _pointLinks = [];

    private readonly List<PlanItemDialValue> _addedValues = [];
    private readonly List<PlanItemDialValue> _deletedValues = [];
    private readonly List<DrillDial> _addedDials = [];
    private readonly List<DrillDial> _deletedDials = [];
    private readonly List<Drill> _deletedDrills = [];

    private Drill _drill = null!;
    private Guid _userId;
    private Guid _planId;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();

        _drills.Clear();
        _spine.Clear();
        _grouped.Clear();
        _values.Clear();
        _variations.Clear();
        _pointLinks.Clear();
        _addedValues.Clear();
        _deletedValues.Clear();
        _addedDials.Clear();
        _deletedDials.Clear();
        _deletedDrills.Clear();

        _userId = Guid.NewGuid();
        _planId = Guid.NewGuid();
        _drill = NewDrill("Serve receive", "Serve to the setter");

        _drillRepository = Substitute.For<IDrillRepository>();
        _dialRepository = Substitute.For<IRepository<DrillDial>>();
        _itemRepository = Substitute.For<IPlanItemRepository>();
        _stationItemRepository = Substitute.For<IRepository<PlanStationItem>>();
        _valueRepository = Substitute.For<IRepository<PlanItemDialValue>>();
        _variationRepository = Substitute.For<IRepository<DrillVariation>>();
        _pointDrillRepository = Substitute.For<IRepository<ImprovementPointDrill>>();
        _clubs = Substitute.For<IClubsGrpcClient>();

        _drillRepository.GetByIdWithDetailsAsync(Arg.Any<Guid>())
            .Returns(call => _drills.GetValueOrDefault(call.Arg<Guid>()));
        _drillRepository.When(r => r.Delete(Arg.Any<Drill>())).Do(c => _deletedDrills.Add(c.Arg<Drill>()));

        _itemRepository.Query().Returns(_ => _spine.BuildMock());
        _stationItemRepository.Query().Returns(_ => _grouped.BuildMock());
        _valueRepository.Query().Returns(_ => _values.BuildMock());
        _variationRepository.Query().Returns(_ => _variations.BuildMock());
        _pointDrillRepository.Query().Returns(_ => _pointLinks.BuildMock());

        _valueRepository.When(r => r.Add(Arg.Any<PlanItemDialValue>())).Do(c => _addedValues.Add(c.Arg<PlanItemDialValue>()));
        _valueRepository.When(r => r.Delete(Arg.Any<PlanItemDialValue>())).Do(c => _deletedValues.Add(c.Arg<PlanItemDialValue>()));
        _dialRepository.When(r => r.Add(Arg.Any<DrillDial>())).Do(c => _addedDials.Add(c.Arg<DrillDial>()));
        _dialRepository.When(r => r.Delete(Arg.Any<DrillDial>())).Do(c => _deletedDials.Add(c.Arg<DrillDial>()));

        var drillService = Substitute.For<IDrillService>();
        drillService.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(new DrillDto { Name = "Serve receive" });

        _reconciler = new DrillDialReconciler(_dialRepository, _itemRepository, _stationItemRepository, _valueRepository);

        _sut = new DrillDialService(
            _drillRepository,
            _dialRepository,
            _valueRepository,
            _variationRepository,
            _pointDrillRepository,
            _clubs,
            drillService,
            _reconciler);
    }

    // =========================================================================
    // PROMOTING A WORD
    // =========================================================================

    [Test]
    public async Task AddAsync_BacksTheDefaultOntoEveryUseThatDoesNotAlreadyAnswer()
    {
        // Arrange — two plans' worth of uses, one of which already holds an answer under this
        // name from a dial that was removed earlier.
        var settled = SpineUse(_drill.Id);
        var blank = SpineUse(_drill.Id);
        var inGroup = GroupUse(_drill.Id);
        _values.Add(new PlanItemDialValue { PlanId = _planId, ItemId = settled.Id, DialName = "reps", Value = "12" });

        // Act
        await _sut.AddAsync(_drill.Id, Number("reps", "6", Prose("Serve {reps} time~s")), _userId);

        // Assert
        _addedValues.Select(v => v.ItemId ?? v.StationItemId).Should().BeEquivalentTo(new Guid?[] { blank.Id, inGroup.Id });
        _addedValues.Should().OnlyContain(v => v.DialName == "reps" && v.Value == "6" && v.PlanId == _planId);
    }

    [Test]
    public async Task AddAsync_KeysAGroupsAnswerToTheGroupRowAndNotTheSpine()
    {
        // Arrange — a use inside a station group is a use like any other, but it is a different row
        var inGroup = GroupUse(_drill.Id);

        // Act
        await _sut.AddAsync(_drill.Id, Number("reps", "6", Prose("Serve {reps} time~s")), _userId);

        // Assert
        var written = _addedValues.Single();
        written.StationItemId.Should().Be(inGroup.Id);
        written.ItemId.Should().BeNull();
    }

    [Test]
    public async Task AddAsync_StoresTheDialAndTheRetokenizedProseTogether()
    {
        // Act
        await _sut.AddAsync(_drill.Id, Number("reps", "6", Prose("Serve {reps} time~s")), _userId);

        // Assert
        _addedDials.Single().Name.Should().Be("reps");
        _addedDials.Single().Kind.Should().Be(DialKind.Number);
        _drill.Instructions.Should().Equal("Serve {reps} time~s");
        _drill.InstructionsHtml.Should().Contain("{reps}");
    }

    [Test]
    public async Task AddAsync_WhenTheProseNeverMentionsTheNewDial_Throws()
    {
        // Arrange — a splice that missed leaves a control that changes nothing
        var act = () => _sut.AddAsync(_drill.Id, Number("reps", "6", Prose("Serve to the setter")), _userId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _addedDials.Should().BeEmpty();
    }

    [Test]
    public async Task AddAsync_WhenTheProseMentionsATokenNoDialDefines_Throws()
    {
        // Arrange — {tempo} would render as literal braces on the coach's screen
        var act = () => _sut.AddAsync(_drill.Id, Number("reps", "6", Prose("Serve {reps} at {tempo}")), _userId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Test]
    public async Task AddAsync_WhenTheNameIsAlreadyADialOnThisDrill_Throws()
    {
        // Arrange
        GiveDialsAndProse([Dial("reps", DialKind.Number, "6")], "Serve {reps} time~s");

        // Act
        var act = () => _sut.AddAsync(_drill.Id, Number("reps", "8", Prose("Serve {reps} time~s")), _userId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [TestCase("2reps")]
    [TestCase("reps per set")]
    [TestCase("reps_set")]
    public async Task AddAsync_WhenTheNameCannotBeAToken_Throws(string name)
    {
        var act = () => _sut.AddAsync(_drill.Id, Number(name, "6", Prose($"Serve {{{name}}} time~s")), _userId);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Test]
    public async Task AddAsync_WhenAToggleBringsNoSentences_Throws()
    {
        // Arrange — a toggle with nothing to swap between has nothing to say when it is on
        var toggle = new CreateDrillDialDto("net", DialKind.Toggle, "true", Prose("Serve {net} time~s"));

        // Act
        var act = () => _sut.AddAsync(_drill.Id, toggle, _userId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Test]
    public async Task AddAsync_AToggleStoresItsSentencesAndABooleanDefault()
    {
        // Arrange
        var toggle = new CreateDrillDialDto(
            "net", DialKind.Toggle, "YES", Prose("Serve {net} time~s"),
            OnText: "over the net", OffText: "into the wall", OnLabel: "Net", OffLabel: "Wall");

        // Act
        await _sut.AddAsync(_drill.Id, toggle, _userId);

        // Assert
        var stored = _addedDials.Single();
        stored.DefaultValue.Should().Be("false", "only a parseable true is on");
        stored.OnText.Should().Be("over the net");
        stored.OffLabel.Should().Be("Wall");
    }

    [Test]
    public async Task AddAsync_WhenTheCallerDidNotCreateTheDrill_Throws()
    {
        var act = () => _sut.AddAsync(_drill.Id, Number("reps", "6", Prose("Serve {reps} time~s")), Guid.NewGuid());

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task AddAsync_WhenTheDrillBelongsToAClubTheCallerNoLongerCoaches_Throws()
    {
        // Arrange — the creator check alone would let a departed coach keep editing club drills
        _drill.ClubId = Guid.NewGuid();
        _clubs.IsUserCoachInClubAsync(_userId, _drill.ClubId.Value).Returns(false);

        // Act
        var act = () => _sut.AddAsync(_drill.Id, Number("reps", "6", Prose("Serve {reps} time~s")), _userId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    // =========================================================================
    // RENAMING AND REMOVING
    // =========================================================================

    [Test]
    public async Task UpdateAsync_ARenameCarriesEveryRecordedAnswerWithIt()
    {
        // Arrange — two uses, so a rename that only ever reaches the first is caught
        GiveDialsAndProse([Dial("reps", DialKind.Number, "6")], "Serve {reps} time~s");
        var first = SpineUse(_drill.Id);
        var second = GroupUse(_drill.Id);
        _values.Add(new PlanItemDialValue { PlanId = _planId, ItemId = first.Id, DialName = "reps", Value = "12" });
        _values.Add(new PlanItemDialValue { PlanId = _planId, StationItemId = second.Id, DialName = "reps", Value = "4" });

        // Act
        await _sut.UpdateAsync(
            _drill.Id, "reps", new UpdateDrillDialDto(NewName: "count", InstructionsHtml: Prose("Serve {count} time~s")), _userId);

        // Assert
        _values.Should().OnlyContain(v => v.DialName == "count");
        _values.Select(v => v.Value).Should().BeEquivalentTo(new[] { "12", "4" });
        _drill.Dials.Single().Name.Should().Be("count");
    }

    [Test]
    public async Task UpdateAsync_ARenameOntoAStaleAnswerKeepsTheLiveOne()
    {
        // Arrange — the use still holds a "count" left by a dial removed earlier. Two rows cannot
        // share a name on one use, and the dial being renamed is the one that means something.
        GiveDialsAndProse([Dial("reps", DialKind.Number, "6")], "Serve {reps} time~s");
        var use = SpineUse(_drill.Id);
        var live = new PlanItemDialValue { PlanId = _planId, ItemId = use.Id, DialName = "reps", Value = "12" };
        var stale = new PlanItemDialValue { PlanId = _planId, ItemId = use.Id, DialName = "count", Value = "99" };
        _values.Add(live);
        _values.Add(stale);

        // Act
        await _sut.UpdateAsync(
            _drill.Id, "reps", new UpdateDrillDialDto(NewName: "count", InstructionsHtml: Prose("Serve {count} time~s")), _userId);

        // Assert
        stale.Value.Should().Be("12");
        _deletedValues.Should().ContainSingle().Which.Should().BeSameAs(live);
    }

    [Test]
    public async Task UpdateAsync_WhenTheNewNameIsAnotherDialOnTheSameDrill_Throws()
    {
        // Arrange
        GiveDialsAndProse(
            [Dial("reps", DialKind.Number, "6"), Dial("tempo", DialKind.Text, "slow")],
            "Serve {reps} time~s at {tempo}");

        // Act
        var act = () => _sut.UpdateAsync(
            _drill.Id, "reps", new UpdateDrillDialDto(NewName: "tempo", InstructionsHtml: Prose("Serve {tempo} time~s")), _userId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Test]
    public async Task UpdateAsync_WhenARenameBringsNoRetokenizedProse_Throws()
    {
        // Arrange — the old token would be left in the instructions with no dial behind it
        GiveDialsAndProse([Dial("reps", DialKind.Number, "6")], "Serve {reps} time~s");

        // Act
        var act = () => _sut.UpdateAsync(_drill.Id, "reps", new UpdateDrillDialDto(NewName: "count"), _userId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Test]
    public async Task UpdateAsync_ChangingOnlyTheDefaultLeavesTheProseAndTheAnswersAlone()
    {
        // Arrange
        GiveDialsAndProse([Dial("reps", DialKind.Number, "6")], "Serve {reps} time~s");
        var use = SpineUse(_drill.Id);
        _values.Add(new PlanItemDialValue { PlanId = _planId, ItemId = use.Id, DialName = "reps", Value = "12" });

        // Act
        await _sut.UpdateAsync(_drill.Id, "reps", new UpdateDrillDialDto(DefaultValue: "8"), _userId);

        // Assert
        _drill.Dials.Single().DefaultValue.Should().Be("8");
        _values.Single().Value.Should().Be("12", "a plan that has already answered keeps its answer");
        _deletedValues.Should().BeEmpty();
    }

    [Test]
    public async Task UpdateAsync_WhenNoDialGoesByThatName_Throws()
    {
        var act = () => _sut.UpdateAsync(_drill.Id, "reps", new UpdateDrillDialDto(DefaultValue: "8"), _userId);

        await act.Should().ThrowAsync<EntityNotFoundException>();
    }

    [Test]
    public async Task DeleteAsync_TakesTheDialAndEveryAnswerToIt()
    {
        // Arrange — one of the two dials goes; both kinds of use hold an answer to it
        GiveDialsAndProse(
            [Dial("reps", DialKind.Number, "6"), Dial("tempo", DialKind.Text, "slow")],
            "Serve {reps} time~s at {tempo}");
        var onSpine = SpineUse(_drill.Id);
        var inGroup = GroupUse(_drill.Id);
        var goes = new PlanItemDialValue { PlanId = _planId, ItemId = onSpine.Id, DialName = "reps", Value = "12" };
        var alsoGoes = new PlanItemDialValue { PlanId = _planId, StationItemId = inGroup.Id, DialName = "reps", Value = "4" };
        var stays = new PlanItemDialValue { PlanId = _planId, ItemId = onSpine.Id, DialName = "tempo", Value = "fast" };
        _values.AddRange([goes, alsoGoes, stays]);

        // Act
        await _sut.DeleteAsync(_drill.Id, "reps", new DeleteDrillDialDto(Prose("Serve 6 times at {tempo}")), _userId);

        // Assert
        _deletedDials.Single().Name.Should().Be("reps");
        _deletedValues.Should().BeEquivalentTo(new[] { goes, alsoGoes });
        _deletedValues.Should().NotContain(stays);
    }

    [Test]
    public async Task DeleteAsync_WhenTheProseStillMentionsTheDial_Throws()
    {
        // Arrange — the words were never put back, so the token would outlive the dial
        GiveDialsAndProse([Dial("reps", DialKind.Number, "6")], "Serve {reps} time~s");

        // Act
        var act = () => _sut.DeleteAsync(_drill.Id, "reps", new DeleteDrillDialDto(Prose("Serve {reps} time~s")), _userId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _deletedDials.Should().BeEmpty();
    }

    // =========================================================================
    // FOLDING ONE DRILL INTO ANOTHER
    // =========================================================================

    [Test]
    public async Task FoldAsync_MovesEveryUseOntoTheKeeperAndAnswersItsDials()
    {
        // Arrange — the duplicate is used on a plan's spine and inside a group
        var keep = NewDrill("Serve receive", "Serve {reps} time~s");
        GiveDialsAndProseTo(keep, [Dial("reps", DialKind.Number, "6")], "Serve {reps} time~s");
        var onSpine = SpineUse(_drill.Id);
        var inGroup = GroupUse(_drill.Id);

        // Act
        var result = await _sut.FoldAsync(keep.Id, new FoldDrillDto(_drill.Id, new() { ["reps"] = "10" }), _userId);

        // Assert
        result.MovedUses.Should().Be(2);
        onSpine.DrillId.Should().Be(keep.Id);
        inGroup.DrillId.Should().Be(keep.Id);
        _addedValues.Should().HaveCount(2);
        _addedValues.Should().OnlyContain(v => v.DialName == "reps" && v.Value == "10");
        _deletedDrills.Single().Should().BeSameAs(_drill);
    }

    [Test]
    public async Task FoldAsync_LeavesAnAnswerTheUseAlreadyGave()
    {
        // Arrange — this use has already been through the keeper's dial once
        var keep = NewDrill("Serve receive", "Serve {reps} time~s");
        GiveDialsAndProseTo(keep, [Dial("reps", DialKind.Number, "6")], "Serve {reps} time~s");
        var answered = SpineUse(_drill.Id);
        var blank = SpineUse(_drill.Id);
        _values.Add(new PlanItemDialValue { PlanId = _planId, ItemId = answered.Id, DialName = "reps", Value = "3" });

        // Act
        await _sut.FoldAsync(keep.Id, new FoldDrillDto(_drill.Id, new() { ["reps"] = "10" }), _userId);

        // Assert
        _addedValues.Should().ContainSingle().Which.ItemId.Should().Be(blank.Id);
    }

    [Test]
    public async Task FoldAsync_MovesAVariationThatPointedAtTheDuplicate()
    {
        // Arrange — the database refuses to delete a drill another drill lists as a variation
        var keep = NewDrill("Serve receive", "Serve to the setter");
        var elsewhere = Guid.NewGuid();
        var link = new DrillVariation { SourceDrillId = elsewhere, TargetDrillId = _drill.Id };
        _variations.Add(link);

        // Act
        await _sut.FoldAsync(keep.Id, new FoldDrillDto(_drill.Id), _userId);

        // Assert
        link.TargetDrillId.Should().Be(keep.Id);
        _deletedDrills.Single().Should().BeSameAs(_drill);
    }

    [Test]
    public async Task FoldAsync_MovesAnImprovementPointOntoTheKeeper()
    {
        // Arrange — a coach's feedback naming the duplicate should survive the merge
        var keep = NewDrill("Serve receive", "Serve to the setter");
        var link = new ImprovementPointDrill { ImprovementPointId = Guid.NewGuid(), DrillId = _drill.Id };
        _pointLinks.Add(link);

        // Act
        await _sut.FoldAsync(keep.Id, new FoldDrillDto(_drill.Id), _userId);

        // Assert
        link.DrillId.Should().Be(keep.Id);
    }

    [Test]
    public async Task FoldAsync_WhenAskedToFoldADrillIntoItself_Throws()
    {
        var act = () => _sut.FoldAsync(_drill.Id, new FoldDrillDto(_drill.Id), _userId);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Test]
    public async Task FoldAsync_WhenTheCallerDoesNotOwnTheDuplicate_Throws()
    {
        // Arrange — both drills change, so both have to be the caller's to change
        var keep = NewDrill("Serve receive", "Serve to the setter");
        _drill.CreatedByUserId = Guid.NewGuid();

        // Act
        var act = () => _sut.FoldAsync(keep.Id, new FoldDrillDto(_drill.Id), _userId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _deletedDrills.Should().BeEmpty();
    }

    // =========================================================================

    private Drill NewDrill(string name, params string[] lines)
    {
        var drill = new Drill
        {
            Name = name,
            CreatedByUserId = _userId,
            InstructionsHtml = Prose(lines),
            Instructions = lines,
        };
        _drills[drill.Id] = drill;
        return drill;
    }

    private void GiveDialsAndProse(DrillDial[] dials, params string[] lines) =>
        GiveDialsAndProseTo(_drill, dials, lines);

    private static void GiveDialsAndProseTo(Drill drill, DrillDial[] dials, params string[] lines)
    {
        foreach (var dial in dials)
        {
            dial.DrillId = drill.Id;
            drill.Dials.Add(dial);
        }

        drill.InstructionsHtml = Prose(lines);
        drill.Instructions = lines;
    }

    private static DrillDial Dial(string name, DialKind kind, string defaultValue) =>
        new() { Name = name, Kind = kind, DefaultValue = defaultValue };

    private static CreateDrillDialDto Number(string name, string defaultValue, string html) =>
        new(name, DialKind.Number, defaultValue, html);

    private static string Prose(params string[] lines) =>
        "<ol>" + string.Concat(lines.Select(line => $"<li><p>{line}</p></li>")) + "</ol>";

    private PlanItem SpineUse(Guid drillId)
    {
        var item = new PlanItem { TemplateId = _planId, DrillId = drillId };
        _spine.Add(item);
        return item;
    }

    private PlanStationItem GroupUse(Guid drillId)
    {
        var row = new PlanStationItem
        {
            DrillId = drillId,
            Station = new PlanStation { Name = "Setters", Item = new PlanItem { TemplateId = _planId } },
        };
        _grouped.Add(row);
        return row;
    }
}
