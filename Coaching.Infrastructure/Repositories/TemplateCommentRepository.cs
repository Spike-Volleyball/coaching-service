using Coaching.Application.DTOs.Comments;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class PlanCommentRepository : BaseRepository<PlanComment>, IPlanCommentRepository
{
    public PlanCommentRepository(CoachingDbContext context) : base(context) { }

    public async Task<IEnumerable<PlanComment>> GetByTemplateWithCursorAsync(Guid templateId, Guid? cursor, int limit)
    {
        var query = _dbSet
            .Where(c => c.TemplateId == templateId && c.ParentCommentId == null && !c.IsDeleted)
            .Include(c => c.User)
            .Include(c => c.Replies.Where(r => !r.IsDeleted))
                .ThenInclude(r => r.User)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id);

        if (cursor.HasValue)
        {
            var cursorComment = await _dbSet.FindAsync(cursor.Value);
            if (cursorComment != null)
            {
                query = (IOrderedQueryable<PlanComment>)query
                    .Where(c => c.CreatedAt < cursorComment.CreatedAt ||
                               (c.CreatedAt == cursorComment.CreatedAt && c.Id < cursorComment.Id));
            }
        }

        return await query.Take(limit + 1).ToListAsync();
    }

    // Includes soft-deleted comments and those of deleted resources: the hand-over to social keeps
    // what the thread's readers no longer see, and pages by id so a page never shifts under it.
    public async Task<List<CommentExport>> ExportPageAsync(Guid? afterId, int limit) =>
        await _dbSet
            .IgnoreQueryFilters()
            .Where(c => afterId == null || c.Id.CompareTo(afterId.Value) > 0)
            .OrderBy(c => c.Id)
            .Take(limit)
            .Select(c => new CommentExport(
                c.Id, c.TemplateId, c.UserId, c.ParentCommentId, c.Content, c.CreatedAt, c.UpdatedAt, c.IsDeleted))
            .ToListAsync();

    public async Task<int> GetCountByTemplateAsync(Guid templateId)
    {
        return await _dbSet.CountAsync(c => c.TemplateId == templateId && !c.IsDeleted);
    }
}
