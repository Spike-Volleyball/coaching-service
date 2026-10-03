using AutoMapper;
using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Tests.Unit.Analytics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MockQueryable;
using NSubstitute;
using Shared.Exceptions;
using Shared.Options;
using Shared.Services.Analytics;
using Shared.Services.FileStorage.Intefaces;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// SPI-6282: the drill library's server-side events. Every one of them belongs after the save
/// that made the fact true, so the refusals below must record nothing at all.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DrillAnalyticsTests
{
    private IDrillRepository _drillRepository = null!;
    private IDrillLikeRepository _likeRepository = null!;
    private IDrillBookmarkRepository _bookmarkRepository = null!;
    private IDrillDialReconciler _dialReconciler = null!;
    private IClubsGrpcClient _clubsClient = null!;
    private IAnalyticsCapture _analytics = null!;
    private DrillService _sut = null!;

    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _drillRepository = Substitute.For<IDrillRepository>();
        _likeRepository = Substitute.For<IDrillLikeRepository>();
        _bookmarkRepository = Substitute.For<IDrillBookmarkRepository>();
        _dialReconciler = Substitute.For<IDrillDialReconciler>();
        _clubsClient = Substitute.For<IClubsGrpcClient>();
        _analytics = Substitute.For<IAnalyticsCapture>();

        var mapper = Substitute.For<IMapper>();
        mapper.Map<DrillDto>(Arg.Any<Drill>()).Returns(call => ToDto(call.Arg<Drill>()));

        _drillRepository.Query().Returns(new List<Drill>().BuildMock());
        _clubsClient.IsUserCoachInClubAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(true);

        _sut = new DrillService(
            _drillRepository,
            _likeRepository,
            _bookmarkRepository,
            Substitute.For<IDrillCommentRepository>(),
            Substitute.For<IDrillAttachmentRepository>(),
            _clubsClient,
            Substitute.For<IFileService>(),
            Options.Create(new S3Settings { Bucket = "test-bucket", PublicBaseUrl = "https://cdn.test" }),
            mapper,
            Substitute.For<ILogger<DrillService>>(),
            _dialReconciler,
            _analytics,
            Substitute.For<IDrillReadGrants>());
    }

    [Test]
    public async Task CreateAsync_WithADrillThatSaves_CapturesDrillCreatedOnce()
    {
        // Arrange
        StubCreatedDrill();
        AddOneDialOnReconcile();

        // Act
        var created = await _sut.CreateAsync(CreateRequest(clubId: ClubId), CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.DrillCreated, CoachId);
        properties["drill_id"].Should().Be(created.Id);
        properties["club_id"].Should().Be(ClubId);
        properties["visibility"].Should().Be(DrillVisibility.Public);
        properties["category"].Should().Be(DrillCategory.Technical);
        properties["has_video"].Should().Be(true);
        properties["dial_count"].Should().Be(1);
    }

    [Test]
    public async Task CreateAsync_WithNoName_CapturesNothing()
    {
        // Arrange
        var request = CreateRequest() with { Name = "  " };

        // Act
        var act = () => _sut.CreateAsync(request, CoachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task ImportAsync_WithOneBadRow_CapturesOneEventCarryingBothCounts()
    {
        // Arrange
        var request = new ImportDrillsDto(ClubId, DrillVisibility.Private,
            [ImportRow(1, "Serve receive"), ImportRow(2, "Block footwork"), ImportRow(3, "")]);

        // Act
        await _sut.ImportAsync(request, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.DrillImported, CoachId);
        properties["row_count"].Should().Be(3);
        properties["imported_count"].Should().Be(2);
        properties["failed_count"].Should().Be(1);
        properties["club_id"].Should().Be(ClubId);
        properties["visibility"].Should().Be(DrillVisibility.Private);
    }

    [Test]
    public async Task ImportAsync_WithMoreRowsThanTheCeiling_CapturesNothing()
    {
        // Arrange
        var rows = Enumerable.Range(1, DrillService.MaxImportRows + 1)
            .Select(number => ImportRow(number, $"Drill {number}"))
            .ToList();

        // Act
        var act = () => _sut.ImportAsync(new ImportDrillsDto(null, DrillVisibility.Private, rows), CoachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task UpdateAsync_WhenTheDrillSaves_CapturesDrillUpdatedOnce()
    {
        // Arrange
        var drill = ExistingDrill(CoachId);
        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);
        AddOneDialOnReconcile();

        // Act
        await _sut.UpdateAsync(UpdateRequest(drill.Id, ClubId), CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.DrillUpdated, CoachId);
        properties["drill_id"].Should().Be(drill.Id);
        properties["club_id"].Should().Be(ClubId);
        properties["visibility"].Should().Be(DrillVisibility.Public);
        properties["dial_count"].Should().Be(1);
    }

    [Test]
    public async Task UpdateAsync_WhenTheCallerDidNotWriteTheDrill_CapturesNothing()
    {
        // Arrange
        var drill = ExistingDrill(Guid.NewGuid());
        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);

        // Act
        var act = () => _sut.UpdateAsync(UpdateRequest(drill.Id, null), CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task LikeDrillAsync_WhenTheLikeIsNew_CapturesDrillSavedOn()
    {
        // Arrange
        var drill = ExistingDrill(Guid.NewGuid());
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);
        _likeRepository.GetByDrillAndUserAsync(drill.Id, CoachId).Returns((DrillLike?)null);

        // Act
        await _sut.LikeDrillAsync(drill.Id, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.DrillSaved, CoachId);
        properties["drill_id"].Should().Be(drill.Id);
        properties["kind"].Should().Be(DrillSaveKind.Like);
        properties["is_on"].Should().Be(true);
    }

    [Test]
    public async Task LikeDrillAsync_WhenTheDrillIsAlreadyLiked_CapturesNothing()
    {
        // Arrange
        var drill = ExistingDrill(Guid.NewGuid());
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);
        _likeRepository.GetByDrillAndUserAsync(drill.Id, CoachId)
            .Returns(new DrillLike { DrillId = drill.Id, UserId = CoachId });

        // Act
        await _sut.LikeDrillAsync(drill.Id, CoachId);

        // Assert
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task UnlikeDrillAsync_WhenTheLikeExists_CapturesDrillSavedOff()
    {
        // Arrange
        var drill = ExistingDrill(Guid.NewGuid());
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);
        _likeRepository.GetByDrillAndUserAsync(drill.Id, CoachId)
            .Returns(new DrillLike { DrillId = drill.Id, UserId = CoachId });

        // Act
        await _sut.UnlikeDrillAsync(drill.Id, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.DrillSaved, CoachId);
        properties["kind"].Should().Be(DrillSaveKind.Like);
        properties["is_on"].Should().Be(false);
    }

    [Test]
    public async Task BookmarkDrillAsync_WhenTheBookmarkIsNew_CapturesDrillSavedOn()
    {
        // Arrange
        var drill = ExistingDrill(Guid.NewGuid());
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);
        _bookmarkRepository.GetByDrillAndUserAsync(drill.Id, CoachId).Returns((DrillBookmark?)null);

        // Act
        await _sut.BookmarkDrillAsync(drill.Id, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.DrillSaved, CoachId);
        properties["drill_id"].Should().Be(drill.Id);
        properties["kind"].Should().Be(DrillSaveKind.Bookmark);
        properties["is_on"].Should().Be(true);
    }

    [Test]
    public async Task UnbookmarkDrillAsync_WhenThereIsNoBookmark_CapturesNothing()
    {
        // Arrange
        var drillId = Guid.NewGuid();
        _bookmarkRepository.GetByDrillAndUserAsync(drillId, CoachId).Returns((DrillBookmark?)null);

        // Act
        await _sut.UnbookmarkDrillAsync(drillId, CoachId);

        // Assert
        _analytics.CapturedNothing();
    }

    private void StubCreatedDrill()
    {
        Drill? persisted = null;
        _drillRepository.When(repository => repository.Add(Arg.Any<Drill>()))
            .Do(call => persisted = call.Arg<Drill>());
        _drillRepository.GetByIdWithDetailsAsync(Arg.Any<Guid>()).Returns(_ => persisted);
    }

    private void AddOneDialOnReconcile() =>
        _dialReconciler.When(reconciler =>
                reconciler.ReconcileAsync(Arg.Any<Drill>(), Arg.Any<IReadOnlyList<DrillDialInputDto>>()))
            .Do(call =>
            {
                var drill = call.Arg<Drill>();
                drill.Dials.Add(new DrillDial { DrillId = drill.Id, Name = "reps", Kind = DialKind.Number });
            });

    private static Drill ExistingDrill(Guid createdBy) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Serve receive to target",
        CreatedByUserId = createdBy,
        Visibility = DrillVisibility.Private
    };

    private static CreateDrillDto CreateRequest(Guid? clubId = null) => new(
        Name: "Serve receive to target",
        Description: "Three lines, passer to target",
        Category: DrillCategory.Technical,
        Intensity: DrillIntensity.Medium,
        Visibility: DrillVisibility.Public,
        Skills: [DrillSkill.Passing],
        Duration: 20,
        MinPlayers: 4,
        MaxPlayers: 12,
        Instructions: ["Split into pairs"],
        CoachingPoints: ["Platform early"],
        Variations: [],
        Equipment: [],
        VideoUrl: "https://example.test/serve-receive",
        ClubId: clubId,
        Dials: [new DrillDialInputDto(null, "reps", DialKind.Number, "6")]);

    private static UpdateDrillDto UpdateRequest(Guid drillId, Guid? clubId) => new(
        Id: drillId,
        Name: "Serve receive to target",
        Description: "Three lines, passer to target",
        Category: DrillCategory.Technical,
        Intensity: DrillIntensity.Medium,
        Visibility: DrillVisibility.Public,
        Skills: [DrillSkill.Passing],
        Duration: 20,
        MinPlayers: 4,
        MaxPlayers: 12,
        Instructions: ["Split into pairs"],
        CoachingPoints: ["Platform early"],
        Variations: [],
        Equipment: [],
        VideoUrl: "https://example.test/serve-receive",
        ClubId: clubId,
        Dials: [new DrillDialInputDto(null, "reps", DialKind.Number, "6")]);

    private static ImportDrillRowDto ImportRow(int rowNumber, string name) => new(
        RowNumber: rowNumber,
        Name: name,
        Description: null,
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

    private static DrillDto ToDto(Drill drill) => new()
    {
        Id = drill.Id,
        Name = drill.Name,
        Category = drill.Category,
        Intensity = drill.Intensity,
        Visibility = drill.Visibility,
        VideoUrl = drill.VideoUrl,
        ClubId = drill.ClubId,
        CreatedByUserId = drill.CreatedByUserId,
        Dials = drill.Dials.Select(dial => new DrillDialDto
        {
            Id = dial.Id,
            Name = dial.Name,
            Kind = dial.Kind,
            DefaultValue = dial.DefaultValue,
            Order = dial.Order
        }).ToList()
    };
}
