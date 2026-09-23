using System.Text.Json;
using AutoMapper;
using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.RichText;
using Coaching.Application.Validation;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Enums;
using Shared.Exceptions;
using Shared.DTOs;
using Shared.Options;
using Shared.Services.Analytics;
using Shared.Services.FileStorage.Intefaces;

namespace Coaching.Application.Services;

public class DrillService : IDrillService
{
    private readonly IDrillRepository _drillRepository;
    private readonly IDrillLikeRepository _likeRepository;
    private readonly IDrillBookmarkRepository _bookmarkRepository;
    private readonly IDrillCommentRepository _commentRepository;
    private readonly IDrillAttachmentRepository _attachmentRepository;
    private readonly IClubsGrpcClient _clubsClient;
    private readonly IFileService _fileService;
    private readonly S3Settings _s3Settings;
    private readonly IMapper _mapper;
    private readonly ILogger<DrillService> _logger;
    private readonly IDrillDialReconciler _dialReconciler;
    private readonly IAnalyticsCapture _analytics;

    /// <summary>
    /// The most rows one import request may carry. Public because the ceiling is part of the
    /// contract: a client with a larger spreadsheet has to split it into batches of this size.
    /// </summary>
    public const int MaxImportRows = 500;

    public DrillService(
        IDrillRepository drillRepository,
        IDrillLikeRepository likeRepository,
        IDrillBookmarkRepository bookmarkRepository,
        IDrillCommentRepository commentRepository,
        IDrillAttachmentRepository attachmentRepository,
        IClubsGrpcClient clubsClient,
        IFileService fileService,
        IOptions<S3Settings> s3Settings,
        IMapper mapper,
        ILogger<DrillService> logger,
        IDrillDialReconciler dialReconciler,
        IAnalyticsCapture analytics)
    {
        _drillRepository = drillRepository;
        _likeRepository = likeRepository;
        _bookmarkRepository = bookmarkRepository;
        _commentRepository = commentRepository;
        _attachmentRepository = attachmentRepository;
        _clubsClient = clubsClient;
        _fileService = fileService;
        _s3Settings = s3Settings.Value;
        _mapper = mapper;
        _logger = logger;
        _dialReconciler = dialReconciler;
        _analytics = analytics;
    }

    public async Task<PagedResponse<DrillDto>> GetByFilterAsync(DrillFilterRequest filter, Guid? userId = null)
    {
        var query = _drillRepository.Query();

        if (filter.Scope.HasValue && userId.HasValue)
        {
            var authorizedClubId = await ResolveAuthorizedClubIdAsync(filter.ClubId, userId.Value);
            query = query.ApplyScope(filter.Scope.Value, userId.Value, authorizedClubId);
        }
        else
        {
            // Legacy public-only listing (web + anonymous), with optional author/club narrowing.
            query = query.Where(d => d.Visibility == DrillVisibility.Public);

            if (filter.CreatedByUserId.HasValue)
                query = query.Where(d => d.CreatedByUserId == filter.CreatedByUserId.Value);

            if (filter.ClubId.HasValue)
                query = query.Where(d => d.ClubId == filter.ClubId.Value);
        }

        query = query.ApplyAttributeFilters(filter);

        // Get total count before pagination
        var totalCount = await query.CountAsync();

        // Apply sorting
        var sortBy = filter.SortBy?.ToLower() ?? "likecount";
        var sortOrder = filter.SortOrder?.ToLower() ?? "desc";

        query = sortBy switch
        {
            "name" => sortOrder == "desc" ? query.OrderByDescending(d => d.Name) : query.OrderBy(d => d.Name),
            "createdat" => sortOrder == "desc" ? query.OrderByDescending(d => d.CreatedAt) : query.OrderBy(d => d.CreatedAt),
            "duration" => sortOrder == "desc" ? query.OrderByDescending(d => d.Duration) : query.OrderBy(d => d.Duration),
            _ => sortOrder == "desc" ? query.OrderByDescending(d => d.LikeCount) : query.OrderBy(d => d.LikeCount)
        };

        // Apply pagination
        var skip = (filter.Page - 1) * filter.Limit;
        query = query.Skip(skip).Take(filter.Limit);

        var drills = await query
            .Include(d => d.Attachments.OrderBy(a => a.Order))
            .Include(d => d.Equipment.OrderBy(e => e.Order))
            .Include(d => d.Dials.OrderBy(dial => dial.Order))
            .Include(d => d.Creator)
            .ToListAsync();

        var dtos = _mapper.Map<IEnumerable<DrillDto>>(drills);
        await EnrichWithClubInfoAsync(dtos);
        await EnrichWithUserInteractionsAsync(dtos, userId);
        return PagedResponse<DrillDto>.Create(dtos, totalCount, filter.Page, filter.Limit);
    }

