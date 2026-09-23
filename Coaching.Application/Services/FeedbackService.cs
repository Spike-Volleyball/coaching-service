using AutoMapper;
using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Validation;
using Coaching.Domain.Models.Feedback;
using Ganss.Xss;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Enums;
using Shared.Exceptions;
using Shared.Messaging.Contracts.Events.Coaching;
using Shared.Models;
using Shared.Options;
using Shared.Services.FileStorage.Intefaces;

namespace Coaching.Application.Services;

public class FeedbackService(
    IFeedbackRepository feedbackRepository,
    IRepository<ImprovementPoint> pointRepository,
    IRepository<ImprovementPointDrill> drillLinkRepository,
    IRepository<ImprovementPointMedia> mediaRepository,
    IRepository<FeedbackMedia> feedbackMediaRepository,
    IRepository<Praise> praiseRepository,
    IRepository<Coaching.Domain.Models.Drills.Drill> drillRepository,
    IFeedbackAuthorizationService authorizationService,
    IEventsGrpcClient eventsClient,
    IRepository<UserProfile> userProfileRepository,
    IMapper mapper,
    IFileService fileService,
    IOptions<S3Settings> s3Settings,
    TimeProvider timeProvider,
    IPublishEndpoint publishEndpoint) : IFeedbackService
{
    private static readonly HtmlSanitizer _htmlSanitizer = CreateSanitizer();
    private static readonly Dictionary<string, long> AllowedMediaTypes = new()
    {
        ["image/jpeg"] = 10L * 1024 * 1024,
        ["image/png"] = 10L * 1024 * 1024,
        ["image/gif"] = 10L * 1024 * 1024,
        ["image/webp"] = 10L * 1024 * 1024,
        ["video/mp4"] = 100L * 1024 * 1024,
        ["video/quicktime"] = 100L * 1024 * 1024,
        ["video/webm"] = 100L * 1024 * 1024,
        ["application/pdf"] = 25L * 1024 * 1024,
        ["application/msword"] = 25L * 1024 * 1024,
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = 25L * 1024 * 1024,
        ["application/vnd.ms-excel"] = 25L * 1024 * 1024,
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = 25L * 1024 * 1024,
    };

    private static readonly Dictionary<string, string> ContentTypeExtensions = new()
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["video/mp4"] = ".mp4",
        ["video/quicktime"] = ".mov",
        ["video/webm"] = ".webm",
        ["application/pdf"] = ".pdf",
        ["application/msword"] = ".doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
    };

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(new[]
        {
            "p", "br", "strong", "em", "u", "s", "a",
            "ul", "ol", "li", "h1", "h2", "h3",
            "blockquote", "code", "pre"
        });
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(new[] { "href", "target", "rel" });
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith(new[] { "http", "https" });
        return sanitizer;
    }

    public async Task<FeedbackMediaUploadResponseDto> GetMediaUploadUrlAsync(
        FeedbackMediaUploadRequestDto request, Guid coachUserId)
    {
        var raw = request.ContentType?.Trim().ToLowerInvariant() ?? "";
        var mimeType = raw.Contains(';') ? raw[..raw.IndexOf(';')].TrimEnd() : raw;
        if (!AllowedMediaTypes.TryGetValue(mimeType, out var maxSize))
            throw new BadRequestException("Unsupported file type", ErrorCodeEnum.ValidationError);
        if (request.FileSize <= 0 || request.FileSize > maxSize)
            throw new BadRequestException("File exceeds the allowed size", ErrorCodeEnum.ValidationError);

        var mediaId = Guid.NewGuid();
        var extension = ContentTypeExtensions[mimeType];
        var s3Key = $"feedback/{coachUserId}/{mediaId}{extension}";

        var uploadUrl = await fileService.GetPresignedUploadLink(s3Key, s3Settings.Value.Bucket, mimeType);
        var fileUrl = fileService.GetPublicUrl(s3Key);

        return new FeedbackMediaUploadResponseDto { UploadUrl = uploadUrl, FileUrl = fileUrl };
    }

    public async Task<FeedbackDto> CreateAsync(CreateFeedbackDto request, Guid coachUserId)
    {
        // Validate authorization before creating.
        // ValidateCreateAsync returns the resolved ClubId from event context (if event-linked)
        // so we don't need to call GetEventContextAsync again.
        var resolvedClubId = await authorizationService.ValidateCreateAsync(request, coachUserId);

        // For event-linked feedback, set ClubId from the resolved event context automatically
        if (resolvedClubId.HasValue)
            request = request with { ClubId = resolvedClubId.Value };

        // Before the first save: a create saves in stages, so a late refusal would leave half a feedback.
        LinkUrl.EnsureHttp(AttachmentLinksOf(request.Attachments)
            .Concat((request.ImprovementPoints ?? []).SelectMany((point, i) =>
                PointLinksOf(point.MediaLinks, $"improvementPoints[{i}]."))));

        var feedback = mapper.Map<Feedback>(request);
        feedback.CoachUserId = coachUserId;

        if (!string.IsNullOrEmpty(feedback.Content))
        {
            // Gate 1: Raw input size check BEFORE sanitization
            if (feedback.Content.Length > 100_000)
                throw new BadRequestException("Feedback content is too large", ErrorCodeEnum.ValidationError);

            // Sanitize HTML to prevent stored XSS
            feedback.Content = _htmlSanitizer.Sanitize(feedback.Content);

            // Gate 2: Post-sanitization length check
            if (feedback.Content.Length > 50_000)
                throw new BadRequestException("Feedback content exceeds maximum length of 50,000 characters", ErrorCodeEnum.ValidationError);

            feedback.ContentPlainText = StripHtml(feedback.Content);
            // Phase A: Keep Comment in sync for backward compat / rollback
            feedback.Comment = feedback.ContentPlainText;
        }

        feedbackRepository.Add(feedback);
        await feedbackRepository.SaveChangesAsync();

        if (request.ImprovementPoints != null)
        {
            var order = 1;
            foreach (var pointDto in request.ImprovementPoints)
            {
                await AddImprovementPointInternal(feedback.Id, pointDto, order++);
            }
        }

        if (request.Praise != null)
        {
            var praise = mapper.Map<Praise>(request.Praise);
            praise.FeedbackId = feedback.Id;
            praiseRepository.Add(praise);
            await praiseRepository.SaveChangesAsync();
        }

        if (request.Attachments != null)
        {
            var mediaOrder = 0;
            foreach (var mediaDto in request.Attachments)
            {
                var media = mapper.Map<FeedbackMedia>(mediaDto);
                media.FeedbackId = feedback.Id;
                media.Order = mediaOrder++;
                feedbackMediaRepository.Add(media);
            }
            await feedbackMediaRepository.SaveChangesAsync();
        }

        if (feedback.SharedWithPlayer)
        {
            await PublishFeedbackSharedAsync(
                feedback,
                BuildPreview(feedback.ContentPlainText, request.Praise?.Message, request.ImprovementPoints?.Count ?? 0));
            await feedbackRepository.SaveChangesAsync();
        }

        return await GetByIdAsync(feedback.Id, coachUserId) ?? throw new Exception("Failed to retrieve created feedback");
    }

    public async Task<FeedbackDto?> GetByIdAsync(Guid id, Guid requestingUserId)
    {
        var feedback = await feedbackRepository.GetByIdWithDetailsAsync(id);
        if (feedback == null) return null;

        if (feedback.CoachUserId != requestingUserId &&
            (feedback.RecipientUserId != requestingUserId || !feedback.SharedWithPlayer))
        {
            return null;
        }

        var dto = mapper.Map<FeedbackDto>(feedback);
        await EnrichAsync(dto);
        return dto;
    }

    public async Task<FeedbackDto> UpdateAsync(Guid id, UpdateFeedbackDto request, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(id);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can update this feedback");

        // A kept attachment's url is never read (StageAttachmentsAsync keeps the stored one), so
        // only the attachments this edit adds are judged.
        LinkUrl.EnsureHttp(AttachmentLinksOf(request.Attachments)
            .Where((_, i) => request.Attachments![i].Id is null));

        // Handle content update from either Content or Comment field (Phase A compat)
        var newContent = request.Content ?? request.Comment;
        if (newContent != null)
        {
            // Gate 1: Raw input size check before sanitization
            if (newContent.Length > 100_000)
                throw new BadRequestException("Feedback content is too large", ErrorCodeEnum.ValidationError);

            feedback.Content = _htmlSanitizer.Sanitize(newContent);

            // Gate 2: Post-sanitization length check
            if (feedback.Content.Length > 50_000)
                throw new BadRequestException("Feedback content exceeds maximum length of 50,000 characters", ErrorCodeEnum.ValidationError);

            feedback.ContentPlainText = StripHtml(feedback.Content);
            // Phase A: Keep Comment in sync
            feedback.Comment = feedback.ContentPlainText;
        }

        var wasShared = feedback.SharedWithPlayer;
        if (request.SharedWithPlayer.HasValue) feedback.SharedWithPlayer = request.SharedWithPlayer.Value;

        feedbackRepository.Update(feedback);

        if (request.Attachments != null)
            await StageAttachmentsAsync(feedback.Id, request.Attachments);

        // Publishing before the save is what puts the message in the transactional outbox;
        // a Publish after the last SaveChangesAsync is silently dropped.
        if (feedback.SharedWithPlayer)
        {
            var preview = BuildPreview(feedback.ContentPlainText, feedback.Praise?.Message, feedback.ImprovementPoints?.Count ?? 0);
            if (wasShared)
                await PublishFeedbackUpdatedAsync(feedback, preview);
            else
                await PublishFeedbackSharedAsync(feedback, preview);
        }

        await feedbackRepository.SaveChangesAsync();

        return await GetByIdAsync(id, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    /// <summary>
    /// Brings the feedback's own attachments to the list an edit sends, staged for the caller's
    /// single save so the note, the share flag and the attachments land together or not at all.
    /// </summary>
    private async Task StageAttachmentsAsync(Guid feedbackId, IReadOnlyList<UpdateFeedbackMediaDto> attachments)
    {
        var current = await feedbackMediaRepository.Query()
            .Where(m => m.FeedbackId == feedbackId && !m.IsDeleted)
            .ToDictionaryAsync(m => m.Id);

        if (attachments.Any(a => a.Id is { } id && !current.ContainsKey(id)))
            throw new EntityNotFoundException("Attachment not found");

        var keptIds = attachments.Where(a => a.Id.HasValue).Select(a => a.Id!.Value).ToHashSet();
        foreach (var removed in current.Values.Where(m => !keptIds.Contains(m.Id)))
            removed.IsDeleted = true;

        // A kept row keeps its stored Url whatever the entry says: reads hand out presigned URLs.
        // A new row maps like a created one, so a presigned URL is stored bare there too.
        // New rows go through Add, not through the tracked parent's collection — BaseEntity sets
        // every Id at construction, and a keyed child found only by navigation saves as an UPDATE.
        for (var order = 0; order < attachments.Count; order++)
        {
            var entry = attachments[order];
            if (entry.Id is { } id)
            {
                var kept = current[id];
                kept.Title = entry.Title;
                kept.Type = entry.Type;
                kept.Order = order;
                continue;
            }

            var added = mapper.Map<CreateFeedbackMediaDto, FeedbackMedia>(entry);
            added.FeedbackId = feedbackId;
            added.Order = order;
            feedbackMediaRepository.Add(added);
        }
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(id);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can delete this feedback");

        feedback.IsDeleted = true;
        feedbackRepository.Update(feedback);
        await feedbackRepository.SaveChangesAsync();
    }

    public async Task<IEnumerable<FeedbackDto>> GetByEventIdAsync(Guid eventId, Guid requestingUserId)
    {
        var feedbacks = await feedbackRepository.GetByEventIdAsync(eventId);

        var accessible = feedbacks.Where(f =>
            f.CoachUserId == requestingUserId ||
            (f.RecipientUserId == requestingUserId && f.SharedWithPlayer));

        var items = mapper.Map<List<FeedbackDto>>(accessible);
        await EnrichAsync(items);
        return items;
    }

    public async Task<FeedbackListResponseDto> GetReceivedFeedbackAsync(Guid userId, int page = 1, int pageSize = 20)
    {
        var feedbacks = await feedbackRepository.GetByRecipientIdAsync(userId, page, pageSize);
        var total = await feedbackRepository.Query()
            .CountAsync(f => f.RecipientUserId == userId && f.SharedWithPlayer && !f.IsDeleted);

        var items = mapper.Map<List<FeedbackDto>>(feedbacks);
        await EnrichAsync(items);

        return new FeedbackListResponseDto
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<FeedbackListResponseDto> GetGivenFeedbackAsync(Guid userId, int page = 1, int pageSize = 20)
    {
        var feedbacks = await feedbackRepository.GetByCoachIdAsync(userId, page, pageSize);
        var total = await feedbackRepository.Query()
            .CountAsync(f => f.CoachUserId == userId && !f.IsDeleted);

        var items = mapper.Map<List<FeedbackDto>>(feedbacks);
        await EnrichAsync(items);

        return new FeedbackListResponseDto
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<FeedbackDto> ShareWithPlayerAsync(Guid id, bool share, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdWithDetailsAsync(id);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can share this feedback");

        var wasShared = feedback.SharedWithPlayer;
        feedback.SharedWithPlayer = share;
        feedbackRepository.Update(feedback);

        // Notify the player only on the private → shared transition (no re-notify on re-share).
        if (!wasShared && share)
        {
            await PublishFeedbackSharedAsync(
                feedback,
                BuildPreview(feedback.ContentPlainText, feedback.Praise?.Message, feedback.ImprovementPoints.Count));
        }

        await feedbackRepository.SaveChangesAsync();

        return await GetByIdAsync(id, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task MarkSeenAsync(Guid id, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(id);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.RecipientUserId != userId || !feedback.SharedWithPlayer)
            throw new ForbiddenException("Only the recipient can mark shared feedback as seen");

        // First-seen wins for the receipt; LastSeenAt moves with every open.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        feedback.SeenAt ??= now;
        feedback.LastSeenAt = now;
        feedbackRepository.Update(feedback);
        await feedbackRepository.SaveChangesAsync();
    }

    public Task<int> GetUnseenCountAsync(Guid userId)
        => feedbackRepository.GetUnseenCountAsync(userId);

    public async Task<FeedbackDto> AddImprovementPointAsync(Guid feedbackId, AddImprovementPointDto request, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdWithDetailsAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        LinkUrl.EnsureHttp(PointLinksOf(request.MediaLinks));

        var maxOrder = feedback.ImprovementPoints.Any() ? feedback.ImprovementPoints.Max(p => p.Order) : 0;
        var order = request.Order ?? maxOrder + 1;

        await AddImprovementPointInternal(feedbackId, new CreateImprovementPointDto
        {
            Description = request.Description,
            DrillIds = request.DrillIds,
            MediaLinks = request.MediaLinks
        }, order);

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> UpdateImprovementPointAsync(Guid feedbackId, Guid pointId, UpdateImprovementPointDto request, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        var point = await PointOnFeedbackAsync(feedbackId, pointId);

        if (request.Description != null) point.Description = request.Description;

        pointRepository.Update(point);
        await pointRepository.SaveChangesAsync();

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> RemoveImprovementPointAsync(Guid feedbackId, Guid pointId, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        var point = await PointOnFeedbackAsync(feedbackId, pointId);

        point.IsDeleted = true;
        pointRepository.Update(point);
        await pointRepository.SaveChangesAsync();

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> AddDrillToPointAsync(Guid feedbackId, Guid pointId, Guid drillId, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        await PointOnFeedbackAsync(feedbackId, pointId);

        // Validate drill exists locally (both in coaching-service now)
        var drill = await drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        // (ImprovementPointId, DrillId) is uniquely indexed and removal only soft-deletes, so the
        // slot survives a detach — inserting a second row throws 23505. Revive the existing link
        // instead, which also makes re-attaching an already-linked drill a no-op rather than a 500.
        var link = await drillLinkRepository.Query()
            .FirstOrDefaultAsync(l => l.ImprovementPointId == pointId && l.DrillId == drillId);

        if (link == null)
        {
            drillLinkRepository.Add(new ImprovementPointDrill
            {
                ImprovementPointId = pointId,
                DrillId = drillId
            });
            await drillLinkRepository.SaveChangesAsync();
        }
        else if (link.IsDeleted)
        {
            link.IsDeleted = false;
            drillLinkRepository.Update(link);
            await drillLinkRepository.SaveChangesAsync();
        }

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> RemoveDrillFromPointAsync(Guid feedbackId, Guid pointId, Guid drillId, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        await PointOnFeedbackAsync(feedbackId, pointId);

        var link = await drillLinkRepository.Query()
            .FirstOrDefaultAsync(l => l.ImprovementPointId == pointId && l.DrillId == drillId);

        if (link != null)
        {
            link.IsDeleted = true;
            drillLinkRepository.Update(link);
            await drillLinkRepository.SaveChangesAsync();
        }

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> AddMediaToPointAsync(Guid feedbackId, Guid pointId, CreateImprovementPointMediaDto request, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        await PointOnFeedbackAsync(feedbackId, pointId);
        LinkUrl.EnsureHttp([("url", request.Url)]);

        var media = mapper.Map<ImprovementPointMedia>(request);
        media.ImprovementPointId = pointId;

        mediaRepository.Add(media);
        await mediaRepository.SaveChangesAsync();

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> RemoveMediaFromPointAsync(Guid feedbackId, Guid pointId, Guid mediaId, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        await PointOnFeedbackAsync(feedbackId, pointId);

        var media = await mediaRepository.GetByIdAsync(mediaId);
        if (media == null || media.ImprovementPointId != pointId)
            throw new EntityNotFoundException("Media not found");

        media.IsDeleted = true;
        mediaRepository.Update(media);
        await mediaRepository.SaveChangesAsync();

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> AddPraiseAsync(Guid feedbackId, CreatePraiseDto request, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdWithDetailsAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        if (feedback.Praise != null)
            throw new ConflictException("Feedback already has praise. Update or remove it first.");

        var praise = mapper.Map<Praise>(request);
        praise.FeedbackId = feedbackId;

        praiseRepository.Add(praise);
        await praiseRepository.SaveChangesAsync();

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> UpdatePraiseAsync(Guid feedbackId, UpdatePraiseDto request, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdWithDetailsAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        if (feedback.Praise == null)
            throw new EntityNotFoundException("Feedback has no praise");

        if (request.Message != null) feedback.Praise.Message = request.Message;
        if (request.BadgeType.HasValue) feedback.Praise.BadgeType = request.BadgeType;

        praiseRepository.Update(feedback.Praise);
        await praiseRepository.SaveChangesAsync();

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    public async Task<FeedbackDto> RemovePraiseAsync(Guid feedbackId, Guid userId)
    {
        var feedback = await feedbackRepository.GetByIdWithDetailsAsync(feedbackId);
        if (feedback == null)
            throw new EntityNotFoundException("Feedback not found");

        if (feedback.CoachUserId != userId)
            throw new ForbiddenException("Only the coach can modify this feedback");

        if (feedback.Praise != null)
        {
            feedback.Praise.IsDeleted = true;
            praiseRepository.Update(feedback.Praise);
            await praiseRepository.SaveChangesAsync();
        }

        return await GetByIdAsync(feedbackId, userId) ?? throw new Exception("Failed to retrieve feedback");
    }

    private async Task AddImprovementPointInternal(Guid feedbackId, CreateImprovementPointDto request, int order)
    {
        var point = mapper.Map<ImprovementPoint>(request);
        point.FeedbackId = feedbackId;
        point.Order = order;

        pointRepository.Add(point);
        await pointRepository.SaveChangesAsync();

        if (request.DrillIds != null)
        {
            foreach (var drillId in request.DrillIds)
            {
                var link = new ImprovementPointDrill
                {
                    ImprovementPointId = point.Id,
                    DrillId = drillId
                };
                drillLinkRepository.Add(link);
            }
            await drillLinkRepository.SaveChangesAsync();
        }

        if (request.MediaLinks != null)
        {
            foreach (var mediaDto in request.MediaLinks)
            {
                var media = mapper.Map<ImprovementPointMedia>(mediaDto);
                media.ImprovementPointId = point.Id;
                mediaRepository.Add(media);
            }
            await mediaRepository.SaveChangesAsync();
        }
    }

    /// <summary>
    /// The point a request names, which must be a live one on the feedback it names: the caller was
    /// authorized against that feedback, so a point from another would be changed on its authority.
    /// </summary>
    private async Task<ImprovementPoint> PointOnFeedbackAsync(Guid feedbackId, Guid pointId)
    {
        var point = await pointRepository.GetByIdAsync(pointId);
        if (point == null || point.IsDeleted || point.FeedbackId != feedbackId)
            throw new EntityNotFoundException("Improvement point not found");
        return point;
    }

    /// <summary>A feedback's own attachments, each named as the request carries it.</summary>
    private static IEnumerable<(string Field, string? Url)> AttachmentLinksOf(IEnumerable<CreateFeedbackMediaDto>? attachments) =>
        (attachments ?? []).Select((media, i) => ($"attachments[{i}].url", (string?)media.Url));

    /// <summary>One point's media, named under the point when the request nests it in one.</summary>
    private static IEnumerable<(string Field, string? Url)> PointLinksOf(
        IEnumerable<CreateImprovementPointMediaDto>? mediaLinks, string prefix = "") =>
        (mediaLinks ?? []).Select((media, i) => ($"{prefix}mediaLinks[{i}].url", (string?)media.Url));

    private async Task EnrichWithProfilesAsync(IEnumerable<FeedbackDto> feedbacks)
    {
        var userIds = feedbacks
            .SelectMany(f => new[] { f.RecipientUserId, f.CoachUserId })
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (userIds.Count == 0) return;

        var profiles = await userProfileRepository.Query()
            .Where(p => userIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Surname, p.ImageUrl })
            .ToDictionaryAsync(p => p.Id, p => p);

        foreach (var feedback in feedbacks)
        {
            if (profiles.TryGetValue(feedback.RecipientUserId, out var recipient))
            {
                feedback.RecipientName = $"{recipient.Name ?? ""} {recipient.Surname ?? ""}".Trim();
                feedback.RecipientImageUrl = recipient.ImageUrl;
            }
            if (profiles.TryGetValue(feedback.CoachUserId, out var coach))
            {
                feedback.CoachName = $"{coach.Name ?? ""} {coach.Surname ?? ""}".Trim();
                feedback.CoachImageUrl = coach.ImageUrl;
            }
        }
    }

    /// <summary>
    /// Names and pictures come from the profile rows this service mirrors; the session each row
    /// was given at comes from events-service, asked once for the whole page. Neither answer
    /// depends on the other.
    /// </summary>
    private Task EnrichAsync(IReadOnlyCollection<FeedbackDto> feedbacks) =>
        Task.WhenAll(EnrichWithProfilesAsync(feedbacks), EnrichWithEventsAsync(feedbacks));

    private Task EnrichAsync(FeedbackDto feedback) => EnrichAsync([feedback]);

    private async Task EnrichWithEventsAsync(IReadOnlyCollection<FeedbackDto> feedbacks)
    {
        var eventIds = feedbacks
            .Where(f => f.EventId is { } id && id != Guid.Empty)
            .Select(f => f.EventId!.Value)
            .Distinct()
            .ToList();

        if (eventIds.Count == 0) return;

        var summaries = await eventsClient.GetEventInfoAsync(eventIds);

        foreach (var feedback in feedbacks)
        {
            if (feedback.EventId is { } eventId && summaries.TryGetValue(eventId, out var summary))
            {
                feedback.Event = new FeedbackEventDto
                {
                    Id = summary.Id,
                    Name = summary.Name,
                    StartTime = summary.StartTime,
                    Type = summary.Type
                };
            }
        }
    }

    private async Task PublishFeedbackSharedAsync(Feedback feedback, string? preview)
    {
        await publishEndpoint.Publish(new FeedbackSharedEvent
        {
            UserId = feedback.RecipientUserId,
            FeedbackId = feedback.Id,
            CoachUserId = feedback.CoachUserId,
            CoachName = await ResolveCoachNameAsync(feedback.CoachUserId),
            Preview = preview,
        });
    }

    private async Task PublishFeedbackUpdatedAsync(Feedback feedback, string? preview)
    {
        await publishEndpoint.Publish(new FeedbackUpdatedEvent
        {
            UserId = feedback.RecipientUserId,
            FeedbackId = feedback.Id,
            CoachUserId = feedback.CoachUserId,
            CoachName = await ResolveCoachNameAsync(feedback.CoachUserId),
            Preview = preview,
        });
    }

    private async Task<string> ResolveCoachNameAsync(Guid coachUserId)
    {
        var profile = await userProfileRepository.Query()
            .Where(u => u.Id == coachUserId)
            .Select(u => new { u.Name, u.Surname })
            .FirstOrDefaultAsync();

        var name = profile == null ? "" : $"{profile.Name ?? ""} {profile.Surname ?? ""}".Trim();
        return string.IsNullOrWhiteSpace(name) ? "Your coach" : name;
    }

    private const int PreviewMaxLength = 140;

    private static string? BuildPreview(string? note, string? praiseMessage, int pointCount)
    {
        var text = !string.IsNullOrWhiteSpace(praiseMessage) ? praiseMessage
            : !string.IsNullOrWhiteSpace(note) ? note
            : pointCount > 0 ? $"{pointCount} thing{(pointCount == 1 ? "" : "s")} to work on"
            : null;

        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.Length > PreviewMaxLength ? text[..PreviewMaxLength] : text;
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var doc = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var text = doc.Body?.TextContent ?? "";
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 4000 ? text[..4000] : text;
    }
}
