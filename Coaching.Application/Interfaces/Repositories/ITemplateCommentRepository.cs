using Coaching.Application.DTOs.Comments;
using Coaching.Domain.Models.Templates;
using Shared.DataAccess.Repositories.Interfaces;

namespace Coaching.Application.Interfaces.Repositories;

public interface IPlanCommentRepository : IRepository<PlanComment>
{
    Task<List<CommentExport>> ExportPageAsync(Guid? afterId, int limit);
    Task<IEnumerable<PlanComment>> GetByTemplateWithCursorAsync(Guid templateId, Guid? cursor, int limit);
    Task<int> GetCountByTemplateAsync(Guid templateId);
}