    public async Task<DrillDto?> GetByIdAsync(Guid id, Guid? userId = null)
    {
        var drill = await _drillRepository.GetByIdWithDetailsAsync(id);
        if (drill == null) return null;

        // Check visibility
        if (drill.Visibility == DrillVisibility.Private)
        {
            if (!userId.HasValue)
                throw new ForbiddenException("This drill is private");

            var canRead = drill.CreatedByUserId == userId.Value;
            if (!canRead && drill.ClubId.HasValue)
                canRead = await _clubsClient.IsUserClubMemberAsync(userId.Value, drill.ClubId.Value);

            if (!canRead)
            {
                throw new ForbiddenException("This drill is private");
            }
        }

        var dto = _mapper.Map<DrillDto>(drill);
        await EnrichWithClubInfoAsync([dto]);
        await EnrichWithUserInteractionsAsync([dto], userId);
        return dto;
    }

    public async Task<DrillDto> CreateAsync(CreateDrillDto request, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BadRequestException("Name is required", ErrorCodeEnum.ValidationError);

        EnsureVideoUrlIsHttp(request.VideoUrl);

        if (request.ClubId.HasValue)
            await EnsureCanManageClubDrillsAsync(request.ClubId.Value, userId);

        // Validate variation drill IDs exist
        var variationDrillIds = request.Variations?.Select(v => v.DrillId).Distinct().ToList() ?? [];
        if (variationDrillIds.Count > 0)
        {
            var existingDrills = await _drillRepository.Query()
                .Where(d => variationDrillIds.Contains(d.Id))
                .Select(d => d.Id)
                .ToListAsync();

            var missingIds = variationDrillIds.Except(existingDrills).ToList();
            if (missingIds.Count > 0)
                throw new BadRequestException($"Variation drill(s) not found: {string.Join(", ", missingIds)}", ErrorCodeEnum.EntityNotFound);
        }

        var instructions = DrillRichText.Resolve(request.InstructionsHtml, request.Instructions, ordered: true);
        var coachingPoints = DrillRichText.Resolve(request.CoachingPointsHtml, request.CoachingPoints, ordered: false);

        var drill = new Drill
        {
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Intensity = request.Intensity,
            Visibility = request.Visibility,
            Skills = request.Skills ?? [],
            Duration = request.Duration,
            MinPlayers = request.MinPlayers,
            MaxPlayers = request.MaxPlayers,
            InstructionsHtml = instructions.Html,
            Instructions = instructions.Lines,
            CoachingPointsHtml = coachingPoints.Html,
            CoachingPoints = coachingPoints.Lines,
            VideoUrl = request.VideoUrl,
            CreatedByUserId = userId,
            ClubId = request.ClubId,
            LikeCount = 0
        };

        // Add equipment
        if (request.Equipment != null)
        {
            for (int i = 0; i < request.Equipment.Length; i++)
            {
                var equipmentInput = request.Equipment[i];
                drill.Equipment.Add(new DrillEquipment
                {
                    DrillId = drill.Id,
                    Name = equipmentInput.Name,
                    IsOptional = equipmentInput.IsOptional,
                    Order = i
                });
            }
        }

        // Add variations
        if (request.Variations != null)
        {
            for (int i = 0; i < request.Variations.Length; i++)
            {
                var variationInput = request.Variations[i];
                drill.Variations.Add(new DrillVariation
                {
                    SourceDrillId = drill.Id,
                    TargetDrillId = variationInput.DrillId,
                    Note = variationInput.Note,
                    Order = i
                });
            }
        }

        _drillRepository.Add(drill);

        if (request.Dials is not null)
            await _dialReconciler.ReconcileAsync(drill, request.Dials);

        await _drillRepository.SaveChangesAsync();

        // Re-fetch with details for proper mapping
        var createdDrill = await _drillRepository.GetByIdWithDetailsAsync(drill.Id);
        var dto = _mapper.Map<DrillDto>(createdDrill);
        await EnrichWithClubInfoAsync([dto]);

        _analytics.Capture(userId, AnalyticsEventNames.DrillCreated, new Dictionary<string, object?>
        {
            ["drill_id"] = dto.Id,
            ["club_id"] = dto.ClubId,
            ["visibility"] = dto.Visibility,
            ["category"] = dto.Category,
            ["has_video"] = !string.IsNullOrWhiteSpace(dto.VideoUrl),
            ["dial_count"] = dto.Dials.Count
        });

        return dto;
    }

