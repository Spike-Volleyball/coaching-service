using AutoMapper;
using Coaching.Application.DTOs.Comments;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.Options;
using Shared.Services.Analytics;
using Shared.Services.FileStorage.Intefaces;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// What social is told before it serves or moderates a drill's comments. The reading rule is the
/// one the drill itself follows, and today nobody but a comment's author removes it.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DrillCommentStandingTests
{
    private IDrillRepository _drillRepository = null!;
    private IClubsGrpcClient _clubsClient = null!;
    private DrillService _sut = null!;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _drillRepository = Substitute.For<IDrillRepository>();
        _clubsClient = Substitute.For<IClubsGrpcClient>();

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
            Substitute.For<IAnalyticsCapture>(),
            Substitute.For<IDrillReadGrants>());
    }

    private Drill StubDrill(DrillVisibility visibility, Guid? clubId = null, Guid? createdBy = null)
    {
        var drill = new Drill
        {
            Id = Guid.NewGuid(),
            Name = "Drill",
            CreatedByUserId = createdBy ?? Guid.NewGuid(),
            ClubId = clubId,
            Visibility = visibility
        };
        _drillRepository.GetByIdAsync(drill.Id).Returns(drill);
        return drill;
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAPublicDrill_IsReadableByAnyone()
    {
        // Arrange
        var drill = StubDrill(DrillVisibility.Public);

        // Act
        var standing = await _sut.GetCommentStandingAsync(drill.Id, UserId);

        // Assert
        standing.Should().Be(new CommentStanding(Exists: true, CanRead: true, CanModerate: false));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAPrivateClubDrill_IsReadableByAMemberOfThatClub()
    {
        // Arrange
        var drill = StubDrill(DrillVisibility.Private, clubId: ClubId);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(true);

        // Act
        var standing = await _sut.GetCommentStandingAsync(drill.Id, UserId);

        // Assert
        standing.Should().Be(new CommentStanding(true, true, false));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAPrivateClubDrill_IsNotReadableByANonMember()
    {
        // Arrange
        var drill = StubDrill(DrillVisibility.Private, clubId: ClubId);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(false);

        // Act
        var standing = await _sut.GetCommentStandingAsync(drill.Id, UserId);

        // Assert
        standing.Should().Be(new CommentStanding(true, false, false));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAPrivateDrill_IsReadableByItsAuthor_AndAuthorsDoNotModerate()
    {
        // Arrange
        var drill = StubDrill(DrillVisibility.Private, createdBy: UserId);

        // Act
        var standing = await _sut.GetCommentStandingAsync(drill.Id, UserId);

        // Assert
        standing.Should().Be(new CommentStanding(true, true, false));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnADrillThatDoesNotExist_IsNothing()
    {
        // Act
        var standing = await _sut.GetCommentStandingAsync(Guid.NewGuid(), UserId);

        // Assert
        standing.Should().Be(CommentStanding.Missing);
    }
}
