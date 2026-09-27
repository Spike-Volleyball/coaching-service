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
using Shared.Options;
using Shared.Services.Analytics;
using Shared.Services.FileStorage.Intefaces;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// SPI-5462: club-scoped drill reads must verify the requesting user is a member of the
/// contextual club before including that club's (non-public) drills.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DrillServiceTests
{
    private IDrillRepository _drillRepository = null!;
    private IDrillLikeRepository _likeRepository = null!;
    private IDrillBookmarkRepository _bookmarkRepository = null!;
    private IClubsGrpcClient _clubsClient = null!;
    private IMapper _mapper = null!;
    private DrillService _sut = null!;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();
    private static readonly Guid ForeignClubId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _drillRepository = Substitute.For<IDrillRepository>();
        _likeRepository = Substitute.For<IDrillLikeRepository>();
        _bookmarkRepository = Substitute.For<IDrillBookmarkRepository>();
        _clubsClient = Substitute.For<IClubsGrpcClient>();
        _mapper = Substitute.For<IMapper>();

        _bookmarkRepository.GetBookmarkCountsAsync(Arg.Any<IEnumerable<Guid>>()).Returns(new Dictionary<Guid, int>());
        _likeRepository.GetUserLikedDrillIdsAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns([]);
        _bookmarkRepository.GetUserBookmarkedDrillIdsAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns([]);
        _mapper.Map<IEnumerable<DrillDto>>(Arg.Any<IEnumerable<Drill>>())
            .Returns(call => call.Arg<IEnumerable<Drill>>()
                .Select(d => new DrillDto { Id = d.Id, Name = d.Name, ClubId = d.ClubId, CreatedByUserId = d.CreatedByUserId })
                .ToList());
        _mapper.Map<DrillDto>(Arg.Any<Drill>())
            .Returns(call =>
            {
                var drill = call.Arg<Drill>();
                return new DrillDto
                {
                    Id = drill.Id,
                    Name = drill.Name,
                    ClubId = drill.ClubId,
                    CreatedByUserId = drill.CreatedByUserId,
                    Visibility = drill.Visibility
                };
            });

        _sut = new DrillService(
            _drillRepository,
            _likeRepository,
            _bookmarkRepository,
            Substitute.For<IDrillCommentRepository>(),
            Substitute.For<IDrillAttachmentRepository>(),
            _clubsClient,
            Substitute.For<IFileService>(),
            Options.Create(new S3Settings { Bucket = "test-bucket", PublicBaseUrl = "https://cdn.test" }),
            _mapper,
            Substitute.For<ILogger<DrillService>>(),
            Substitute.For<IDrillDialReconciler>(),
            Substitute.For<IAnalyticsCapture>());
    }

    private static Drill BuildDrill(
        Guid? clubId = null,
        Guid? createdBy = null,
        DrillVisibility visibility = DrillVisibility.Private) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Drill",
        CreatedByUserId = createdBy ?? Guid.NewGuid(),
        ClubId = clubId,
        Visibility = visibility
    };

    private void StubDrills(params Drill[] drills) =>
        _drillRepository.Query().Returns(drills.ToList().BuildMock());

    [Test]
    public async Task GetByIdAsync_PrivateClubDrill_MemberCanRead()
    {
        // Arrange
        var clubDrill = BuildDrill(clubId: ClubId);
        _drillRepository.GetByIdWithDetailsAsync(clubDrill.Id).Returns(clubDrill);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(true);

        // Act
        var result = await _sut.GetByIdAsync(clubDrill.Id, UserId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(clubDrill.Id);
        await _clubsClient.Received(1).IsUserClubMemberAsync(UserId, ClubId);
    }

    [Test]
    public async Task GetByIdAsync_PrivateClubDrill_NonMemberGetsTheNotFoundAMissingDrillGives()
    {
        // Arrange
        var clubDrill = BuildDrill(clubId: ClubId);
        _drillRepository.GetByIdWithDetailsAsync(clubDrill.Id).Returns(clubDrill);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(false);

        // Act
        var act = () => _sut.GetByIdAsync(clubDrill.Id, UserId);

        // Assert
        await act.Should().ThrowAsync<Shared.Exceptions.EntityNotFoundException>();
    }

    [Test]
    public async Task GetByFilterAsync_ScopeClub_MemberSeesClubDrills()
    {
        // Arrange
        var clubDrill = BuildDrill(clubId: ClubId);
        var foreignClubDrill = BuildDrill(clubId: ForeignClubId);
        StubDrills(clubDrill, foreignClubDrill);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(true);
        var filter = new DrillFilterRequest { Scope = DrillScope.Club, ClubId = ClubId };

        // Act
        var result = await _sut.GetByFilterAsync(filter, UserId);

        // Assert
        result.Items.Should().ContainSingle(d => d.Id == clubDrill.Id);
    }

    [Test]
    public async Task GetByFilterAsync_ScopeClub_NonMemberSeesNoClubDrills()
    {
        // Arrange — a foreign club's private drills must not leak to a non-member who merely
        // supplies that club's ID.
        var clubDrill = BuildDrill(clubId: ClubId);
        StubDrills(clubDrill);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(false);
        var filter = new DrillFilterRequest { Scope = DrillScope.Club, ClubId = ClubId };

        // Act
        var result = await _sut.GetByFilterAsync(filter, UserId);

        // Assert
        result.Items.Should().BeEmpty();
    }

    [Test]
    public async Task GetByFilterAsync_ScopeAll_NonMemberExcludesForeignClubDrills()
    {
        // Arrange — the default/"All" scope mixes public + own + contextual-club drills in one
        // query; a non-member must still see the public/own portion, just not the club portion.
        var publicDrill = BuildDrill(visibility: DrillVisibility.Public);
        var myDrill = BuildDrill(createdBy: UserId);
        var clubDrill = BuildDrill(clubId: ClubId);
        StubDrills(publicDrill, myDrill, clubDrill);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(false);
        var filter = new DrillFilterRequest { Scope = DrillScope.All, ClubId = ClubId };

        // Act
        var result = await _sut.GetByFilterAsync(filter, UserId);

        // Assert
        result.Items.Select(d => d.Id).Should().BeEquivalentTo([publicDrill.Id, myDrill.Id]);
    }

    [Test]
    public async Task GetByFilterAsync_ScopeAll_MemberIncludesClubDrills()
    {
        // Arrange
        var publicDrill = BuildDrill(visibility: DrillVisibility.Public);
        var clubDrill = BuildDrill(clubId: ClubId);
        StubDrills(publicDrill, clubDrill);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(true);
        var filter = new DrillFilterRequest { Scope = DrillScope.All, ClubId = ClubId };

        // Act
        var result = await _sut.GetByFilterAsync(filter, UserId);

        // Assert
        result.Items.Select(d => d.Id).Should().BeEquivalentTo([publicDrill.Id, clubDrill.Id]);
    }

    [Test]
    public async Task GetByFilterAsync_ScopeMine_WithNoClubId_SkipsMembershipCheck()
    {
        // Arrange — public/own scopes must be unaffected: no clubId means no club-membership
        // call at all, regardless of scope.
        var mine = BuildDrill(createdBy: UserId);
        StubDrills(mine);
        var filter = new DrillFilterRequest { Scope = DrillScope.Mine };

        // Act
        var result = await _sut.GetByFilterAsync(filter, UserId);

        // Assert
        result.Items.Should().ContainSingle(d => d.Id == mine.Id);
        await _clubsClient.DidNotReceive().IsUserClubMemberAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task GetClubDrillsAsync_Member_ReturnsClubDrills()
    {
        // Arrange
        var clubDrill = BuildDrill(clubId: ClubId);
        _drillRepository.GetByClubAsync(ClubId).Returns(new List<Drill> { clubDrill });
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(true);

        // Act
        var result = await _sut.GetClubDrillsAsync(ClubId, UserId);

        // Assert
        result.Should().ContainSingle(d => d.Id == clubDrill.Id);
    }

    [Test]
    public async Task GetClubDrillsAsync_NonMember_ReturnsEmptyWithoutQueryingDrills()
    {
        // Arrange
        var clubDrill = BuildDrill(clubId: ClubId);
        _drillRepository.GetByClubAsync(ClubId).Returns(new List<Drill> { clubDrill });
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(false);

        // Act
        var result = await _sut.GetClubDrillsAsync(ClubId, UserId);

        // Assert
        result.Should().BeEmpty();
        await _drillRepository.DidNotReceive().GetByClubAsync(Arg.Any<Guid>());
    }
}