    /// <summary>
    /// Create many drills from a parsed spreadsheet. Rows that fail validation are reported back
    /// against their own line number rather than failing the batch, so one bad cell in a hundred
    /// rows costs the coach one correction instead of the whole import.
    /// </summary>
    public async Task<ImportDrillsResultDto> ImportAsync(ImportDrillsDto request, Guid userId)
    {
        if (request.Drills is null || request.Drills.Count == 0)
            throw new BadRequestException("No drills to import", ErrorCodeEnum.ValidationError);

        if (request.Drills.Count > MaxImportRows)
            throw new BadRequestException(
                $"An import carries at most {MaxImportRows} drills; this one has {request.Drills.Count}",
                ErrorCodeEnum.ValidationError);

        // The destination is chosen once for the batch, so the club is authorized once too.
        if (request.ClubId.HasValue)
            await EnsureCanManageClubDrillsAsync(request.ClubId.Value, userId);

        var results = new List<ImportDrillResultDto>(request.Drills.Count);
        var toCreate = new List<Drill>();

        foreach (var row in request.Drills)
        {
            var error = ValidateImportRow(row);
            if (error is not null)
            {
                results.Add(new ImportDrillResultDto(row.RowNumber, row.Name, null, error));
                continue;
            }

            var drill = BuildImportedDrill(row, request, userId);
            toCreate.Add(drill);
            results.Add(new ImportDrillResultDto(row.RowNumber, drill.Name, drill.Id, null));
        }

        if (toCreate.Count > 0)
        {
            _drillRepository.AddRange(toCreate);
            await _drillRepository.SaveChangesAsync();
        }

        var failed = results.Count - toCreate.Count;

        // One event for the batch, not one per drill: the coach made a single decision, and a
        // row each would drown the drills written by hand.
        _analytics.Capture(userId, AnalyticsEventNames.DrillImported, new Dictionary<string, object?>
        {
            ["row_count"] = results.Count,
            ["imported_count"] = toCreate.Count,
            ["failed_count"] = failed,
            ["club_id"] = request.ClubId,
            ["visibility"] = request.Visibility
        });

        return new ImportDrillsResultDto(toCreate.Count, failed, results);
    }

    private static string? ValidateImportRow(ImportDrillRowDto row)
    {
        if (string.IsNullOrWhiteSpace(row.Name))
            return "Name is required";

        // The database's limits, not taste. One over-long cell threw out of the single save and
        // took every good row in the batch down with it.
        if (row.Name.Trim().Length > Drill.NameMaxLength)
            return $"Name is longer than {Drill.NameMaxLength} characters";

        if (row.VideoUrl?.Length > Drill.VideoUrlMaxLength)
            return $"Video link is longer than {Drill.VideoUrlMaxLength} characters";

        if (!string.IsNullOrWhiteSpace(row.VideoUrl) && !LinkUrl.IsHttp(row.VideoUrl))
            return "Video link must start with http:// or https://";

        if (row.Equipment?.Any(item => item.Name?.Length > DrillEquipment.NameMaxLength) == true)
            return $"Equipment name is longer than {DrillEquipment.NameMaxLength} characters";

        if (row.Duration is < 0)
            return "Duration cannot be negative";

        if (row.MinPlayers is < 0 || row.MaxPlayers is < 0)
            return "Player counts cannot be negative";

        if (row.MinPlayers.HasValue && row.MaxPlayers.HasValue && row.MinPlayers > row.MaxPlayers)
            return "Minimum players cannot exceed maximum players";

        return null;
    }

