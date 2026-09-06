using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Tactics;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Application.Services;

/// <summary>One shelf of boards, once its scope and its ids have been checked against each other.</summary>
public readonly record struct TacticsShelf(TacticsScope Scope, Guid? ClubId, Guid? TeamId);

/// <summary>
/// Who may see a shelf of tactics boards.
///
/// Boards are coaching material, not player-facing: a club's boards are for the people who run or
/// coach the club, and a team's for that team's staff — plus the club staff above them, who would
/// otherwise need a row on every team they oversee.
/// </summary>
public static class TacticsAccess
{
    /// <summary>
    /// The shelf a request names, rejecting one that does not hold together. Ids that do not belong
    /// to the scope are dropped rather than stored, so a personal board cannot carry a club id.
    /// </summary>
    public static TacticsShelf ShelfOf(TacticsScope scope, Guid? clubId, Guid? teamId) => scope switch
    {
        TacticsScope.Personal => new TacticsShelf(scope, null, null),
        TacticsScope.Club when clubId is { } club && club != Guid.Empty => new TacticsShelf(scope, club, null),
        TacticsScope.Club => throw new BadRequestException("A club board needs a club", ErrorCodeEnum.ValidationError),
        TacticsScope.Team when teamId is { } team && team != Guid.Empty => new TacticsShelf(scope, null, team),
        TacticsScope.Team => throw new BadRequestException("A team board needs a team", ErrorCodeEnum.ValidationError),
        _ => throw new BadRequestException("Unknown board scope", ErrorCodeEnum.ValidationError)
    };

    public static TacticsShelf ShelfOf(TacticsBoard board) => new(board.Scope, board.ClubId, board.TeamId);

    public static TacticsShelf ShelfOf(TacticsFolder folder) => new(folder.Scope, folder.ClubId, folder.TeamId);

    /// <summary>
    /// Throws unless this user may read and write the shelf. Returns it with the owning club filled
    /// in — resolved from the team rather than taken from the request, because the caller does not
    /// get to nominate which club vouches for them.
    /// </summary>
    public static async Task<TacticsShelf> EnsureMayUseAsync(TacticsShelf shelf, Guid userId, IClubsGrpcClient clubs)
    {
        switch (shelf.Scope)
        {
            case TacticsScope.Personal:
                return shelf;

            case TacticsScope.Club:
                if (!await clubs.IsClubStaffAsync(userId, shelf.ClubId!.Value))
                    throw new ForbiddenException("Only this club's coaches and admins can open its tactics boards");
                return shelf;

            case TacticsScope.Team:
                var teamId = shelf.TeamId!.Value;
                var staffOfTeam = clubs.IsUnitStaffAsync(userId, ContextType.Team, teamId);
                var owningClub = await clubs.ResolveClubIdAsync(ContextType.Team, teamId);
                var allowed = await staffOfTeam
                    || (owningClub is { } club && await clubs.IsClubStaffAsync(userId, club));

                if (!allowed)
                    throw new ForbiddenException("Only this team's staff can open its tactics boards");
                return shelf with { ClubId = owningClub };

            default:
                throw new BadRequestException("Unknown board scope", ErrorCodeEnum.ValidationError);
        }
    }

    /// <summary>
    /// Throws unless this user may open one stored board. A personal board answers here and nowhere
    /// else: it is the owner's, and no club standing reaches into it.
    /// </summary>
    public static async Task EnsureMayOpenAsync(TacticsBoard board, Guid userId, IClubsGrpcClient clubs)
    {
        if (board.Scope == TacticsScope.Personal)
        {
            if (board.OwnerUserId != userId)
                throw new ForbiddenException("This board belongs to somebody else");
            return;
        }

        await EnsureMayUseAsync(ShelfOf(board), userId, clubs);
    }

    public static async Task EnsureMayOpenAsync(TacticsFolder folder, Guid userId, IClubsGrpcClient clubs)
    {
        if (folder.Scope == TacticsScope.Personal)
        {
            if (folder.OwnerUserId != userId)
                throw new ForbiddenException("This folder belongs to somebody else");
            return;
        }

        await EnsureMayUseAsync(ShelfOf(folder), userId, clubs);
    }
}
