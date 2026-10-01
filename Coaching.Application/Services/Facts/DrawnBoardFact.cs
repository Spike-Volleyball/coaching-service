using Coaching.Domain.Models.Tactics;
using Shared.Messaging.Contracts.Events.Coaching;

namespace Coaching.Application.Services.Facts;

/// <summary>
/// What rewards-service holds of a tactics board somebody made: that it has a drawing, and the tools
/// that drawing uses. Its snapshot is <see cref="TacticsBoardDrawnEvent"/>, published when the board
/// is first drawn and whenever a save changes its tools.
/// </summary>
public static class DrawnBoardFact
{
    /// <summary>The snapshot of a board that has been drawn, which is every board with a <see cref="TacticsBoard.DrawnAt"/>.</summary>
    public static TacticsBoardDrawnEvent Snapshot(TacticsBoard board, DateTime drawnAt, DateTime snapshotAt) => new()
    {
        BoardId = board.Id,
        MakerUserId = board.OwnerUserId,
        DrawnAt = drawnAt,
        Tools = board.Tools,
        SnapshotAt = snapshotAt,
    };
}