    private static Drill BuildImportedDrill(ImportDrillRowDto row, ImportDrillsDto request, Guid userId)
    {
        var instructions = DrillRichText.Resolve(null, row.Instructions, ordered: true);
        var coachingPoints = DrillRichText.Resolve(null, row.CoachingPoints, ordered: false);

        var drill = new Drill
        {
            Name = row.Name.Trim(),
            Description = row.Description,
            Category = row.Category,
            Intensity = row.Intensity,
            Visibility = request.Visibility,
            Skills = row.Skills ?? [],
            Duration = row.Duration,
            MinPlayers = row.MinPlayers,
            MaxPlayers = row.MaxPlayers,
            InstructionsHtml = instructions.Html,
            Instructions = instructions.Lines,
            CoachingPointsHtml = coachingPoints.Html,
            CoachingPoints = coachingPoints.Lines,
            VideoUrl = row.VideoUrl,
            CreatedByUserId = userId,
            ClubId = request.ClubId,
            LikeCount = 0
        };

        var equipment = row.Equipment ?? [];
        for (int i = 0; i < equipment.Length; i++)
        {
            drill.Equipment.Add(new DrillEquipment
            {
                DrillId = drill.Id,
                Name = equipment[i].Name,
                IsOptional = equipment[i].IsOptional,
                Order = i
            });
        }

        return drill;
    }

