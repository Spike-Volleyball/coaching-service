using AutoMapper;
using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.Enums;
using Shared.Exceptions;
using Shared.Options;
using Shared.Services.Analytics;
using Shared.Services.FileStorage.Intefaces;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class DrillImportServiceTests
{
    private IDrillRepository _drillRepository = null!;
    private IClubsGrpcClient _clubsClient = null!;
    private DrillService _sut = null!;

    private static readonly Guid ImporterId = Guid.NewGuid();

    private List<Drill> _persisted = null!;

    [SetUp]
    public void SetUp()
    {
        _drillRepository = Substitute.For<IDrillRepository>();
        _clubsClient = Substitute.For<IClubsGrpcClient>();

        _persisted = [];
        _drillRepository.When(x => x.AddRange(Arg.Any<IEnumerable<Drill>>()))
            .Do(call => _persisted.AddRange(call.Arg<IEnumerable<Drill>>()));

        _sut = new DrillService(
            _drillRepository,
            Substitute.For<IDrillLikeRepository>(),
            Substitute.For<IDrillBookmarkRepository>(),
            Substitute.For<IDrillCommentRepository>(),
            Substitute.For<IDrillAttachmentRepository>(),
            _clubsClient,
            Substitute.For<IFileService>(),
            Options.Create(new S3Settings { Bucket = "test-bucket", PublicBaseUrl = "https://cdn.test" }),
            Substitute.For<IMapper>(),
            Substitute.For<ILogger<DrillService>>(),
            Substitute.For<IDrillDialReconciler>(),
            Substitute.For<IAnalyticsCapture>());
    }

    [Test]
    public async Task ImportAsync_WithValidRows_PersistsEveryRowInOneSave()
    {
        // Arrange
        var request = ImportRequest([Row(1, "Serve receive"), Row(2, "Block footwork"), Row(3, "Pepper")]);

        // Act
        var result = await _sut.ImportAsync(request, ImporterId);

        // Assert
        result.Imported.Should().Be(3);
        result.Failed.Should().Be(0);
        result.Results.Should().OnlyContain(r => r.Error == null && r.DrillId != null);
        result.Results.Select(r => r.RowNumber).Should().Equal(1, 2, 3);

        _persisted.Select(d => d.Name).Should().Equal("Serve receive", "Block footwork", "Pepper");
        _drillRepository.Received(1).AddRange(Arg.Any<IEnumerable<Drill>>());
        await _drillRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task ImportAsync_ReportsTheFailingRowAndStillImportsTheRest()
    {
        // Arrange
        var request = ImportRequest([Row(1, "Serve receive"), Row(2, "   "), Row(3, "Pepper")]);

        // Act
        var result = await _sut.ImportAsync(request, ImporterId);

        // Assert
        result.Imported.Should().Be(2);
        result.Failed.Should().Be(1);

        var failure = result.Results.Single(r => r.RowNumber == 2);
        failure.Error.Should().Be("Name is required");
        failure.DrillId.Should().BeNull();

        _persisted.Select(d => d.Name).Should().Equal("Serve receive", "Pepper");
    }

    [Test]
    public async Task ImportAsync_WhenEveryRowFails_WritesNothing()
    {
        // Arrange
        var request = ImportRequest([Row(1, ""), Row(2, null!)]);

        // Act
        var result = await _sut.ImportAsync(request, ImporterId);

        // Assert
        result.Imported.Should().Be(0);
        result.Failed.Should().Be(2);
        _drillRepository.DidNotReceive().AddRange(Arg.Any<IEnumerable<Drill>>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task ImportAsync_AppliesTheBatchDestinationToEveryRow()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        _clubsClient.IsUserCoachInClubAsync(ImporterId, clubId).Returns(true);
        var request = ImportRequest([Row(1, "One"), Row(2, "Two")], clubId, DrillVisibility.Public);

        // Act
        await _sut.ImportAsync(request, ImporterId);

        // Assert
        _persisted.Should().OnlyContain(d =>
            d.ClubId == clubId &&
            d.Visibility == DrillVisibility.Public &&
            d.CreatedByUserId == ImporterId &&
            d.LikeCount == 0);
    }

    [Test]
    public async Task ImportAsync_ForClub_ChecksCoachPermissionOncePerImportNotPerRow()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        _clubsClient.IsUserCoachInClubAsync(ImporterId, clubId).Returns(true);
        var request = ImportRequest([Row(1, "One"), Row(2, "Two"), Row(3, "Three")], clubId);

        // Act
        await _sut.ImportAsync(request, ImporterId);

        // Assert
        await _clubsClient.Received(1).IsUserCoachInClubAsync(ImporterId, clubId);
    }

    [Test]
    public async Task ImportAsync_ForClub_WhenCallerIsNotCoach_RejectsWithoutWriting()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        _clubsClient.IsUserCoachInClubAsync(ImporterId, clubId).Returns(false);
        var request = ImportRequest([Row(1, "One")], clubId);

        // Act
        var act = () => _sut.ImportAsync(request, ImporterId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _drillRepository.DidNotReceive().AddRange(Arg.Any<IEnumerable<Drill>>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task ImportAsync_ForPersonalLibrary_DoesNotAskAboutClubPermission()
    {
        // Arrange
        var request = ImportRequest([Row(1, "One")]);

        // Act
        await _sut.ImportAsync(request, ImporterId);

        // Assert
        await _clubsClient.DidNotReceive().IsUserCoachInClubAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task ImportAsync_WithMinPlayersAboveMaxPlayers_ReportsTheRow()
    {
        // Arrange
        var request = ImportRequest([Row(1, "Backwards range") with { MinPlayers = 12, MaxPlayers = 6 }]);

        // Act
        var result = await _sut.ImportAsync(request, ImporterId);

        // Assert
        result.Failed.Should().Be(1);
        result.Results.Single().Error.Should().Be("Minimum players cannot exceed maximum players");
    }

    [Test]
    public async Task ImportAsync_WithNegativeDuration_ReportsTheRow()
    {
        // Arrange
        var request = ImportRequest([Row(1, "Negative") with { Duration = -5 }]);

        // Act
        var result = await _sut.ImportAsync(request, ImporterId);

        // Assert
        result.Failed.Should().Be(1);
        result.Results.Single().Error.Should().Be("Duration cannot be negative");
    }

    [Test]
    public async Task ImportAsync_WithNameLongerThanTheColumn_ReportsTheRowRatherThanFailingTheBatch()
    {
        // Arrange — the length ceiling is the database's, and blowing it threw a
        // DbUpdateException out of the single save, losing every good row with it.
        var overlong = new string('a', Drill.NameMaxLength + 1);
        var request = ImportRequest([Row(1, "Good one"), Row(2, overlong), Row(3, "Another good one")]);

        // Act
        var result = await _sut.ImportAsync(request, ImporterId);

        // Assert
        result.Imported.Should().Be(2);
        result.Failed.Should().Be(1);
        result.Results.Single(r => r.RowNumber == 2).Error.Should()
            .Be($"Name is longer than {Drill.NameMaxLength} characters");
        _persisted.Select(d => d.Name).Should().Equal("Good one", "Another good one");
    }

    [Test]
    public async Task ImportAsync_WithNameExactlyTheColumnWidth_Accepts()
    {
        // Arrange
        var exact = new string('a', Drill.NameMaxLength);

        // Act
        var result = await _sut.ImportAsync(ImportRequest([Row(1, exact)]), ImporterId);

        // Assert
        result.Imported.Should().Be(1);
    }

    [Test]
    public async Task ImportAsync_WithVideoUrlLongerThanTheColumn_ReportsTheRow()
    {
        // Arrange
        var row = Row(1, "Has a long link") with { VideoUrl = new string('u', Drill.VideoUrlMaxLength + 1) };

        // Act
        var result = await _sut.ImportAsync(ImportRequest([row]), ImporterId);

        // Assert
        result.Failed.Should().Be(1);
        result.Results.Single().Error.Should().Be($"Video link is longer than {Drill.VideoUrlMaxLength} characters");
    }

    [Test]
    public async Task ImportAsync_WithAVideoLinkThatIsNotHttp_ReportsTheRowAndImportsTheRest()
    {
        // Arrange
        var request = ImportRequest(
        [
            Row(1, "Linked") with { VideoUrl = "https://youtu.be/serve" },
            Row(2, "Hostile") with { VideoUrl = "javascript:alert(1)" },
            Row(3, "Unlinked"),
        ]);

        // Act
        var result = await _sut.ImportAsync(request, ImporterId);

        // Assert
        result.Results.Single(r => r.RowNumber == 2).Error.Should().Be("Video link must start with http:// or https://");
        _persisted.Select(d => d.Name).Should().Equal("Linked", "Unlinked");
    }

    [Test]
    public async Task ImportAsync_WithEquipmentNameLongerThanTheColumn_ReportsTheRow()
    {
        // Arrange
        var row = Row(1, "Has long equipment") with
        {
            Equipment = [new DrillEquipmentInput(new string('e', DrillEquipment.NameMaxLength + 1))]
        };

        // Act
        var result = await _sut.ImportAsync(ImportRequest([row]), ImporterId);

        // Assert
        result.Failed.Should().Be(1);
        result.Results.Single().Error.Should()
            .Be($"Equipment name is longer than {DrillEquipment.NameMaxLength} characters");
    }

    [Test]
    public async Task ImportAsync_TrimsTheName()
    {
        // Arrange
        var request = ImportRequest([Row(1, "  Serve receive  ")]);

        // Act
        await _sut.ImportAsync(request, ImporterId);

        // Assert
        _persisted.Single().Name.Should().Be("Serve receive");
    }

    [Test]
    public async Task ImportAsync_BuildsRichTextFromTheImportedLines()
    {
        // Arrange
        var row = Row(1, "Serve receive") with
        {
            Instructions = ["Split into pairs", "Serve to zone one"],
            CoachingPoints = ["Platform early"]
        };

        // Act
        await _sut.ImportAsync(ImportRequest([row]), ImporterId);

        // Assert
        var drill = _persisted.Single();
        drill.Instructions.Should().Equal("Split into pairs", "Serve to zone one");
        drill.CoachingPoints.Should().Equal("Platform early");
        drill.InstructionsHtml.Should().NotBeNullOrEmpty();
        drill.InstructionsHtml.Should().Contain("Serve to zone one");
        drill.CoachingPointsHtml.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task ImportAsync_AddsEquipmentInSheetOrder()
    {
        // Arrange
        var row = Row(1, "Serve receive") with
        {
            Equipment = [new DrillEquipmentInput("Volleyballs"), new DrillEquipmentInput("Cones", IsOptional: true)]
        };

        // Act
        await _sut.ImportAsync(ImportRequest([row]), ImporterId);

        // Assert
        var drill = _persisted.Single();
        drill.Equipment.Should().SatisfyRespectively(
            first =>
            {
                first.Name.Should().Be("Volleyballs");
                first.IsOptional.Should().BeFalse();
                first.Order.Should().Be(0);
                first.DrillId.Should().Be(drill.Id);
            },
            second =>
            {
                second.Name.Should().Be("Cones");
                second.IsOptional.Should().BeTrue();
                second.Order.Should().Be(1);
                second.DrillId.Should().Be(drill.Id);
            });
    }

    [Test]
    public async Task ImportAsync_WithNoRows_RejectsRequest()
    {
        // Arrange
        var request = ImportRequest([]);

        // Act
        var act = () => _sut.ImportAsync(request, ImporterId);

        // Assert
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be(ErrorCodeEnum.ValidationError);
    }

    [Test]
    public async Task ImportAsync_BeyondTheRowLimit_RejectsRequestWithoutWriting()
    {
        // Arrange
        var rows = Enumerable.Range(1, DrillService.MaxImportRows + 1)
            .Select(i => Row(i, $"Drill {i}"))
            .ToList();

        // Act
        var act = () => _sut.ImportAsync(ImportRequest(rows), ImporterId);

        // Assert
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be(ErrorCodeEnum.ValidationError);
        _drillRepository.DidNotReceive().AddRange(Arg.Any<IEnumerable<Drill>>());
    }

    private static ImportDrillsDto ImportRequest(
        List<ImportDrillRowDto> rows,
        Guid? clubId = null,
        DrillVisibility visibility = DrillVisibility.Private) =>
        new(clubId, visibility, rows);

    private static ImportDrillRowDto Row(int rowNumber, string name) => new(
        RowNumber: rowNumber,
        Name: name,
        Description: "Imported from the club spreadsheet",
        Category: DrillCategory.Technical,
        Intensity: DrillIntensity.Medium,
        Skills: [DrillSkill.Passing],
        Duration: 15,
        MinPlayers: 4,
        MaxPlayers: 12,
        Instructions: ["Step one"],
        CoachingPoints: ["Point one"],
        Equipment: [],
        VideoUrl: null);
}
