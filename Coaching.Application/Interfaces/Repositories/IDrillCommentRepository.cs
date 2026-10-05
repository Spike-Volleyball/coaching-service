using Coaching.Application.DTOs.Comments;
using Coaching.Domain.Models.Drills;
using Shared.DataAccess.Repositories.Interfaces;

namespace Coaching.Application.Interfaces.Repositories;

public interface IDrillCommentRepository : IRepository<DrillComment>
{
    Task<IEnumerable<DrillComment>> GetByDrillWithCursorAsync(Guid drillId, Guid? cursor, int limit);
    Task<List<CommentExport>> ExportPageAsync(Guid? afterId, int limit);
    Task<int> GetCountByDrillAsync(Guid drillId);
}