    public async Task<DrillDto> UpdateAsync(UpdateDrillDto request, Guid userId)
    {
        var drill = await _drillRepository.GetByIdWithDetailsAsync(request.Id);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        if (!CanModifyDrill(drill, userId))
            throw new ForbiddenException("Only the creator can update this drill");

        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BadRequestException("Name is required", ErrorCodeEnum.ValidationError);

        EnsureVideoUrlIsHttp(request.VideoUrl);

        // Updating a club drill affects the current club even when the request moves it out.
        // A move to another club affects both clubs, so authorization is required in each.
        var affectedClubIds = new[] { drill.ClubId, request.ClubId }
            .Where(clubId => clubId.HasValue)
            .Select(clubId => clubId!.Value)
            .Distinct();
        foreach (var clubId in affectedClubIds)
            await EnsureCanManageClubDrillsAsync(clubId, userId);

        // Validate variation drill IDs exist
        var variationDrillIds = request.Variations?.Select(v => v.DrillId).Distinct().ToList() ?? [];
        if (variationDrillIds.Count > 0)
        {
            // Prevent self-referencing
            if (variationDrillIds.Contains(request.Id))
                throw new BadRequestException("A drill cannot be a variation of itself", ErrorCodeEnum.ValidationError);

            var existingDrills = await _drillRepository.Query()
                .Where(d => variationDrillIds.Contains(d.Id))
                .Select(d => d.Id)
                .ToListAsync();

            var missingIds = variationDrillIds.Except(existingDrills).ToList();
            if (missingIds.Count > 0)
                throw new BadRequestException($"Variation drill(s) not found: {string.Join(", ", missingIds)}", ErrorCodeEnum.EntityNotFound);
        }

        drill.Name = request.Name;
        drill.Description = request.Description;
        drill.Category = request.Category;
        drill.Intensity = request.Intensity;
        drill.Visibility = request.Visibility;
        drill.Skills = request.Skills ?? [];
        drill.Duration = request.Duration;
        drill.MinPlayers = request.MinPlayers;
        drill.MaxPlayers = request.MaxPlayers;
        var instructions = DrillRichText.Resolve(request.InstructionsHtml, request.Instructions, ordered: true);
        var coachingPoints = DrillRichText.Resolve(request.CoachingPointsHtml, request.CoachingPoints, ordered: false);
        drill.InstructionsHtml = instructions.Html;
        drill.Instructions = instructions.Lines;
        drill.CoachingPointsHtml = coachingPoints.Html;
        drill.CoachingPoints = coachingPoints.Lines;
        drill.VideoUrl = request.VideoUrl;
        drill.ClubId = request.ClubId;
        drill.UpdatedAt = DateTime.UtcNow;

        // Update equipment - clear existing and add new
        drill.Equipment.Clear();
        if (request.Equipment != null)
        {
            for (int i = 0; i < request.Equipment.Length; i++)
            {
                var equipmentInput = request.Equipment[i];
                drill.Equipment.Add(new DrillEquipment
                {
                    // This entity is being attached to an already tracked aggregate. An empty
                    // generated key tells EF unequivocally that this is a new child row.
                    Id = Guid.Empty,
                    DrillId = drill.Id,
                    Name = equipmentInput.Name,
                    IsOptional = equipmentInput.IsOptional,
                    Order = i
                });
            }
        }

        // Update variations - clear existing and add new
        drill.Variations.Clear();
        if (request.Variations != null)
        {
            for (int i = 0; i < request.Variations.Length; i++)
            {
                var variationInput = request.Variations[i];
                drill.Variations.Add(new DrillVariation
                {
                    Id = Guid.Empty,
                    SourceDrillId = drill.Id,
                    TargetDrillId = variationInput.DrillId,
                    Note = variationInput.Note,
                    Order = i
                });
            }
        }

        // Null leaves the dials alone — a client that has never heard of dials cannot
        // strip them by omission.
        if (request.Dials is not null)
            await _dialReconciler.ReconcileAsync(drill, request.Dials);

        // GetByIdWithDetailsAsync returns a tracked aggregate. Saving it directly lets EF keep
        // replacement children as Added and removed children as Deleted. Calling Update on the
        // whole graph would mark the newly generated equipment/variation IDs as Modified and
        // issue UPDATE statements for rows that do not exist.
        await _drillRepository.SaveChangesAsync();

        // Re-fetch with details for proper mapping
        var updatedDrill = await _drillRepository.GetByIdWithDetailsAsync(drill.Id);
        var dto = _mapper.Map<DrillDto>(updatedDrill);
        await EnrichWithClubInfoAsync([dto]);

        _analytics.Capture(userId, AnalyticsEventNames.DrillUpdated, new Dictionary<string, object?>
        {
            ["drill_id"] = dto.Id,
            ["club_id"] = dto.ClubId,
            ["visibility"] = dto.Visibility,
            ["dial_count"] = dto.Dials.Count
        });

        return dto;
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(id);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        if (!CanModifyDrill(drill, userId))
            throw new ForbiddenException("Only the creator can delete this drill");

        _drillRepository.Delete(drill);
        await _drillRepository.SaveChangesAsync();
    }

    public async Task<IEnumerable<DrillDto>> GetCurrentUserDrillsAsync(Guid userId)
    {
        var drills = await _drillRepository.GetByCreatorAsync(userId);
        var dtos = _mapper.Map<IEnumerable<DrillDto>>(drills);
        await EnrichWithClubInfoAsync(dtos);
        await EnrichWithUserInteractionsAsync(dtos, userId);
        return dtos;
    }

    public async Task<IEnumerable<DrillDto>> GetClubDrillsAsync(Guid clubId, Guid userId)
    {
        if (!await _clubsClient.IsUserClubMemberAsync(userId, clubId))
            return [];

        var drills = await _drillRepository.GetByClubAsync(clubId);
        var dtos = _mapper.Map<IEnumerable<DrillDto>>(drills);
        await EnrichWithClubInfoAsync(dtos);
        await EnrichWithUserInteractionsAsync(dtos, userId);
        return dtos;
    }

    // A club's non-public drills must only surface for a request scoped to that club when the
    // requesting user is actually a member — mirrors the IsUserClubMemberAsync check
    // FeedbackAuthorizationService already uses for club-scoped authorization elsewhere in this
    // service. Returning null (rather than throwing) keeps ApplyScope's existing "no club
    // context" convention: a non-member sees the same empty club slice as a request with no
    // clubId at all, without failing the rest of a mixed-scope ("All") query.
    private async Task<Guid?> ResolveAuthorizedClubIdAsync(Guid? clubId, Guid userId)
    {
        if (!clubId.HasValue) return null;
        return await _clubsClient.IsUserClubMemberAsync(userId, clubId.Value) ? clubId : null;
    }

