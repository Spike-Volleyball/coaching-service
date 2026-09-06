using Coaching.Domain.Enums;

namespace Coaching.Application.DTOs.Tactics;

/// <summary>Which shelf a request is about. Every list and create call carries one.</summary>
public record TacticsShelfQuery
{
    public TacticsScope Scope { get; init; }
    public Guid? ClubId { get; init; }
    public Guid? TeamId { get; init; }
}

public class TacticsBoardDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;
    public TacticsScope Scope { get; set; }
    public Guid? ClubId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? FolderId { get; set; }
    public bool IsFavorite { get; set; }
    public int FrameCount { get; set; }
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Left out of listings — a shelf of boards would be megabytes of scenes nobody asked for.</summary>
    public string? Document { get; set; }
}

public class TacticsFolderDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TacticsScope Scope { get; set; }
    public Guid? ClubId { get; set; }
    public Guid? TeamId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One board on its way in. The same body creates and updates, because the editor saves the board
/// it is holding at an id it chose itself, and a save that arrives twice must not make two boards.
/// </summary>
public record SaveTacticsBoardRequest
{
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string System { get; init; } = string.Empty;
    public TacticsScope Scope { get; init; }
    public Guid? ClubId { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? FolderId { get; init; }
    public bool IsFavorite { get; init; }
    public int FrameCount { get; init; }
    public string Document { get; init; } = string.Empty;

    /// <summary>
    /// The version the client last read. Absent on a first save; present afterwards, and a value
    /// behind the stored one is refused so a second coach's work is not silently overwritten.
    /// </summary>
    public int? ExpectedVersion { get; init; }
}

/// <summary>
/// A whole starter shelf in one request. Written as one batch because seeding board by board
/// stamps each with its own time, and the library orders by recency — the shelf would come out
/// back to front, and a client that asked twice would leave two of everything.
/// </summary>
public record SeedTacticsBoardsRequest
{
    public TacticsScope Scope { get; init; }
    public Guid? ClubId { get; init; }
    public Guid? TeamId { get; init; }
    public List<SeedTacticsBoard> Boards { get; init; } = [];
}

public record SeedTacticsBoard
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string System { get; init; } = string.Empty;
    public Guid? FolderId { get; init; }
    public bool IsFavorite { get; init; }
    public int FrameCount { get; init; }
    public string Document { get; init; } = string.Empty;
}

public record CreateTacticsFolderRequest
{
    public TacticsScope Scope { get; init; }
    public Guid? ClubId { get; init; }
    public Guid? TeamId { get; init; }
    public string Name { get; init; } = string.Empty;
}

public record RenameTacticsFolderRequest
{
    public string Name { get; init; } = string.Empty;
}
