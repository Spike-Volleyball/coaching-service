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
using MockQueryable;
using NSubstitute;
using Shared.Enums;
using Shared.Exceptions;
using Shared.Options;
using Shared.Services.Analytics;
using Shared.Services.FileStorage.Intefaces;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class DrillCrudServiceTests
{
    private IDrillRepository _drillRepository = null!;
    private IDrillAttachmentRepository _attachmentRepository = null!;
    private IClubsGrpcClient _clubsClient = null!;
    private IMapper _mapper = null!;
    private DrillService _sut = null!;

    private static readonly Guid CreatorId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _drillRepository = Substitute.For<IDrillRepository>();
        _attachmentRepository = Substitute.For<IDrillAttachmentRepository>();
        _clubsClient = Substitute.For<IClubsGrpcClient>();
        _mapper = Substitute.For<IMapper>();
        _mapper.Map<DrillDto>(Arg.Any<Drill>()).Returns(call => ToDto(call.Arg<Drill>()));

        _sut = new DrillService(
            _drillRepository,
            Substitute.For<IDrillLikeRepository>(),
            Substitute.For<IDrillBookmarkRepository>(),
            Substitute.For<IDrillCommentRepository>(),
            _attachmentRepository,
            _clubsClient,
            Substitute.For<IFileService>(),
            Options.Create(new S3Settings { Bucket = "test-bucket", PublicBaseUrl = "https://cdn.test" }),
            _mapper,
            Substitute.For<ILogger<DrillService>>(),
            Substitute.For<IDrillDialReconciler>(),
            Substitute.For<IAnalyticsCapture>());
    }

    [Test]
    public async Task CreateAsync_WithCompleteRequest_AddsAndReturnsPersistedDrill()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        _clubsClient.IsUserCoachInClubAsync(CreatorId, clubId).Returns(true);
        var variationOne = NewDrill("Variation one");
        var variationTwo = NewDrill("Variation two");
        _drillRepository.Query().Returns(new[] { variationOne, variationTwo }.BuildMock());

        Drill? persisted = null;
        _drillRepository.When(x => x.Add(Arg.Any<Drill>()))
            .Do(call => persisted = call.Arg<Drill>());
        _drillRepository.GetByIdWithDetailsAsync(Arg.Any<Guid>())
            .Returns(_ => persisted);

        var request = CompleteCreateRequest(
            clubId,
            [
                new CreateDrillVariationInput(variationOne.Id, "Make it easier"),
                new CreateDrillVariationInput(variationTwo.Id, "Make it harder")
            ]);

        // Act
        var result = await _sut.CreateAsync(request, CreatorId);

        // Assert
        persisted.Should().NotBeNull();
        persisted!.Name.Should().Be(request.Name);
        persisted.Description.Should().Be(request.Description);
        persisted.Category.Should().Be(request.Category);
        persisted.Intensity.Should().Be(request.Intensity);
        persisted.Visibility.Should().Be(request.Visibility);
        persisted.Skills.Should().Equal(request.Skills);
        persisted.Duration.Should().Be(request.Duration);
        persisted.MinPlayers.Should().Be(request.MinPlayers);
        persisted.MaxPlayers.Should().Be(request.MaxPlayers);
        persisted.Instructions.Should().Equal(request.Instructions);
        persisted.CoachingPoints.Should().Equal(request.CoachingPoints);
        persisted.VideoUrl.Should().Be(request.VideoUrl);
        persisted.ClubId.Should().Be(clubId);
        persisted.CreatedByUserId.Should().Be(CreatorId);
        persisted.LikeCount.Should().Be(0);

        persisted.Equipment.Should().SatisfyRespectively(
            first =>
            {
                first.Name.Should().Be("Volleyballs");
                first.IsOptional.Should().BeFalse();
                first.Order.Should().Be(0);
                first.DrillId.Should().Be(persisted.Id);
            },
            second =>
            {
                second.Name.Should().Be("Targets");
                second.IsOptional.Should().BeTrue();
                second.Order.Should().Be(1);
                second.DrillId.Should().Be(persisted.Id);
            });

        persisted.Variations.Should().SatisfyRespectively(
            first =>
            {
                first.TargetDrillId.Should().Be(variationOne.Id);
                first.Note.Should().Be("Make it easier");
                first.Order.Should().Be(0);
                first.SourceDrillId.Should().Be(persisted.Id);
            },
            second =>
            {
                second.TargetDrillId.Should().Be(variationTwo.Id);
                second.Note.Should().Be("Make it harder");
                second.Order.Should().Be(1);
                second.SourceDrillId.Should().Be(persisted.Id);
            });

        result.Id.Should().Be(persisted.Id);
        result.Name.Should().Be(request.Name);
        _drillRepository.Received(1).Add(persisted);
        await _drillRepository.Received(1).SaveChangesAsync();
        await _drillRepository.Received(1).GetByIdWithDetailsAsync(persisted.Id);
        await _clubsClient.Received(1).IsUserCoachInClubAsync(CreatorId, clubId);
    }

    [Test]
    public async Task CreateAsync_ForClub_WhenCallerIsNotCoach_RejectsWithoutWriting()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        _clubsClient.IsUserCoachInClubAsync(CreatorId, clubId).Returns(false);
        var request = CompleteCreateRequest(clubId);

        // Act
        var act = () => _sut.CreateAsync(request, CreatorId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _drillRepository.DidNotReceive().Add(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task CreateAsync_WithoutName_RejectsRequestWithoutWriting(string? name)
    {
        // Arrange
        var request = CompleteCreateRequest() with { Name = name! };

        // Act
        var act = () => _sut.CreateAsync(request, CreatorId);

        // Assert
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be(ErrorCodeEnum.ValidationError);
        _drillRepository.DidNotReceive().Add(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task CreateAsync_WhenVariationDoesNotExist_RejectsRequestWithoutWriting()
    {
        // Arrange
        var missingDrillId = Guid.NewGuid();
        _drillRepository.Query().Returns(Array.Empty<Drill>().BuildMock());
        var request = CompleteCreateRequest(variations:
            [new CreateDrillVariationInput(missingDrillId, null)]);

        // Act
        var act = () => _sut.CreateAsync(request, CreatorId);

        // Assert
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be(ErrorCodeEnum.EntityNotFound);
        exception.Which.Message.Should().Contain(missingDrillId.ToString());
        _drillRepository.DidNotReceive().Add(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task CreateAsync_WithAJavascriptVideoUrl_RejectsNamingItWithoutWriting()
    {
        // Arrange
        var request = CompleteCreateRequest() with { VideoUrl = "javascript:alert(document.cookie)" };

        // Act
        var act = () => _sut.CreateAsync(request, CreatorId);

        // Assert
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.FieldErrors.Select(e => e.Field).Should().Equal("videoUrl");
        _drillRepository.DidNotReceive().Add(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task CreateAsync_WithoutAVideoUrl_StillCreates(string? videoUrl)
    {
        // Arrange
        Drill? persisted = null;
        _drillRepository.When(x => x.Add(Arg.Any<Drill>())).Do(call => persisted = call.Arg<Drill>());
        _drillRepository.GetByIdWithDetailsAsync(Arg.Any<Guid>()).Returns(_ => persisted);
        var request = CompleteCreateRequest() with { VideoUrl = videoUrl };

        // Act
        await _sut.CreateAsync(request, CreatorId);

        // Assert
        persisted.Should().NotBeNull();
        await _drillRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_WithCompleteRequest_ReplacesMutableStateAndPreservesIdentityAndOwnership()
    {
        // Arrange
        var originalCreatedAt = DateTime.UtcNow.AddDays(-5);
        var oldVariation = NewDrill("Old variation");
        var newVariation = NewDrill("New variation");
        var drill = NewDrill("Original", CreatorId);
        drill.CreatedAt = originalCreatedAt;
        drill.LikeCount = 42;
        drill.Equipment.Add(new DrillEquipment
        {
            DrillId = drill.Id,
            Name = "Old equipment",
            IsOptional = false,
            Order = 0
        });
        drill.Variations.Add(new DrillVariation
        {
            SourceDrillId = drill.Id,
            TargetDrillId = oldVariation.Id,
            Note = "Old note",
            Order = 0
        });

        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);
        _drillRepository.Query().Returns(new[] { newVariation }.BuildMock());
        var targetClubId = Guid.NewGuid();
        _clubsClient.IsUserCoachInClubAsync(CreatorId, targetClubId).Returns(true);
        var request = CompleteUpdateRequest(
            drill.Id,
            targetClubId,
            [new CreateDrillVariationInput(newVariation.Id, "Replacement")]);
        var beforeUpdate = DateTime.UtcNow;

        // Act
        var result = await _sut.UpdateAsync(request, CreatorId);

        // Assert
        drill.Id.Should().Be(request.Id);
        drill.CreatedByUserId.Should().Be(CreatorId);
        drill.CreatedAt.Should().Be(originalCreatedAt);
        drill.LikeCount.Should().Be(42);
        drill.Name.Should().Be(request.Name);
        drill.Description.Should().Be(request.Description);
        drill.Category.Should().Be(request.Category);
        drill.Intensity.Should().Be(request.Intensity);
        drill.Visibility.Should().Be(request.Visibility);
        drill.Skills.Should().Equal(request.Skills);
        drill.Duration.Should().Be(request.Duration);
        drill.MinPlayers.Should().Be(request.MinPlayers);
        drill.MaxPlayers.Should().Be(request.MaxPlayers);
        drill.Instructions.Should().Equal(request.Instructions);
        drill.CoachingPoints.Should().Equal(request.CoachingPoints);
        drill.VideoUrl.Should().Be(request.VideoUrl);
        drill.ClubId.Should().Be(request.ClubId);
        drill.UpdatedAt.Should().BeOnOrAfter(beforeUpdate);

        drill.Equipment.Should().SatisfyRespectively(
            first =>
            {
                first.Name.Should().Be("Updated net");
                first.IsOptional.Should().BeFalse();
                first.Order.Should().Be(0);
            },
            second =>
            {
                second.Name.Should().Be("Updated cones");
                second.IsOptional.Should().BeTrue();
                second.Order.Should().Be(1);
            });
        drill.Equipment.Should().NotContain(e => e.Name == "Old equipment");

        drill.Variations.Should().ContainSingle();
        drill.Variations.Single().TargetDrillId.Should().Be(newVariation.Id);
        drill.Variations.Single().Note.Should().Be("Replacement");
        drill.Variations.Single().Order.Should().Be(0);
        drill.Variations.Should().NotContain(v => v.TargetDrillId == oldVariation.Id);

        result.Id.Should().Be(drill.Id);
        result.Name.Should().Be(request.Name);
        await _drillRepository.Received(1).SaveChangesAsync();
        await _clubsClient.Received(1).IsUserCoachInClubAsync(CreatorId, targetClubId);
    }

    [Test]
    public async Task UpdateAsync_ClubDrill_WhenCreatorIsNotCoach_RejectsWithoutChangingDrill()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        var drill = NewDrill("Original", CreatorId);
        drill.ClubId = clubId;
        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);
        _clubsClient.IsUserCoachInClubAsync(CreatorId, clubId).Returns(false);
        var request = CompleteUpdateRequest(drill.Id, clubId);

        // Act
        var act = () => _sut.UpdateAsync(request, CreatorId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        drill.Name.Should().Be("Original");
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_MovingClubDrill_RequiresCoachRoleInSourceAndDestinationClubs()
    {
        // Arrange
        var sourceClubId = Guid.NewGuid();
        var destinationClubId = Guid.NewGuid();
        var drill = NewDrill("Original", CreatorId);
        drill.ClubId = sourceClubId;
        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);
        _clubsClient.IsUserCoachInClubAsync(CreatorId, sourceClubId).Returns(true);
        _clubsClient.IsUserCoachInClubAsync(CreatorId, destinationClubId).Returns(false);
        var request = CompleteUpdateRequest(drill.Id, destinationClubId);

        // Act
        var act = () => _sut.UpdateAsync(request, CreatorId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        drill.ClubId.Should().Be(sourceClubId);
        drill.Name.Should().Be("Original");
        await _clubsClient.Received(1).IsUserCoachInClubAsync(CreatorId, sourceClubId);
        await _clubsClient.Received(1).IsUserCoachInClubAsync(CreatorId, destinationClubId);
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task UpdateAsync_WithoutName_RejectsRequestWithoutChangingDrill(string? name)
    {
        // Arrange
        var drill = NewDrill("Original", CreatorId);
        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);
        var request = CompleteUpdateRequest(drill.Id) with { Name = name! };

        // Act
        var act = () => _sut.UpdateAsync(request, CreatorId);

        // Assert
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be(ErrorCodeEnum.ValidationError);
        drill.Name.Should().Be("Original");
        _drillRepository.DidNotReceive().Update(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_WhenCallerIsNotCreator_RejectsWithoutChangingDrill()
    {
        // Arrange
        var drill = NewDrill("Original", CreatorId);
        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);
        var request = CompleteUpdateRequest(drill.Id);

        // Act
        var act = () => _sut.UpdateAsync(request, Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        drill.Name.Should().Be("Original");
        _drillRepository.DidNotReceive().Update(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_WhenDrillDoesNotExist_ThrowsNotFoundWithoutWriting()
    {
        // Arrange
        var request = CompleteUpdateRequest(Guid.NewGuid());
        _drillRepository.GetByIdWithDetailsAsync(request.Id).Returns((Drill?)null);

        // Act
        var act = () => _sut.UpdateAsync(request, CreatorId);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException>();
        _drillRepository.DidNotReceive().Update(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_WithSelfVariation_RejectsWithoutChangingDrill()
    {
        // Arrange
        var drill = NewDrill("Original", CreatorId);
        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);
        var request = CompleteUpdateRequest(drill.Id, variations:
            [new CreateDrillVariationInput(drill.Id, "Recursive")]);

        // Act
        var act = () => _sut.UpdateAsync(request, CreatorId);

        // Assert
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be(ErrorCodeEnum.ValidationError);
        drill.Name.Should().Be("Original");
        _drillRepository.DidNotReceive().Update(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_WithADataVideoUrl_RejectsNamingItWithoutChangingDrill()
    {
        // Arrange
        var drill = NewDrill("Existing", CreatorId);
        drill.VideoUrl = "https://video.test/original";
        _drillRepository.GetByIdWithDetailsAsync(drill.Id).Returns(drill);
        var request = CompleteUpdateRequest(drill.Id) with { VideoUrl = "data:text/html,<script>alert(1)</script>" };

        // Act
        var act = () => _sut.UpdateAsync(request, CreatorId);

        // Assert
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.FieldErrors.Select(e => e.Field).Should().Equal("videoUrl");
        drill.VideoUrl.Should().Be("https://video.test/original");
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task AddAttachmentAsync_WithAJavascriptFileUrl_RejectsNamingItWithoutWriting()
    {
        // Arrange
        var drill = NewDrill("Existing", CreatorId);
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);
        var request = new CreateDrillAttachmentDto("Tap me", "javascript:alert(1)", DrillAttachmentType.Document, 0);

        // Act
        var act = () => _sut.AddAttachmentAsync(drill.Id, request, CreatorId);

        // Assert
        var exception = await act.Should().ThrowAsync<ValidationException>();
        exception.Which.FieldErrors.Select(e => e.Field).Should().Equal("fileUrl");
        _attachmentRepository.DidNotReceive().Add(Arg.Any<DrillAttachment>());
        await _attachmentRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task AddAttachmentAsync_WithAnHttpsLink_AddsIt()
    {
        // Arrange
        var drill = NewDrill("Existing", CreatorId);
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);
        var request = new CreateDrillAttachmentDto("Reference", "https://youtu.be/serve", DrillAttachmentType.Video, 0);

        // Act
        await _sut.AddAttachmentAsync(drill.Id, request, CreatorId);

        // Assert
        _attachmentRepository.Received(1).Add(Arg.Is<DrillAttachment>(a => a.FileUrl == "https://youtu.be/serve"));
        await _attachmentRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task DeleteAsync_AsCreator_DeletesAndPersistsDrill()
    {
        // Arrange
        var drill = NewDrill("To delete", CreatorId);
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);

        // Act
        await _sut.DeleteAsync(drill.Id, CreatorId);

        // Assert
        _drillRepository.Received(1).Delete(drill);
        await _drillRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task DeleteAsync_AsDifferentUser_RejectsWithoutDeleting()
    {
        // Arrange
        var drill = NewDrill("Not yours", CreatorId);
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);

        // Act
        var act = () => _sut.DeleteAsync(drill.Id, Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _drillRepository.DidNotReceive().Delete(Arg.Any<Drill>());
        await _drillRepository.DidNotReceive().SaveChangesAsync();
    }

    private static Drill NewDrill(string name, Guid? creatorId = null) => new()
    {
        Name = name,
        CreatedByUserId = creatorId ?? Guid.NewGuid()
    };

    private static CreateDrillDto CompleteCreateRequest(
        Guid? clubId = null,
        CreateDrillVariationInput[]? variations = null) => new(
        Name: "Serve receive under pressure",
        Description: "Rehearse three-player reception patterns.",
        Category: DrillCategory.Technical,
        Intensity: DrillIntensity.High,
        Visibility: DrillVisibility.Private,
        Skills: [DrillSkill.Serving, DrillSkill.Passing],
        Duration: 18,
        MinPlayers: 4,
        MaxPlayers: 12,
        Instructions: ["Form two groups", "Serve to zones"],
        CoachingPoints: ["Hold the platform", "Call early"],
        Variations: variations ?? [],
        Equipment:
        [
            new DrillEquipmentInput("Volleyballs"),
            new DrillEquipmentInput("Targets", IsOptional: true)
        ],
        VideoUrl: "https://video.test/serve-receive",
        ClubId: clubId);

    private static UpdateDrillDto CompleteUpdateRequest(
        Guid id,
        Guid? clubId = null,
        CreateDrillVariationInput[]? variations = null) => new(
        Id: id,
        Name: "Updated transition drill",
        Description: "Updated description",
        Category: DrillCategory.Tactical,
        Intensity: DrillIntensity.Medium,
        Visibility: DrillVisibility.Public,
        Skills: [DrillSkill.Defense, DrillSkill.Attacking],
        Duration: 25,
        MinPlayers: 6,
        MaxPlayers: 14,
        Instructions: ["Updated first step", "Updated second step"],
        CoachingPoints: ["Read the setter"],
        Variations: variations ?? [],
        Equipment:
        [
            new DrillEquipmentInput("Updated net"),
            new DrillEquipmentInput("Updated cones", IsOptional: true)
        ],
        VideoUrl: "https://video.test/updated",
        ClubId: clubId);

    private static DrillDto ToDto(Drill drill) => new()
    {
        Id = drill.Id,
        Name = drill.Name,
        Description = drill.Description,
        Category = drill.Category,
        Intensity = drill.Intensity,
        Visibility = drill.Visibility,
        Skills = drill.Skills,
        Duration = drill.Duration,
        MinPlayers = drill.MinPlayers,
        MaxPlayers = drill.MaxPlayers,
        Instructions = drill.Instructions,
        CoachingPoints = drill.CoachingPoints,
        VideoUrl = drill.VideoUrl,
        CreatedByUserId = drill.CreatedByUserId,
        ClubId = drill.ClubId,
        LikeCount = drill.LikeCount,
        CreatedAt = drill.CreatedAt ?? default,
        UpdatedAt = drill.UpdatedAt
    };
}