    private Task EnsureCanManageClubDrillsAsync(Guid clubId, Guid userId) =>
        DrillEditRules.EnsureCanManageClubDrillsAsync(clubId, userId, _clubsClient);

    /// <summary>A drill needs no video, so a missing or blank one is not judged.</summary>
    private static void EnsureVideoUrlIsHttp(string? videoUrl)
    {
        if (!string.IsNullOrWhiteSpace(videoUrl))
            LinkUrl.EnsureHttp([("videoUrl", videoUrl)]);
    }

    // =========================================================================
    // LIKES
    // =========================================================================

    public async Task<DrillLikeStatusDto> LikeDrillAsync(Guid drillId, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        var existingLike = await _likeRepository.GetByDrillAndUserAsync(drillId, userId);
        if (existingLike != null)
        {
            return new DrillLikeStatusDto
            {
                IsLiked = true,
                LikeCount = drill.LikeCount
            };
        }

        var like = new DrillLike
        {
            DrillId = drillId,
            UserId = userId
        };

        _likeRepository.Add(like);

        drill.LikeCount++;
        _drillRepository.Update(drill);

        await _drillRepository.SaveChangesAsync();

        _analytics.CaptureDrillSaved(drillId, userId, DrillSaveKind.Like, isOn: true);

        return new DrillLikeStatusDto
        {
            IsLiked = true,
            LikeCount = drill.LikeCount
        };
    }

    public async Task<DrillLikeStatusDto> UnlikeDrillAsync(Guid drillId, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        var existingLike = await _likeRepository.GetByDrillAndUserAsync(drillId, userId);
        if (existingLike == null)
        {
            return new DrillLikeStatusDto
            {
                IsLiked = false,
                LikeCount = drill.LikeCount
            };
        }

        _likeRepository.Delete(existingLike);

        drill.LikeCount = Math.Max(0, drill.LikeCount - 1);
        _drillRepository.Update(drill);

        await _drillRepository.SaveChangesAsync();

        _analytics.CaptureDrillSaved(drillId, userId, DrillSaveKind.Like, isOn: false);

        return new DrillLikeStatusDto
        {
            IsLiked = false,
            LikeCount = drill.LikeCount
        };
    }

    public async Task<DrillLikeStatusDto> GetLikeStatusAsync(Guid drillId, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        var existingLike = await _likeRepository.GetByDrillAndUserAsync(drillId, userId);

        return new DrillLikeStatusDto
        {
            IsLiked = existingLike != null,
            LikeCount = drill.LikeCount
        };
    }

    // =========================================================================
    // BOOKMARKS
    // =========================================================================

    public async Task<DrillBookmarkStatusDto> BookmarkDrillAsync(Guid drillId, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        var existingBookmark = await _bookmarkRepository.GetByDrillAndUserAsync(drillId, userId);
        if (existingBookmark != null)
        {
            return new DrillBookmarkStatusDto { IsBookmarked = true };
        }

        var bookmark = new DrillBookmark
        {
            DrillId = drillId,
            UserId = userId
        };

        _bookmarkRepository.Add(bookmark);
        await _bookmarkRepository.SaveChangesAsync();

        _analytics.CaptureDrillSaved(drillId, userId, DrillSaveKind.Bookmark, isOn: true);

        return new DrillBookmarkStatusDto { IsBookmarked = true };
    }

    public async Task<DrillBookmarkStatusDto> UnbookmarkDrillAsync(Guid drillId, Guid userId)
    {
        var existingBookmark = await _bookmarkRepository.GetByDrillAndUserAsync(drillId, userId);
        if (existingBookmark == null)
        {
            return new DrillBookmarkStatusDto { IsBookmarked = false };
        }

        _bookmarkRepository.Delete(existingBookmark);
        await _bookmarkRepository.SaveChangesAsync();

        _analytics.CaptureDrillSaved(drillId, userId, DrillSaveKind.Bookmark, isOn: false);

        return new DrillBookmarkStatusDto { IsBookmarked = false };
    }

