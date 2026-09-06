using Coaching.Application.DTOs.Tactics;

namespace Coaching.Application.Interfaces.Services;

public interface ITacticsBoardService
{
    Task<IReadOnlyList<TacticsBoardDto>> ListBoardsAsync(TacticsShelfQuery query, Guid userId);
    Task<TacticsBoardDto> GetBoardAsync(Guid boardId, Guid userId);
    Task<TacticsBoardDto> SaveBoardAsync(Guid boardId, SaveTacticsBoardRequest request, Guid userId);
    Task DeleteBoardAsync(Guid boardId, Guid userId);

    Task<IReadOnlyList<TacticsFolderDto>> ListFoldersAsync(TacticsShelfQuery query, Guid userId);
    Task<TacticsFolderDto> CreateFolderAsync(CreateTacticsFolderRequest request, Guid userId);
    Task<TacticsFolderDto> RenameFolderAsync(Guid folderId, string name, Guid userId);
    Task DeleteFolderAsync(Guid folderId, Guid userId);
}
