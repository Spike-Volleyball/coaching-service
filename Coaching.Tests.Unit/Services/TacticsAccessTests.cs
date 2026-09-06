using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Tactics;
using FluentAssertions;
using NSubstitute;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class TacticsAccessTests
{
    private IClubsGrpcClient _clubs = null!;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();

    [SetUp]
    public void SetUp() => _clubs = Substitute.For<IClubsGrpcClient>();

    [Test]
    public void ShelfOf_PersonalScope_DropsClubAndTeamTheCallerSent()
    {
        // A personal board carrying a club id would list on that club's shelf without ever being
        // authorised against it.
        var shelf = TacticsAccess.ShelfOf(TacticsScope.Personal, ClubId, TeamId);

        shelf.ClubId.Should().BeNull();
        shelf.TeamId.Should().BeNull();
    }

    [Test]
    public void ShelfOf_ClubScopeWithoutClub_Throws()
    {
        var act = () => TacticsAccess.ShelfOf(TacticsScope.Club, null, null);

        act.Should().Throw<BadRequestException>();
    }

    [Test]
    public void ShelfOf_TeamScopeWithoutTeam_Throws()
    {
        var act = () => TacticsAccess.ShelfOf(TacticsScope.Team, ClubId, null);

        act.Should().Throw<BadRequestException>();
    }

    [Test]
    public async Task EnsureMayUseAsync_ClubStaff_IsAllowed()
    {
        _clubs.IsClubStaffAsync(UserId, ClubId).Returns(true);

        var shelf = await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(TacticsScope.Club, ClubId, null), UserId, _clubs);

        shelf.ClubId.Should().Be(ClubId);
    }

    [Test]
    public async Task EnsureMayUseAsync_ClubMemberWhoIsNotStaff_IsRefused()
    {
        _clubs.IsClubStaffAsync(UserId, ClubId).Returns(false);
        _clubs.IsUserClubMemberAsync(UserId, ClubId).Returns(true);

        var act = async () => await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(TacticsScope.Club, ClubId, null), UserId, _clubs);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task EnsureMayUseAsync_TeamStaff_IsAllowedAndKeepsTheOwningClub()
    {
        _clubs.IsUnitStaffAsync(UserId, ContextType.Team, TeamId).Returns(true);
        _clubs.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);

        var shelf = await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(TacticsScope.Team, null, TeamId), UserId, _clubs);

        shelf.TeamId.Should().Be(TeamId);
        shelf.ClubId.Should().Be(ClubId);
    }

    [Test]
    public async Task EnsureMayUseAsync_ClubStaffAboveTheTeam_IsAllowedWithoutATeamRole()
    {
        _clubs.IsUnitStaffAsync(UserId, ContextType.Team, TeamId).Returns(false);
        _clubs.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubs.IsClubStaffAsync(UserId, ClubId).Returns(true);

        var shelf = await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(TacticsScope.Team, null, TeamId), UserId, _clubs);

        shelf.ClubId.Should().Be(ClubId);
    }

    [Test]
    public async Task EnsureMayUseAsync_TeamScope_ResolvesTheClubRatherThanTrustingTheCaller()
    {
        // The caller names a club they do run; the team belongs to another one. Believing the
        // request would hand them every board of a club they have no standing in.
        var claimedClub = Guid.NewGuid();
        _clubs.IsUnitStaffAsync(UserId, ContextType.Team, TeamId).Returns(false);
        _clubs.IsClubStaffAsync(UserId, claimedClub).Returns(true);
        _clubs.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubs.IsClubStaffAsync(UserId, ClubId).Returns(false);

        var act = async () => await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(TacticsScope.Team, claimedClub, TeamId), UserId, _clubs);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task EnsureMayUseAsync_NeitherTeamNorClubStaff_IsRefused()
    {
        _clubs.IsUnitStaffAsync(UserId, ContextType.Team, TeamId).Returns(false);
        _clubs.ResolveClubIdAsync(ContextType.Team, TeamId).Returns(ClubId);
        _clubs.IsClubStaffAsync(UserId, ClubId).Returns(false);

        var act = async () => await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(TacticsScope.Team, null, TeamId), UserId, _clubs);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task EnsureMayOpenAsync_PersonalBoardOfAnotherUser_IsRefusedEvenForClubStaff()
    {
        // No club standing reaches into somebody's own shelf.
        _clubs.IsClubStaffAsync(UserId, ClubId).Returns(true);
        var board = PersonalBoardOf(OtherUserId);

        var act = async () => await TacticsAccess.EnsureMayOpenAsync(board, UserId, _clubs);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task EnsureMayOpenAsync_OwnPersonalBoard_IsAllowedWithoutAskingClubs()
    {
        await TacticsAccess.EnsureMayOpenAsync(PersonalBoardOf(UserId), UserId, _clubs);

        await _clubs.DidNotReceiveWithAnyArgs().IsClubStaffAsync(default, default);
    }

    [Test]
    public async Task EnsureMayOpenAsync_ClubBoard_AsksAboutTheBoardsClubNotTheCallers()
    {
        var board = new TacticsBoard
        {
            Title = "Serve receive",
            Category = "Match day",
            System = "5-1",
            Document = "{}",
            Scope = TacticsScope.Club,
            ClubId = ClubId,
            OwnerUserId = OtherUserId
        };
        _clubs.IsClubStaffAsync(UserId, ClubId).Returns(true);

        await TacticsAccess.EnsureMayOpenAsync(board, UserId, _clubs);

        await _clubs.Received(1).IsClubStaffAsync(UserId, ClubId);
    }

    private static TacticsBoard PersonalBoardOf(Guid ownerId) => new()
    {
        Title = "Serve receive",
        Category = "Match day",
        System = "5-1",
        Document = "{}",
        Scope = TacticsScope.Personal,
        OwnerUserId = ownerId
    };
}