    public async Task<IEnumerable<BookmarkedDrillDto>> GetUserBookmarksAsync(Guid userId)
    {
        var bookmarks = await _bookmarkRepository.GetByUserAsync(userId);

        return bookmarks.Select(b => new BookmarkedDrillDto
        {
            Id = b.Drill.Id,
            Name = b.Drill.Name,
            Category = b.Drill.Category,
            Intensity = b.Drill.Intensity,
            LikeCount = b.Drill.LikeCount,
            BookmarkedAt = b.CreatedAt ?? DateTime.UtcNow
        });
    }

    // =========================================================================
    // COMMENTS
    // =========================================================================

    public async Task<DrillCommentDto> CreateCommentAsync(Guid drillId, CreateDrillCommentDto request, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        if (string.IsNullOrWhiteSpace(request.Content))
            throw new BadRequestException("Comment content is required", ErrorCodeEnum.ValidationError);

        if (request.ParentCommentId.HasValue)
        {
            var parentComment = await _commentRepository.GetByIdAsync(request.ParentCommentId.Value);
            if (parentComment == null || parentComment.DrillId != drillId)
                throw new BadRequestException("Parent comment not found", ErrorCodeEnum.EntityNotFound);
        }

        var comment = new DrillComment
        {
            DrillId = drillId,
            UserId = userId,
            Content = request.Content,
            ParentCommentId = request.ParentCommentId
        };

        _commentRepository.Add(comment);
        await _commentRepository.SaveChangesAsync();

        var createdComment = await _commentRepository.GetByIdWithDetailsAsync(comment.Id);
        return _mapper.Map<DrillCommentDto>(createdComment);
    }

    public async Task<DrillCommentsResponseDto> GetCommentsAsync(Guid drillId, Guid? cursor, int limit)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        var comments = (await _commentRepository.GetByDrillWithCursorAsync(drillId, cursor, limit)).ToList();
        var hasMore = comments.Count > limit;

        if (hasMore)
        {
            comments = comments.Take(limit).ToList();
        }

        var nextCursor = hasMore && comments.Count > 0 ? comments.Last().Id : (Guid?)null;

