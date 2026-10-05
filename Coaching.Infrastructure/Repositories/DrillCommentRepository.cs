using Coaching.Application.DTOs.Comments;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Domain.Models.Drills;
using Coaching.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Shared.DataAccess.Repositories;

namespace Coaching.Infrastructure.Repositories;

public class DrillCommentRepository : BaseRepository<DrillComment>, IDrillCommentRepository
{
    public DrillCommentRepository(CoachingDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<DrillComment>> GetByDrillWithCursorAsync(Guid drillId, Guid? cursor, int limit)
    {
        var query = _dbSet
            .Where(c => c.DrillId == drillId && c.ParentCommentId == null)
            .Include(c => c.User)
            .Include(c => c.Replies.Where(r => !r.IsDeleted))
                .ThenInclude(r => r.User)
            .OrderByDescending(c => c.CreatedAt);

        if (cursor.HasValue)
        {
            var cursorComment = await _dbSet.FindAsync(cursor.Value);
            if (cursorComment != null)
            {
                query = (IOrderedQueryable<DrillComment>)query
                    .Where(c => c.CreatedAt < cursorComment.CreatedAt ||
                               (c.CreatedAt == cursorComment.CreatedAt && c.Id != cursor.Value));
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
                c.Id, c.DrillId, c.UserId, c.ParentCommentId, c.Content, c.CreatedAt, c.UpdatedAt, c.IsDeleted))
            .ToListAsync();

    public async Task<int> GetCountByDrillAsync(Guid drillId)
    {
        return await _dbSet.CountAsync(c => c.DrillId == drillId);
    }
}
