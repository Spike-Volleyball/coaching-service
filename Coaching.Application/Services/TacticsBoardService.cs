using System.Linq.Expressions;
using System.Text.Json;
using Coaching.Application.DTOs.Tactics;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Tactics;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Enums;
using Shared.Exceptions;

namespace Coaching.Application.Services;

public class TacticsBoardService(
    IRepository<TacticsBoard> boards,
    IRepository<TacticsFolder> folders,
    IClubsGrpcClient clubs) : ITacticsBoardService
{
    /// <summary>
    /// The largest board the server will store. A busy board is tens of kilobytes; this is the
    /// ceiling that stops a runaway client filling a column nobody can read back.
    /// </summary>
    public const int MaxDocumentLength = 2_000_000;

    public async Task<IReadOnlyList<TacticsBoardDto>> ListBoardsAsync(TacticsShelfQuery query, Guid userId)
    {
        var shelf = await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(query.Scope, query.ClubId, query.TeamId), userId, clubs);

        return await boards.QueryNoTracking()
            .Where(OnShelf(shelf, userId))
            .OrderByDescending(b => b.UpdatedAt)
            .Select(SummaryProjection)
            .ToListAsync();
    }

    public async Task<TacticsBoardDto> GetBoardAsync(Guid boardId, Guid userId)
    {
        var board = await boards.GetByIdAsync(boardId) ?? throw NoBoard();
        await TacticsAccess.EnsureMayOpenAsync(board, userId, clubs);

        var dto = Summary(board);
        dto.Document = board.Document;
        return dto;
    }

    public async Task<TacticsBoardDto> SaveBoardAsync(Guid boardId, SaveTacticsBoardRequest request, Guid userId)
    {
        var title = Require(request.Title, "A board needs a title", TacticsBoard.TitleMaxLength);
        var document = ValidDocument(request.Document);
        var shelf = await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(request.Scope, request.ClubId, request.TeamId), userId, clubs);
        var folderId = await FolderOnShelfAsync(request.FolderId, shelf, userId);

        var board = await boards.GetByIdAsync(boardId);
        if (board is null)
        {
            board = new TacticsBoard
            {
                Id = boardId,
                Title = title,
                Category = string.Empty,
                System = string.Empty,
                Document = document,
                OwnerUserId = userId
            };
            boards.Add(board);
        }
        else
        {
            // The board this client last read. A save that names an older one — or names none at all,
            // having never read it — would overwrite whatever somebody else did in between.
            await TacticsAccess.EnsureMayOpenAsync(board, userId, clubs);
            if (request.ExpectedVersion != board.Version)
                throw new ConflictException(
                    "Somebody else saved this board while you were editing it. Reload it to see their version.");
        }

        board.Title = title;
        board.Category = Trim(request.Category, TacticsBoard.CategoryMaxLength);
        board.System = Trim(request.System, TacticsBoard.SystemMaxLength);
        board.Scope = shelf.Scope;
        board.ClubId = shelf.ClubId;
        board.TeamId = shelf.TeamId;
        board.FolderId = folderId;
        board.IsFavorite = request.IsFavorite;
        board.FrameCount = Math.Max(request.FrameCount, 0);
        board.Document = document;
        board.Version++;

        await boards.SaveChangesAsync();

        var dto = Summary(board);
        dto.Document = board.Document;
        return dto;
    }

    public async Task DeleteBoardAsync(Guid boardId, Guid userId)
    {
        var board = await boards.GetByIdAsync(boardId) ?? throw NoBoard();
        await TacticsAccess.EnsureMayOpenAsync(board, userId, clubs);

        boards.Delete(board);
        await boards.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<TacticsFolderDto>> ListFoldersAsync(TacticsShelfQuery query, Guid userId)
    {
        var shelf = await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(query.Scope, query.ClubId, query.TeamId), userId, clubs);

        return await folders.QueryNoTracking()
            .Where(FolderOnShelf(shelf, userId))
            .OrderBy(f => f.Name)
            .Select(FolderProjection)
            .ToListAsync();
    }

    public async Task<TacticsFolderDto> CreateFolderAsync(CreateTacticsFolderRequest request, Guid userId)
    {
        var name = Require(request.Name, "A folder needs a name", TacticsFolder.NameMaxLength);
        var shelf = await TacticsAccess.EnsureMayUseAsync(
            TacticsAccess.ShelfOf(request.Scope, request.ClubId, request.TeamId), userId, clubs);

        var folder = new TacticsFolder
        {
            Name = name,
            Scope = shelf.Scope,
            ClubId = shelf.ClubId,
            TeamId = shelf.TeamId,
            OwnerUserId = userId
        };

        folders.Add(folder);
        await folders.SaveChangesAsync();
        return Describe(folder);
    }

    public async Task<TacticsFolderDto> RenameFolderAsync(Guid folderId, string name, Guid userId)
    {
        var trimmed = Require(name, "A folder needs a name", TacticsFolder.NameMaxLength);
        var folder = await folders.GetByIdAsync(folderId) ?? throw NoFolder();
        await TacticsAccess.EnsureMayOpenAsync(folder, userId, clubs);

        folder.Name = trimmed;
        await folders.SaveChangesAsync();
        return Describe(folder);
    }

    public async Task DeleteFolderAsync(Guid folderId, Guid userId)
    {
        var folder = await folders.GetByIdAsync(folderId) ?? throw NoFolder();
        await TacticsAccess.EnsureMayOpenAsync(folder, userId, clubs);

        // The boards filed in it stay on the shelf, unfiled — losing a folder should never lose work.
        folders.Delete(folder);
        await folders.SaveChangesAsync();
    }

    /// <summary>
    /// Filing a board is only allowed into a folder on the same shelf, so a club board cannot be
    /// hidden inside somebody's personal folder.
    /// </summary>
    private async Task<Guid?> FolderOnShelfAsync(Guid? folderId, TacticsShelf shelf, Guid userId)
    {
        if (folderId is not { } id || id == Guid.Empty)
            return null;

        var exists = await folders.QueryNoTracking()
            .Where(FolderOnShelf(shelf, userId))
            .AnyAsync(f => f.Id == id);

        return exists
            ? id
            : throw new BadRequestException("That folder is not on this shelf", ErrorCodeEnum.ValidationError);
    }

    private static Expression<Func<TacticsBoard, bool>> OnShelf(TacticsShelf shelf, Guid userId) =>
        shelf.Scope switch
        {
            TacticsScope.Club => b => b.Scope == TacticsScope.Club && b.ClubId == shelf.ClubId,
            TacticsScope.Team => b => b.Scope == TacticsScope.Team && b.TeamId == shelf.TeamId,
            _ => b => b.Scope == TacticsScope.Personal && b.OwnerUserId == userId
        };

    private static Expression<Func<TacticsFolder, bool>> FolderOnShelf(TacticsShelf shelf, Guid userId) =>
        shelf.Scope switch
        {
            TacticsScope.Club => f => f.Scope == TacticsScope.Club && f.ClubId == shelf.ClubId,
            TacticsScope.Team => f => f.Scope == TacticsScope.Team && f.TeamId == shelf.TeamId,
            _ => f => f.Scope == TacticsScope.Personal && f.OwnerUserId == userId
        };

    // Hand-mapped rather than through AutoMapper: the document is the bulk of a board and is left
    // out of listings, which a profile would have to special-case anyway. Written once as an
    // expression so the database can do the same projection the in-memory path does — a listing
    // that materialised whole boards would carry every scene of every board on the shelf.
    private static readonly Expression<Func<TacticsBoard, TacticsBoardDto>> SummaryProjection = board => new TacticsBoardDto
    {
        Id = board.Id,
        Title = board.Title,
        Category = board.Category,
        System = board.System,
        Scope = board.Scope,
        ClubId = board.ClubId,
        TeamId = board.TeamId,
        FolderId = board.FolderId,
        IsFavorite = board.IsFavorite,
        FrameCount = board.FrameCount,
        Version = board.Version,
        UpdatedAt = board.UpdatedAt ?? board.CreatedAt ?? DateTime.MinValue
    };

    private static readonly Expression<Func<TacticsFolder, TacticsFolderDto>> FolderProjection = folder => new TacticsFolderDto
    {
        Id = folder.Id,
        Name = folder.Name,
        Scope = folder.Scope,
        ClubId = folder.ClubId,
        TeamId = folder.TeamId,
        UpdatedAt = folder.UpdatedAt ?? folder.CreatedAt ?? DateTime.MinValue
    };

    private static readonly Func<TacticsBoard, TacticsBoardDto> Summary = SummaryProjection.Compile();
    private static readonly Func<TacticsFolder, TacticsFolderDto> Describe = FolderProjection.Compile();

    private static string Require(string? value, string message, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new BadRequestException(message, ErrorCodeEnum.ValidationError);

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static string Trim(string? value, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    /// <summary>
    /// The column is jsonb, so anything that is not JSON fails in the driver as a 500. Parsing it
    /// here turns that into the 400 it actually is.
    /// </summary>
    private static string ValidDocument(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
            throw new BadRequestException("A board needs a document", ErrorCodeEnum.ValidationError);

        if (document.Length > MaxDocumentLength)
            throw new BadRequestException(
                $"This board is too large to save (limit {MaxDocumentLength:N0} characters)", ErrorCodeEnum.ValidationError);

        try
        {
            using var parsed = JsonDocument.Parse(document);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new BadRequestException("A board document must be a JSON object", ErrorCodeEnum.ValidationError);
        }
        catch (JsonException)
        {
            throw new BadRequestException("That board document is not valid JSON", ErrorCodeEnum.ValidationError);
        }

        return document;
    }

    private static EntityNotFoundException NoBoard() =>
        new("Board not found", ErrorCodeEnum.EntityNotFound);

    private static EntityNotFoundException NoFolder() =>
        new("Folder not found", ErrorCodeEnum.EntityNotFound);
}