        return new DrillCommentsResponseDto
        {
            Items = _mapper.Map<ICollection<DrillCommentDto>>(comments),
            NextCursor = nextCursor,
            HasMore = hasMore
        };
    }

    public async Task DeleteCommentAsync(Guid drillId, Guid commentId, Guid userId)
    {
        var comment = await _commentRepository.GetByIdAsync(commentId);
        if (comment == null || comment.DrillId != drillId)
            throw new EntityNotFoundException("Comment not found");

        if (comment.UserId != userId)
            throw new ForbiddenException("Only the comment author can delete this comment");

        comment.IsDeleted = true;
        _commentRepository.Update(comment);
        await _commentRepository.SaveChangesAsync();
    }

    // =========================================================================
    // ATTACHMENTS
    // =========================================================================

    public async Task<DrillAttachmentUploadResponseDto> GetAttachmentUploadUrlAsync(
        Guid drillId, DrillAttachmentUploadRequestDto request, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        if (!CanModifyDrill(drill, userId))
            throw new ForbiddenException("Only the creator can add attachments");

        var fileId = Guid.NewGuid();
        var extension = Path.GetExtension(request.FileName);
        var s3Key = $"drills/{drillId}/{fileId}{extension}";

        var uploadUrl = await _fileService.GetPresignedUploadLink(s3Key, _s3Settings.Bucket, request.ContentType);
        var fileUrl = _fileService.GetPublicUrl(s3Key);

        return new DrillAttachmentUploadResponseDto
        {
            UploadUrl = uploadUrl,
            FileUrl = fileUrl
        };
    }

    public async Task<DrillAttachmentDto> AddAttachmentAsync(Guid drillId, CreateDrillAttachmentDto request, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        if (!CanModifyDrill(drill, userId))
            throw new ForbiddenException("Only the creator can add attachments");

        // An uploaded file's url and a pasted link both arrive here.
        LinkUrl.EnsureHttp([("fileUrl", request.FileUrl)]);

        var maxOrder = await _attachmentRepository.GetMaxOrderForDrillAsync(drillId);

        var attachment = new DrillAttachment
        {
            DrillId = drillId,
            FileName = request.FileName,
            FileUrl = request.FileUrl,
            FileType = request.FileType,
            FileSize = request.FileSize,
            Order = maxOrder + 1
        };

        _attachmentRepository.Add(attachment);
        await _attachmentRepository.SaveChangesAsync();

        return _mapper.Map<DrillAttachmentDto>(attachment);
    }

    public async Task DeleteAttachmentAsync(Guid drillId, Guid attachmentId, Guid userId)
    {
        var drill = await _drillRepository.GetByIdAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        if (!CanModifyDrill(drill, userId))
            throw new ForbiddenException("Only the creator can delete attachments");

        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId);
        if (attachment == null || attachment.DrillId != drillId)
            throw new EntityNotFoundException("Attachment not found");

        _attachmentRepository.Delete(attachment);
        await _attachmentRepository.SaveChangesAsync();
    }

    // =========================================================================
    // ANIMATION
    // =========================================================================

    public async Task<DrillDto> UpdateAnimationsAsync(Guid drillId, UpdateDrillAnimationsDto request, Guid userId)
    {
        var drill = await _drillRepository.GetByIdWithDetailsAsync(drillId);
        if (drill == null)
            throw new EntityNotFoundException("Drill not found");

        if (!CanModifyDrill(drill, userId))
            throw new ForbiddenException("Only the creator can update the animations");

        drill.Animations = request.Animations.Count > 0
            ? JsonSerializer.Serialize(request.Animations, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            : null;
        drill.UpdatedAt = DateTime.UtcNow;

        _drillRepository.Update(drill);
        await _drillRepository.SaveChangesAsync();

        var updatedDrill = await _drillRepository.GetByIdWithDetailsAsync(drill.Id);
        var dto = _mapper.Map<DrillDto>(updatedDrill);
        await EnrichWithClubInfoAsync([dto]);
        return dto;
    }

    private bool CanModifyDrill(Drill drill, Guid userId) => DrillEditRules.IsCreator(drill, userId);

    private async Task EnrichWithUserInteractionsAsync(IEnumerable<DrillDto> drills, Guid? userId)
    {
        var drillList = drills.ToList();
        if (drillList.Count == 0) return;

        var drillIds = drillList.Select(d => d.Id).ToList();

        var bookmarkCounts = await _bookmarkRepository.GetBookmarkCountsAsync(drillIds);
        foreach (var drill in drillList)
        {
            drill.BookmarkCount = bookmarkCounts.TryGetValue(drill.Id, out var count) ? count : 0;
        }

        if (!userId.HasValue) return;

        var userLikedDrillIds = await _likeRepository.GetUserLikedDrillIdsAsync(userId.Value, drillIds);
        var likedSet = userLikedDrillIds.ToHashSet();

        var userBookmarkedDrillIds = await _bookmarkRepository.GetUserBookmarkedDrillIdsAsync(userId.Value, drillIds);
        var bookmarkedSet = userBookmarkedDrillIds.ToHashSet();

        foreach (var drill in drillList)
        {
            drill.IsLiked = likedSet.Contains(drill.Id);
            drill.IsBookmarked = bookmarkedSet.Contains(drill.Id);
        }
    }

    private async Task EnrichWithClubInfoAsync(IEnumerable<DrillDto> drills)
    {
        var drillList = drills.ToList();
        if (drillList.Count == 0) return;

        var clubIds = drillList
            .Where(d => d.ClubId.HasValue)
            .Select(d => d.ClubId!.Value)
            .Distinct()
            .ToList();

        if (clubIds.Count == 0) return;

        try
        {
            var clubInfos = await _clubsClient.GetClubInfoAsync(clubIds);

            foreach (var drill in drillList)
            {
                if (drill.ClubId.HasValue && clubInfos.TryGetValue(drill.ClubId.Value, out var clubInfo))
                {
                    drill.ClubName = clubInfo.Name;
                    drill.ClubLogoUrl = clubInfo.LogoUrl;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich drills with club info");
        }
    }

}
