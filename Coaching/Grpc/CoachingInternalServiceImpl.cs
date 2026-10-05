using System.Globalization;
using Coaching.Application.DTOs.Comments;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Models.Drills;
using Grpc.Core;
using Shared.Contracts.Grpc;
using Shared.DataAccess.Repositories.Interfaces;

namespace Coaching.Grpc;

public class CoachingInternalServiceImpl : CoachingInternalService.CoachingInternalServiceBase
{
    private const int DefaultExportPageSize = 100;
    private const int MaxExportPageSize = 500;

    private readonly IDrillRepository _drillRepository;
    private readonly IDrillService _drillService;
    private readonly ITrainingPlanService _planService;
    private readonly IDrillCommentRepository _drillComments;
    private readonly IPlanCommentRepository _planComments;

    public CoachingInternalServiceImpl(
        IDrillRepository drillRepository,
        IDrillService drillService,
        ITrainingPlanService planService,
        IDrillCommentRepository drillComments,
        IPlanCommentRepository planComments)
    {
        _drillRepository = drillRepository;
        _drillService = drillService;
        _planService = planService;
        _drillComments = drillComments;
        _planComments = planComments;
    }

    public override async Task<ValidateDrillExistsResponse> ValidateDrillExists(
        ValidateDrillExistsRequest request,
        ServerCallContext context)
    {
        var drillId = Guid.Parse(request.DrillId);
        var drill = await _drillRepository.GetByIdAsync(drillId);

        if (drill == null)
        {
            return new ValidateDrillExistsResponse { Exists = false };
        }

        return new ValidateDrillExistsResponse
        {
            Exists = true,
            DrillName = drill.Name,
            Category = drill.Category.ToString(),
            EstimatedDuration = drill.Duration ?? 0,
            Level = drill.Intensity.ToString()
        };
    }

    public override async Task<GetDrillsListResponse> GetDrillsList(
        GetDrillsListRequest request,
        ServerCallContext context)
    {
        var drillIds = request.DrillIds.Select(Guid.Parse).ToList();
        var response = new GetDrillsListResponse();

        foreach (var drillId in drillIds)
        {
            var drill = await _drillRepository.GetByIdAsync(drillId);
            if (drill != null)
            {
                var thumbnailUrl = drill.Attachments?
                    .OrderBy(a => a.Order)
                    .FirstOrDefault()?.FileUrl ?? "";

                response.Drills.Add(new DrillInfo
                {
                    Id = drill.Id.ToString(),
                    Name = drill.Name,
                    Category = drill.Category.ToString(),
                    EstimatedDuration = drill.Duration ?? 0,
                    Level = drill.Intensity.ToString(),
                    ThumbnailUrl = thumbnailUrl
                });
            }
        }

        return response;
    }

    // A "no" is an answer. Only a failure to find out throws, so the caller can deny on it.
    public override async Task<GetCommentStandingResponse> GetCommentStanding(
        GetCommentStandingRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.ResourceId, out var resourceId) || !Guid.TryParse(request.UserId, out var userId))
            return ToResponse(CommentStanding.Missing);

        var standing = request.Kind switch
        {
            CommentResourceKind.Drill => await _drillService.GetCommentStandingAsync(resourceId, userId),
            CommentResourceKind.Plan => await _planService.GetCommentStandingAsync(resourceId, userId),
            _ => CommentStanding.Missing
        };

        return ToResponse(standing);
    }

    // Exists for the one-off import of these comments into social, which pages through all of them,
    // deleted ones flagged. Served on the internal listener only, like every RPC here.
    public override async Task<ExportCommentsResponse> ExportComments(
        ExportCommentsRequest request,
        ServerCallContext context)
    {
        Guid? afterId = null;
        if (!string.IsNullOrEmpty(request.AfterId))
        {
            if (!Guid.TryParse(request.AfterId, out var parsed))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "after_id is not a valid id"));
            afterId = parsed;
        }

        var limit = request.Limit <= 0 ? DefaultExportPageSize : Math.Min(request.Limit, MaxExportPageSize);

        // One more than asked for tells whether another page follows.
        var rows = request.Kind switch
        {
            CommentResourceKind.Drill => await _drillComments.ExportPageAsync(afterId, limit + 1),
            CommentResourceKind.Plan => await _planComments.ExportPageAsync(afterId, limit + 1),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "kind is required"))
        };

        var page = rows.Take(limit).ToList();
        var response = new ExportCommentsResponse
        {
            NextAfterId = rows.Count > limit ? page[^1].Id.ToString() : ""
        };
        response.Comments.AddRange(page.Select(ToExported));
        return response;
    }

    private static GetCommentStandingResponse ToResponse(CommentStanding standing) => new()
    {
        Exists = standing.Exists,
        CanRead = standing.CanRead,
        CanModerate = standing.CanModerate
    };

    private static ExportedComment ToExported(CommentExport comment) => new()
    {
        Id = comment.Id.ToString(),
        ResourceId = comment.ResourceId.ToString(),
        AuthorId = comment.AuthorId.ToString(),
        ParentCommentId = comment.ParentCommentId?.ToString() ?? "",
        Content = comment.Content,
        CreatedAt = Iso(comment.CreatedAt),
        UpdatedAt = Iso(comment.UpdatedAt),
        IsDeleted = comment.IsDeleted
    };

    private static string Iso(DateTime? instant) =>
        instant is { } value
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
            : "";
}
