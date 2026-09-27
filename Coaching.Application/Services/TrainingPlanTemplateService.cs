using AutoMapper;
using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.RichText;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.DTOs.Errors;
using Shared.Enums;
using Shared.Exceptions;
using Shared.Messaging.Contracts.Events.Coaching;
using Shared.Models;
using Shared.Services.Analytics;

namespace Coaching.Application.Services;

public class TrainingPlanService : ITrainingPlanService
{
    private readonly ITrainingPlanRepository _planRepository;
    private readonly IPlanSectionRepository _sectionRepository;
    private readonly IPlanItemRepository _itemRepository;
    private readonly IPlanLikeRepository _likeRepository;
    private readonly IPlanBookmarkRepository _bookmarkRepository;
    private readonly IPlanCommentRepository _commentRepository;
    private readonly IDrillRepository _drillRepository;
    private readonly IRepository<PlanItemDialValue> _dialValueRepository;
    private readonly IRepository<PlanStation> _stationRepository;
    private readonly IRepository<PlanStationItem> _stationItemRepository;
    private readonly IClubsGrpcClient _clubsClient;
    private readonly IEventsGrpcClient _eventsGrpcClient;
    private readonly IPlanCoachService _planCoachService;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly IMapper _mapper;
    private readonly ILogger<TrainingPlanService> _logger;
    private readonly IAnalyticsCapture _analytics;

    public TrainingPlanService(
        ITrainingPlanRepository planRepository,
        IPlanSectionRepository sectionRepository,
        IPlanItemRepository itemRepository,
        IPlanLikeRepository likeRepository,
        IPlanBookmarkRepository bookmarkRepository,
        IPlanCommentRepository commentRepository,
        IDrillRepository drillRepository,
        IRepository<PlanItemDialValue> dialValueRepository,
        IRepository<PlanStation> stationRepository,
        IRepository<PlanStationItem> stationItemRepository,
        IClubsGrpcClient clubsClient,
        IEventsGrpcClient eventsGrpcClient,
        IPlanCoachService planCoachService,
        IPublishEndpoint publishEndpoint,
        IMapper mapper,
        ILogger<TrainingPlanService> logger,
        IAnalyticsCapture analytics)
    {
        _planRepository = planRepository;
        _sectionRepository = sectionRepository;
        _itemRepository = itemRepository;
        _likeRepository = likeRepository;
        _bookmarkRepository = bookmarkRepository;
        _commentRepository = commentRepository;
        _drillRepository = drillRepository;
        _dialValueRepository = dialValueRepository;
        _stationRepository = stationRepository;
        _stationItemRepository = stationItemRepository;
        _clubsClient = clubsClient;
        _eventsGrpcClient = eventsGrpcClient;
        _planCoachService = planCoachService;
        _publishEndpoint = publishEndpoint;
        _mapper = mapper;
        _logger = logger;
        _analytics = analytics;
    }

    // =========================================================================
    // CRUD
    // =========================================================================

    public async Task<TrainingPlanDetailDto> CreateAsync(CreatePlanDto request, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BadRequestException("Plan name is required", ErrorCodeEnum.ValidationError);

        ValidatePlanFields(request.Name, request.Description, request.Sections, request.Items);
        // Before anything is written: a plan that fails on its third item must not leave a
        // half-built plan behind.
        ValidateItemShapes(request.Items);

        var plan = new TrainingPlan
        {
            Name = request.Name,
            Description = request.Description,
            CreatedByUserId = userId,
            ClubId = request.ClubId,
            Visibility = request.Visibility,
            Level = request.Level,
            TotalDuration = 0,
            CoachedDuration = 0,
            LikeCount = 0,
            UsageCount = 0
        };

        _planRepository.Add(plan);
        await _planRepository.SaveChangesAsync();

        // Add sections if provided
        if (request.Sections != null && request.Sections.Count > 0)
        {
            foreach (var sectionDto in request.Sections.OrderBy(s => s.Order))
            {
                var section = new PlanSection
                {
                    TemplateId = plan.Id,
                    Name = sectionDto.Name,
                    Order = sectionDto.Order
                };
                if (sectionDto.Id.HasValue)
                    section.Id = sectionDto.Id.Value;
                _sectionRepository.Add(section);
            }
            await _sectionRepository.SaveChangesAsync();
        }

        // Add items if provided
        if (request.Items != null && request.Items.Count > 0)
        {
            await ValidateItemDrillsAsync(request.Items);

            int order = 1;
            var dialValues = new List<PlanItemDialValue>();
            foreach (var itemDto in request.Items)
            {
                _itemRepository.Add(BuildItem(plan.Id, itemDto, order++, dialValues));
            }
            foreach (var value in dialValues)
                _dialValueRepository.Add(value);
            await _itemRepository.SaveChangesAsync();

            // Recalculate total duration
            await RecalculateTotalDurationAsync(plan.Id);
        }

        // Re-fetch with details
        var created = await _planRepository.GetByIdWithDetailsAsync(plan.Id);
        var dto = _mapper.Map<TrainingPlanDetailDto>(created);
        await AttachDialValuesAsync(dto);
        await EnrichWithClubInfoAsync([dto]);

        _analytics.CaptureTrainingPlanCreated(plan, userId, created?.Items.Count ?? 0);

        return dto;
    }

    public async Task<TrainingPlanDetailDto?> GetByIdAsync(Guid id, Guid? userId = null)
    {
        var plan = await _planRepository.GetByIdWithDetailsAsync(id);
        if (plan == null) return null;

        // Check visibility permissions
        await ValidatePlanAccessAsync(plan, userId);

        var dto = _mapper.Map<TrainingPlanDetailDto>(plan);
        await AttachDialValuesAsync(dto);
        await EnrichWithClubInfoAsync([dto]);
        await EnrichWithUserInteractionsAsync([dto], userId);
        await EnrichWithCoachNamesAsync(dto);
        return dto;
    }

    public async Task<TrainingPlanDetailDto> UpdateAsync(Guid id, UpdatePlanDto request, Guid userId)
    {
        ValidatePlanFields(request.Name, request.Description, request.Sections, request.Items);
        ValidateItemShapes(request.Items);

        var plan = await _planRepository.GetByIdWithDetailsAsync(id);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        // Update fields
        if (request.Name != null)
            plan.Name = request.Name;

        if (request.Description != null)
            plan.Description = request.Description;

        if (request.ClubId.HasValue)
            plan.ClubId = request.ClubId.Value;

        if (request.Visibility.HasValue)
            plan.Visibility = request.Visibility.Value;

        if (request.Level.HasValue)
            plan.Level = request.Level.Value;

        plan.UpdatedAt = DateTime.UtcNow;

        _planRepository.Update(plan);
        await _planRepository.SaveChangesAsync();

        // A save reconciles against what is already there rather than clearing it: a row resent
        // by id IS that row. Everything keyed to a row's id carries no foreign key back to it —
        // a station's coaches, a floor placement, a run's progress — so recreating the row left
        // all of it pointing at an id that no longer existed. Sections go first: an item may be
        // moving into one the same save creates, or out of one it deletes.
        if (request.Sections != null || request.Items != null)
        {
            await GuardPayloadIdsAsync(plan, request.Sections, request.Items);

            if (request.Sections != null)
                ReconcileSections(plan, request.Sections);

            if (request.Items != null)
            {
                await ValidateItemDrillsAsync(request.Items);
                await ReconcileItemsAsync(plan, request.Items);
            }

            await _itemRepository.SaveChangesAsync();

            await RecalculateTotalDurationAsync(plan.Id);
        }

        // Re-fetch with details
        var updated = await _planRepository.GetByIdWithDetailsAsync(plan.Id);
        var dto = _mapper.Map<TrainingPlanDetailDto>(updated);
        await AttachDialValuesAsync(dto);
        await EnrichWithClubInfoAsync([dto]);
        await EnrichWithCoachNamesAsync(dto);

        // Publish event for Instance plans so events-service can update its summary
        if (updated != null && updated.PlanType == PlanType.Instance)
            await PublishPlanUpdatedAsync(updated, "Updated");

        return dto;
    }

    public async Task DeleteAsync(Guid id, Guid userId)
    {
        var plan = await _planRepository.GetByIdWithDetailsAsync(id);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        // Capture plan data before deletion for event publishing
        var isInstance = plan.PlanType == PlanType.Instance;
        var eventId = plan.EventId;

        _planRepository.Delete(plan);
        await _planRepository.SaveChangesAsync();

        // Publish event for Instance plans so events-service clears its summary
        if (isInstance && eventId.HasValue)
        {
            await _publishEndpoint.Publish(new TrainingPlanUpdatedEvent
            {
                PlanId = id,
                TargetEventId = eventId,
                Action = "Deleted",
                PlanName = null,
                TotalDuration = 0,
                SectionCount = 0,
                DrillCount = 0
            });

            // The bus outbox only ships what a SaveChanges commits, so this publish needs its
            // own flush. Without it the message is written and never sent, the event keeps its
            // TrainingPlanId and summary, and the plan's header outlives the plan itself.
            await _planRepository.SaveChangesAsync();
        }
    }

    // =========================================================================
    // EVENT PLANS
    // =========================================================================

    public async Task<TrainingPlanDetailDto> CreateEventPlanAsync(Guid eventId, CreateEventPlanDto request, Guid userId)
    {
        ValidatePlanFields(request.Name, request.Description, request.Sections, request.Items);
        ValidateItemShapes(request.Items);

        // Verify user is event admin (organizer/co-organizer)
        var isAdmin = await _eventsGrpcClient.IsEventAdminAsync(eventId, userId);
        if (!isAdmin)
            throw new ForbiddenException("Only event admins can create training plans for events");

        // Check if event already has a plan
        var existingPlan = await _planRepository.Query()
            .FirstOrDefaultAsync(p => p.EventId == eventId && p.PlanType == PlanType.Instance && !p.IsDeleted);
        if (existingPlan != null)
            throw new BadRequestException("This event already has a training plan", ErrorCodeEnum.ValidationError);

        TrainingPlan? sourceTemplate = null;
        if (request.SourceTemplateId.HasValue)
        {
            sourceTemplate = await _planRepository.GetByIdWithDetailsAsync(request.SourceTemplateId.Value);
            if (sourceTemplate == null || sourceTemplate.PlanType != PlanType.Template)
                throw new EntityNotFoundException("Source template not found");
        }

        // Create the instance plan
        var plan = new TrainingPlan
        {
            Name = request.Name ?? sourceTemplate?.Name ?? "Training Plan",
            Description = request.Description ?? sourceTemplate?.Description,
            CreatedByUserId = userId,
            PlanType = PlanType.Instance,
            EventId = eventId,
            SourceTemplateId = request.SourceTemplateId,
            Visibility = TemplateVisibility.Private,
            TotalDuration = 0,
            LikeCount = 0,
            UsageCount = 0
        };

        _planRepository.Add(plan);
        await _planRepository.SaveChangesAsync();

        // Copy sections and items from source template, or use request body
        if (sourceTemplate != null)
        {
            await CopySectionsAndItemsAsync(sourceTemplate, plan.Id);

            // Atomic increment UsageCount on source template
            await _planRepository.Query()
                .Where(p => p.Id == sourceTemplate.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UsageCount, p => p.UsageCount + 1));
        }
        else
        {
            // Use sections/items from request body
            if (request.Sections != null && request.Sections.Count > 0)
            {
                foreach (var sectionDto in request.Sections.OrderBy(s => s.Order))
                {
                    var section = new PlanSection
                    {
                        TemplateId = plan.Id,
                        Name = sectionDto.Name,
                        Order = sectionDto.Order
                    };
                    if (sectionDto.Id.HasValue)
                        section.Id = sectionDto.Id.Value;
                    _sectionRepository.Add(section);
                }
                await _sectionRepository.SaveChangesAsync();
            }

            if (request.Items != null && request.Items.Count > 0)
            {
                await ValidateItemDrillsAsync(request.Items);

                int order = 1;
                var dialValues = new List<PlanItemDialValue>();
                foreach (var itemDto in request.Items)
                {
                    _itemRepository.Add(BuildItem(plan.Id, itemDto, order++, dialValues));
                }
                foreach (var value in dialValues)
                    _dialValueRepository.Add(value);
                await _itemRepository.SaveChangesAsync();
            }
        }

        // Recalculate total duration
        await RecalculateTotalDurationAsync(plan.Id);

        // Re-fetch with details
        var created = await _planRepository.GetByIdWithDetailsAsync(plan.Id);
        var dto = _mapper.Map<TrainingPlanDetailDto>(created);
        await AttachDialValuesAsync(dto);
        await EnrichWithClubInfoAsync([dto]);

        _analytics.CaptureTrainingPlanCreated(plan, userId, created?.Items.Count ?? 0);

        // Publish event so events-service can update its summary
        if (created != null)
            await PublishPlanUpdatedAsync(created, "Created");

        return dto;
    }

    public async Task<TrainingPlanDetailDto?> GetByEventIdAsync(Guid eventId, Guid userId)
    {
        var (isParticipant, eventExists) = await _eventsGrpcClient.IsEventParticipantAsync(eventId, userId);

        if (!eventExists)
            throw new EntityNotFoundException("Event not found");

        // Being at the session is one way in; being responsible for it is the other. A club owner,
        // a head coach covering it, the coach of the group it belongs to — none of them are
        // participants, and all of them need to read the plan. IsEventParticipant deliberately
        // stays a question about attendance, so the second arm is asked separately rather than by
        // widening what "participant" means for every other caller.
        if (!isParticipant && !await _eventsGrpcClient.IsEventAdminAsync(eventId, userId))
            throw new ForbiddenException("You do not have access to this event's training plan");

        // Mirrors GetByIdWithDetailsAsync — the same drill payload whichever way a plan is
        // read; a field added there and not here is invisible exactly on the event pages.
        var plan = await _planRepository.Query()
            .Include(p => p.Sections.OrderBy(s => s.Order))
            .Include(p => p.Items.OrderBy(i => i.Order))
                .ThenInclude(i => i.Drill)
                    // The values the plan holds are unreadable without the definitions beside them.
                    .ThenInclude(d => d!.Dials.OrderBy(dial => dial.Order))
            .Include(p => p.Items)
                .ThenInclude(i => i.Drill)
                    .ThenInclude(d => d!.Equipment.OrderBy(e => e.Order))
            .Include(p => p.Items)
                .ThenInclude(i => i.Stations.OrderBy(st => st.Order))
                    .ThenInclude(st => st.Items.OrderBy(r => r.Order))
                        .ThenInclude(r => r.Drill)
                            .ThenInclude(d => d!.Dials.OrderBy(dial => dial.Order))
            .Include(p => p.Items)
                .ThenInclude(i => i.Stations)
                    .ThenInclude(st => st.Items)
                        .ThenInclude(r => r.Drill)
                            .ThenInclude(d => d!.Equipment.OrderBy(e => e.Order))
            .Include(p => p.Items)
                .ThenInclude(i => i.Stations)
                    .ThenInclude(st => st.Coaches)
            .Include(p => p.Coaches)
            .Include(p => p.Creator)
            .AsSplitQuery()
            // One plan per event is kept by CreateEventPlanAsync, not by an index; ordering by Id
            // keeps every split statement on the same plan should a second ever exist.
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(p => p.EventId == eventId && p.PlanType == PlanType.Instance && !p.IsDeleted);

        if (plan == null) return null;

        var dto = _mapper.Map<TrainingPlanDetailDto>(plan);
        await AttachDialValuesAsync(dto);
        await EnrichWithClubInfoAsync([dto]);
        await EnrichWithCoachNamesAsync(dto);
        return dto;
    }

    public async Task<TrainingPlanDetailDto> PromoteToTemplateAsync(Guid planId, PromotePlanDto request, Guid userId)
    {
        ValidatePlanFields(request.Name, null, null, null);

        var plan = await _planRepository.GetByIdWithDetailsAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        if (plan.PlanType != PlanType.Instance)
            throw new BadRequestException("Only event plans can be promoted to templates", ErrorCodeEnum.ValidationError);

        // Verify user has access: is event admin or plan creator
        var hasAccess = plan.CreatedByUserId == userId;
        if (!hasAccess && plan.EventId.HasValue)
        {
            hasAccess = await _eventsGrpcClient.IsEventAdminAsync(plan.EventId.Value, userId);
        }
        if (!hasAccess)
            throw new ForbiddenException("Only the plan creator or event admin can promote this plan");

        // Create new template plan by copying
        var template = new TrainingPlan
        {
            Name = request.Name ?? plan.Name,
            Description = plan.Description,
            CreatedByUserId = userId,
            ClubId = request.ClubId,
            PlanType = PlanType.Template,
            Visibility = TemplateVisibility.Private,
            Level = plan.Level,
            TotalDuration = 0,
            LikeCount = 0,
            UsageCount = 0
        };

        _planRepository.Add(template);
        await _planRepository.SaveChangesAsync();

        // Copy sections and items
        await CopySectionsAndItemsAsync(plan, template.Id);

        // Recalculate total duration
        await RecalculateTotalDurationAsync(template.Id);

        // Re-fetch with details
        var created = await _planRepository.GetByIdWithDetailsAsync(template.Id);
        var dto = _mapper.Map<TrainingPlanDetailDto>(created);
        await AttachDialValuesAsync(dto);
        await EnrichWithClubInfoAsync([dto]);
        return dto;
    }

    // =========================================================================
    // LIST/BROWSE
    // =========================================================================

    public async Task<PlanListResponseDto> GetMyPlansAsync(Guid userId, PlanFilterRequest filter)
    {
        var query = _planRepository.Query()
            .Where(t => t.CreatedByUserId == userId && t.PlanType == PlanType.Template);

        var (items, totalCount) = await ApplyFiltersAndPaginationAsync(query, filter);

        var dtos = _mapper.Map<List<TrainingPlanDto>>(items);
        await EnrichWithClubInfoAsync(dtos);
        await EnrichWithUserInteractionsAsync(dtos, userId);

        return new PlanListResponseDto
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = filter.Page,
            PageSize = filter.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)filter.PageSize)
        };
    }

    public async Task<PlanListResponseDto> GetClubPlansAsync(Guid clubId, Guid userId, PlanFilterRequest filter)
    {
        // A foreign club's plans must not leak to a non-member who merely supplies that club's
        // ID — mirrors the IsUserClubMemberAsync check FeedbackAuthorizationService already uses
        // for club-scoped authorization elsewhere in this service.
        if (!await _clubsClient.IsUserClubMemberAsync(userId, clubId))
        {
            return new PlanListResponseDto
            {
                Items = [],
                TotalCount = 0,
                Page = filter.Page,
                PageSize = filter.PageSize,
                TotalPages = 0
            };
        }

        var query = _planRepository.Query()
            .Where(t => t.ClubId == clubId && t.PlanType == PlanType.Template && t.Visibility != TemplateVisibility.Private);

        var (items, totalCount) = await ApplyFiltersAndPaginationAsync(query, filter);

        var dtos = _mapper.Map<List<TrainingPlanDto>>(items);
        await EnrichWithClubInfoAsync(dtos);
        await EnrichWithUserInteractionsAsync(dtos, userId);

        return new PlanListResponseDto
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = filter.Page,
            PageSize = filter.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)filter.PageSize)
        };
    }

    public async Task<PlanListResponseDto> GetPublicPlansAsync(PlanFilterRequest filter, Guid? userId = null)
    {
        var query = _planRepository.Query()
            .Where(t => t.PlanType == PlanType.Template && t.Visibility == TemplateVisibility.Public);

        var (items, totalCount) = await ApplyFiltersAndPaginationAsync(query, filter);

        var dtos = _mapper.Map<List<TrainingPlanDto>>(items);
        await EnrichWithClubInfoAsync(dtos);
        await EnrichWithUserInteractionsAsync(dtos, userId);

        return new PlanListResponseDto
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = filter.Page,
            PageSize = filter.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)filter.PageSize)
        };
    }

    public async Task<PlanListResponseDto> GetBookmarkedPlansAsync(Guid userId, PlanFilterRequest filter)
    {
        var skip = (filter.Page - 1) * filter.PageSize;
        var bookmarks = await _bookmarkRepository.GetByUserAsync(userId, skip, filter.PageSize);
        var totalCount = await _bookmarkRepository.GetCountByUserAsync(userId);

        var plans = bookmarks.Select(b => b.Plan).ToList();

        var dtos = _mapper.Map<List<TrainingPlanDto>>(plans);
        await EnrichWithClubInfoAsync(dtos);
        await EnrichWithUserInteractionsAsync(dtos, userId);

        return new PlanListResponseDto
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = filter.Page,
            PageSize = filter.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)filter.PageSize)
        };
    }

    public async Task<PlanListResponseDto> GetLikedPlansAsync(Guid userId, PlanFilterRequest filter)
    {
        var skip = (filter.Page - 1) * filter.PageSize;
        var likes = await _likeRepository.GetByUserAsync(userId, skip, filter.PageSize);
        var totalCount = await _likeRepository.GetCountByUserAsync(userId);

        var plans = likes.Select(l => l.Plan).ToList();

        var dtos = _mapper.Map<List<TrainingPlanDto>>(plans);
        await EnrichWithClubInfoAsync(dtos);
        await EnrichWithUserInteractionsAsync(dtos, userId);

        return new PlanListResponseDto
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = filter.Page,
            PageSize = filter.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)filter.PageSize)
        };
    }

    // =========================================================================
    // SECTIONS
    // =========================================================================

    public async Task<PlanSectionDto> AddSectionAsync(Guid planId, CreatePlanSectionDto request, Guid userId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BadRequestException("Section name is required", ErrorCodeEnum.ValidationError);

        var section = new PlanSection
        {
            TemplateId = planId,
            Name = request.Name,
            Order = request.Order
        };

        _sectionRepository.Add(section);
        await _sectionRepository.SaveChangesAsync();

        return _mapper.Map<PlanSectionDto>(section);
    }

    public async Task<PlanSectionDto> UpdateSectionAsync(Guid planId, Guid sectionId, UpdatePlanSectionDto request, Guid userId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        var section = await _sectionRepository.GetByIdAsync(sectionId);
        if (section == null || section.TemplateId != planId)
            throw new EntityNotFoundException("Section not found");

        if (request.Name != null)
            section.Name = request.Name;

        if (request.Order.HasValue)
            section.Order = request.Order.Value;

        section.UpdatedAt = DateTime.UtcNow;

        _sectionRepository.Update(section);
        await _sectionRepository.SaveChangesAsync();

        return _mapper.Map<PlanSectionDto>(section);
    }

    public async Task DeleteSectionAsync(Guid planId, Guid sectionId, Guid userId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        var section = await _sectionRepository.GetByIdAsync(sectionId);
        if (section == null || section.TemplateId != planId)
            throw new EntityNotFoundException("Section not found");

        // Set all items in this section to have null sectionId (ungrouped)
        var items = await _itemRepository.Query()
            .Where(i => i.SectionId == sectionId)
            .ToListAsync();

        foreach (var item in items)
        {
            item.SectionId = null;
            _itemRepository.Update(item);
        }

        _sectionRepository.Delete(section);
        await _sectionRepository.SaveChangesAsync();
    }

    // =========================================================================
    // ITEMS
    // =========================================================================

    public async Task<PlanItemDto> AddItemAsync(Guid planId, CreatePlanItemDto request, Guid userId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        if (request.Kind.HasDrill())
        {
            var drill = await _drillRepository.GetByIdAsync(request.DrillId!.Value);
            if (drill == null)
                throw new EntityNotFoundException("Drill not found");
        }

        // Validate section if provided
        if (request.SectionId.HasValue)
        {
            var section = await _sectionRepository.GetByIdAsync(request.SectionId.Value);
            if (section == null || section.TemplateId != planId)
                throw new BadRequestException("Section not found", ErrorCodeEnum.EntityNotFound);
        }

        var maxOrder = await _itemRepository.GetMaxOrderAsync(planId);

        var dialValues = new List<PlanItemDialValue>();
        var item = BuildItem(planId, request, maxOrder + 1, dialValues);

        _itemRepository.Add(item);
        foreach (var value in dialValues)
            _dialValueRepository.Add(value);
        await _itemRepository.SaveChangesAsync();

        // Recalculate total duration
        await RecalculateTotalDurationAsync(planId);

        // Re-fetch item
        var created = await _itemRepository.GetByIdAsync(item.Id);
        return _mapper.Map<PlanItemDto>(created);
    }

    public async Task<PlanItemDto> UpdateItemAsync(Guid planId, Guid itemId, UpdatePlanItemDto request, Guid userId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        var item = await _itemRepository.GetByIdAsync(itemId);
        if (item == null || item.TemplateId != planId)
            throw new EntityNotFoundException("Plan item not found");

        // Validate section if provided
        if (request.SectionId.HasValue)
        {
            var section = await _sectionRepository.GetByIdAsync(request.SectionId.Value);
            if (section == null || section.TemplateId != planId)
                throw new BadRequestException("Section not found", ErrorCodeEnum.EntityNotFound);
        }

        bool durationChanged = false;

        if (request.SectionId.HasValue)
            item.SectionId = request.SectionId.Value;

        if (request.Duration.HasValue && request.Duration.Value != item.Duration)
        {
            item.Duration = request.Duration.Value;
            durationChanged = true;
        }

        if (request.Notes != null)
            item.Notes = request.Notes;

        item.UpdatedAt = DateTime.UtcNow;

        _itemRepository.Update(item);
        await _itemRepository.SaveChangesAsync();

        // Recalculate total duration if changed
        if (durationChanged)
            await RecalculateTotalDurationAsync(planId);

        // Re-fetch item
        var updated = await _itemRepository.GetByIdAsync(item.Id);
        return _mapper.Map<PlanItemDto>(updated);
    }

    public async Task DeleteItemAsync(Guid planId, Guid itemId, Guid userId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        var item = await _itemRepository.GetByIdAsync(itemId);
        if (item == null || item.TemplateId != planId)
            throw new EntityNotFoundException("Plan item not found");

        var values = await _dialValueRepository.Query()
            .Where(v => v.ItemId == itemId)
            .ToListAsync();
        foreach (var value in values)
            _dialValueRepository.Delete(value);

        _itemRepository.Delete(item);
        await _itemRepository.SaveChangesAsync();

        // Recalculate total duration
        await RecalculateTotalDurationAsync(planId);
    }

    public async Task ReorderItemsAsync(Guid planId, ReorderPlanItemsDto request, Guid userId)
    {
        var plan = await _planRepository.GetByIdWithDetailsAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await ValidatePlanEditAsync(plan, userId);

        var items = plan.Items.ToDictionary(i => i.Id);

        for (int i = 0; i < request.ItemIds.Count; i++)
        {
            var itemId = request.ItemIds[i];
            if (items.TryGetValue(itemId, out var item))
            {
                item.Order = i + 1;
                item.UpdatedAt = DateTime.UtcNow;
                _itemRepository.Update(item);
            }
        }

        await _itemRepository.SaveChangesAsync();
    }

    // =========================================================================
    // LIKES
    // =========================================================================

    public async Task<PlanLikeStatusDto> LikeAsync(Guid planId, Guid userId)
    {
        await ValidateIsTemplate(planId);

        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        // Check if already liked
        var existingLike = await _likeRepository.GetByTemplateAndUserAsync(planId, userId);
        if (existingLike != null)
        {
            return new PlanLikeStatusDto
            {
                IsLiked = true,
                LikeCount = plan.LikeCount
            };
        }

        // Create like
        var like = new PlanLike
        {
            TemplateId = planId,
            UserId = userId
        };

        _likeRepository.Add(like);

        // Update denormalized count
        plan.LikeCount++;
        _planRepository.Update(plan);

        await _planRepository.SaveChangesAsync();

        return new PlanLikeStatusDto
        {
            IsLiked = true,
            LikeCount = plan.LikeCount
        };
    }

    public async Task<PlanLikeStatusDto> UnlikeAsync(Guid planId, Guid userId)
    {
        await ValidateIsTemplate(planId);

        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        var existingLike = await _likeRepository.GetByTemplateAndUserAsync(planId, userId);
        if (existingLike == null)
        {
            return new PlanLikeStatusDto
            {
                IsLiked = false,
                LikeCount = plan.LikeCount
            };
        }

        _likeRepository.Delete(existingLike);

        // Update denormalized count
        plan.LikeCount = Math.Max(0, plan.LikeCount - 1);
        _planRepository.Update(plan);

        await _planRepository.SaveChangesAsync();

        return new PlanLikeStatusDto
        {
            IsLiked = false,
            LikeCount = plan.LikeCount
        };
    }

    public async Task<PlanLikeStatusDto> GetLikeStatusAsync(Guid planId, Guid userId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        var existingLike = await _likeRepository.GetByTemplateAndUserAsync(planId, userId);

        return new PlanLikeStatusDto
        {
            IsLiked = existingLike != null,
            LikeCount = plan.LikeCount
        };
    }

    // =========================================================================
    // BOOKMARKS
    // =========================================================================

    public async Task<PlanBookmarkStatusDto> BookmarkAsync(Guid planId, Guid userId)
    {
        await ValidateIsTemplate(planId);

        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        // Check if already bookmarked
        var existingBookmark = await _bookmarkRepository.GetByTemplateAndUserAsync(planId, userId);
        if (existingBookmark != null)
        {
            return new PlanBookmarkStatusDto { IsBookmarked = true };
        }

        // Create bookmark
        var bookmark = new PlanBookmark
        {
            TemplateId = planId,
            UserId = userId
        };

        _bookmarkRepository.Add(bookmark);
        await _bookmarkRepository.SaveChangesAsync();

        return new PlanBookmarkStatusDto { IsBookmarked = true };
    }

    public async Task<PlanBookmarkStatusDto> UnbookmarkAsync(Guid planId, Guid userId)
    {
        await ValidateIsTemplate(planId);

        var existingBookmark = await _bookmarkRepository.GetByTemplateAndUserAsync(planId, userId);
        if (existingBookmark == null)
        {
            return new PlanBookmarkStatusDto { IsBookmarked = false };
        }

        _bookmarkRepository.Delete(existingBookmark);
        await _bookmarkRepository.SaveChangesAsync();

        return new PlanBookmarkStatusDto { IsBookmarked = false };
    }

    // =========================================================================
    // COMMENTS
    // =========================================================================

    public async Task<PlanCommentDto> CreateCommentAsync(Guid planId, CreatePlanCommentDto request, Guid userId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await AuthorizePlanCommentAccess(plan, userId);

        if (string.IsNullOrWhiteSpace(request.Content))
            throw new BadRequestException("Comment content is required", ErrorCodeEnum.ValidationError);

        // Validate parent comment if provided
        if (request.ParentCommentId.HasValue)
        {
            var parentComment = await _commentRepository.GetByIdAsync(request.ParentCommentId.Value);
            if (parentComment == null || parentComment.TemplateId != planId || parentComment.IsDeleted)
                throw new BadRequestException("Parent comment not found", ErrorCodeEnum.EntityNotFound);
        }

        var comment = new PlanComment
        {
            TemplateId = planId,
            UserId = userId,
            Content = request.Content,
            ParentCommentId = request.ParentCommentId
        };

        _commentRepository.Add(comment);
        await _commentRepository.SaveChangesAsync();

        // Re-fetch with details
        var createdComment = await _commentRepository.GetByIdWithDetailsAsync(comment.Id);
        return _mapper.Map<PlanCommentDto>(createdComment);
    }

    public async Task<PlanCommentsResponseDto> GetCommentsAsync(Guid planId, Guid? cursor, int limit, Guid userId)
    {
        limit = Math.Clamp(limit, 1, 100);

        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await AuthorizePlanCommentAccess(plan, userId);

        var comments = (await _commentRepository.GetByTemplateWithCursorAsync(planId, cursor, limit)).ToList();
        var hasMore = comments.Count > limit;

        if (hasMore)
        {
            comments = comments.Take(limit).ToList();
        }

        var nextCursor = hasMore && comments.Count > 0 ? comments.Last().Id : (Guid?)null;

        return new PlanCommentsResponseDto
        {
            Items = _mapper.Map<List<PlanCommentDto>>(comments),
            NextCursor = nextCursor,
            HasMore = hasMore
        };
    }

    public async Task DeleteCommentAsync(Guid planId, Guid commentId, Guid userId)
    {
        var comment = await _commentRepository.GetByIdAsync(commentId);
        if (comment == null || comment.TemplateId != planId)
            throw new EntityNotFoundException("Comment not found");

        // Authorize plan access before revealing any ownership details
        var plan = await _planRepository.GetByIdAsync(comment.TemplateId);
        if (plan == null)
            throw new EntityNotFoundException("Plan not found");

        await AuthorizePlanCommentAccess(plan, userId);

        if (comment.UserId != userId)
        {
            // Allow plan owner to moderate comments
            var canModerate = plan.CreatedByUserId == userId;

            // For Instance plans, also allow event admins to moderate
            if (!canModerate && plan.PlanType == PlanType.Instance && plan.EventId != null)
            {
                canModerate = await _eventsGrpcClient.IsEventAdminAsync(plan.EventId.Value, userId);
            }

            if (!canModerate)
                throw new ForbiddenException("You can only delete your own comments");
        }

        // Soft delete
        comment.IsDeleted = true;
        _commentRepository.Update(comment);
        await _commentRepository.SaveChangesAsync();
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    /// <summary>
    /// Authorizes comment access based on plan type.
    /// Instance plans: user must be a participant of the linked event.
    /// Template plans: open access (public templates are commentable by anyone).
    /// </summary>
    private async Task AuthorizePlanCommentAccess(TrainingPlan plan, Guid userId)
    {
        if (plan.PlanType == PlanType.Instance)
        {
            if (plan.EventId == null)
                throw new BadRequestException("Instance plan has no linked event", ErrorCodeEnum.ValidationError);

            var (isParticipant, eventExists) = await _eventsGrpcClient.IsEventParticipantAsync(plan.EventId.Value, userId);
            if (!eventExists)
                throw new EntityNotFoundException("The linked event no longer exists");
            if (!isParticipant)
                throw new ForbiddenException("Only event participants can comment on this plan");
        }
        // Template plans: open access, no additional check needed
    }

    /// <summary>
    /// Validates that the plan exists and is a Template (not an Instance).
    /// Call at the top of social methods to enforce that social features are template-only.
    /// </summary>
    private async Task ValidateIsTemplate(Guid planId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null || plan.PlanType != PlanType.Template)
            throw new EntityNotFoundException("Plan not found");
    }

    /// <summary>
    /// Copies sections and items from a source plan to a target plan.
    /// Used by both CreateEventPlanAsync (copy from template) and PromoteToTemplateAsync (copy from instance).
    /// </summary>
    private async Task CopySectionsAndItemsAsync(TrainingPlan source, Guid targetPlanId)
    {
        // Map old section IDs to new section IDs so items reference the correct section
        var sectionIdMap = new Dictionary<Guid, Guid>();

        if (source.Sections.Count > 0)
        {
            foreach (var sourceSection in source.Sections.OrderBy(s => s.Order))
            {
                var newSection = new PlanSection
                {
                    TemplateId = targetPlanId,
                    Name = sourceSection.Name,
                    Order = sourceSection.Order
                };
                sectionIdMap[sourceSection.Id] = newSection.Id;
                _sectionRepository.Add(newSection);
            }
            await _sectionRepository.SaveChangesAsync();
        }

        if (source.Items.Count > 0)
        {
            // The answers the source gave its dials come with the copy: a plan saved as a
            // template that arrived blank would make the coach set every dial again.
            var sourceValues = await _dialValueRepository.Query()
                .Where(v => v.PlanId == source.Id && v.ItemId != null)
                .ToListAsync();

            foreach (var sourceItem in source.Items.OrderBy(i => i.Order))
            {
                var newItem = new PlanItem
                {
                    TemplateId = targetPlanId,
                    Kind = sourceItem.Kind,
                    DrillId = sourceItem.DrillId,
                    Title = sourceItem.Title,
                    SectionId = sourceItem.SectionId.HasValue && sectionIdMap.ContainsKey(sourceItem.SectionId.Value)
                        ? sectionIdMap[sourceItem.SectionId.Value]
                        : null,
                    Duration = sourceItem.Duration,
                    PlannedDuration = sourceItem.PlannedDuration,
                    Notes = sourceItem.Notes,
                    Order = sourceItem.Order,
                    Stations = CopyStations(sourceItem)
                };
                _itemRepository.Add(newItem);

                foreach (var value in sourceValues.Where(v => v.ItemId == sourceItem.Id))
                    _dialValueRepository.Add(new PlanItemDialValue
                    {
                        PlanId = targetPlanId,
                        ItemId = newItem.Id,
                        DialName = value.DialName,
                        Value = value.Value,
                    });
            }
            await _itemRepository.SaveChangesAsync();
        }
    }

    /// <summary>
    /// The groups of a copied Stations row, rebuilt under new ids. A copy that carries the row
    /// but not its groups is an empty block: the split IS the row's content, and dropping it
    /// silently turned every loaded template's stations into a blank stretch of practice.
    /// </summary>
    private static List<PlanStation> CopyStations(PlanItem sourceItem) =>
        sourceItem.Stations
            .OrderBy(st => st.Order)
            .Select(st => new PlanStation
            {
                Name = st.Name,
                Order = st.Order,
                Items = st.Items
                    .OrderBy(r => r.Order)
                    .Select(r => new PlanStationItem
                    {
                        Kind = r.Kind,
                        DrillId = r.DrillId,
                        Title = r.Title,
                        Duration = r.Duration,
                        Notes = r.Notes,
                        Order = r.Order
                    })
                    .ToList()
            })
            .ToList();

    private async Task PublishPlanUpdatedAsync(TrainingPlan plan, string action)
    {
        if (plan.EventId == null) return;

        try
        {
            await _publishEndpoint.Publish(new TrainingPlanUpdatedEvent
            {
                PlanId = plan.Id,
                TargetEventId = plan.EventId,
                Action = action,
                PlanName = plan.Name,
                TotalDuration = plan.TotalDuration,
                SectionCount = plan.Sections?.Count ?? 0,
                DrillCount = plan.Items?.Count ?? 0
            });

            // Flush the EF outbox so the message is persisted and delivered
            await _planRepository.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish TrainingPlanUpdatedEvent for plan {PlanId}", plan.Id);
        }
    }

    /// <summary>
    /// Only the rows that point at a drill are checked; a break has none to check.
    /// </summary>
    private async Task ValidateItemDrillsAsync(IEnumerable<CreatePlanItemDto> items)
    {
        var drillIds = items
            .Where(i => i.Kind.HasDrill() && i.DrillId.HasValue)
            .Select(i => i.DrillId!.Value)
            .Concat(items
                .SelectMany(i => i.Stations ?? [])
                .SelectMany(st => st.Items ?? [])
                .Where(r => r.Kind.HasDrill() && r.DrillId.HasValue)
                .Select(r => r.DrillId!.Value))
            .Distinct();

        foreach (var drillId in drillIds)
        {
            var drill = await _drillRepository.GetByIdAsync(drillId);
            if (drill == null)
                throw new BadRequestException($"Drill not found: {drillId}", ErrorCodeEnum.EntityNotFound);
        }
    }

    /// <summary>
    /// One place turns a request into a plan item, so the create, update and copy paths
    /// cannot drift on what a kind is allowed to carry.
    /// </summary>
    private static void ValidateItemShapes(IEnumerable<CreatePlanItemDto>? items)
    {
        foreach (var dto in items ?? [])
        {
            if (dto.Kind.HasDrill() && !dto.DrillId.HasValue)
                throw new BadRequestException("A drill item needs a drill", ErrorCodeEnum.ValidationError);

            if (!dto.Kind.HasDrill() && string.IsNullOrWhiteSpace(dto.Title))
                throw new BadRequestException($"A {dto.Kind} needs a title", ErrorCodeEnum.ValidationError);

            // Groups divide a Stations row. Hanging them off anything else would store a
            // split nothing draws, and the client would lose it on the next save.
            if (dto.Stations?.Count > 0 && dto.Kind != ItemKind.Stations)
                throw new BadRequestException($"A {dto.Kind} cannot have groups", ErrorCodeEnum.ValidationError);

            foreach (var station in dto.Stations ?? [])
            {
                if (string.IsNullOrWhiteSpace(station.Name))
                    throw new BadRequestException("A group needs a name", ErrorCodeEnum.ValidationError);

                foreach (var row in station.Items ?? [])
                {
                    // A group holds the practice, not more structure: a section is the plan's
                    // to divide, and stations inside stations has no meaning on a court.
                    if (row.Kind is ItemKind.Stations)
                        throw new BadRequestException("A group cannot contain stations", ErrorCodeEnum.ValidationError);

                    if (row.Kind.HasDrill() && !row.DrillId.HasValue)
                        throw new BadRequestException("A drill item needs a drill", ErrorCodeEnum.ValidationError);

                    if (!row.Kind.HasDrill() && string.IsNullOrWhiteSpace(row.Title))
                        throw new BadRequestException($"A {row.Kind} needs a title", ErrorCodeEnum.ValidationError);
                }
            }
        }
    }

    /// <summary>
    /// An id a payload sends is either this plan's own row or nobody's. One that belongs to
    /// another plan is a caller mistake rather than a new row: it would be built as an insert and
    /// collide on the primary key mid-save. The same id sent twice is refused for the same
    /// reason — the second entry would silently fold into the first and take its dial values.
    /// </summary>
    private async Task GuardPayloadIdsAsync(
        TrainingPlan plan, List<CreatePlanSectionDto>? sections, List<CreatePlanItemDto>? items)
    {
        var stations = (items ?? []).SelectMany(i => i.Stations ?? []).ToList();
        var planStations = plan.Items.SelectMany(i => i.Stations).ToList();

        // Sequential on purpose: every repository here resolves the same scoped DbContext, and
        // EF throws the moment two of these queries overlap.
        await EnsureIdsAreFreeAsync(
            SentIds(sections, s => s.Id),
            plan.Sections.Select(s => s.Id),
            _sectionRepository.Query(),
            "section");

        await EnsureIdsAreFreeAsync(
            SentIds(items, i => i.Id),
            plan.Items.Select(i => i.Id),
            _itemRepository.Query(),
            "item");

        await EnsureIdsAreFreeAsync(
            SentIds(stations, st => st.Id),
            planStations.Select(st => st.Id),
            _stationRepository.Query(),
            "group");

        await EnsureIdsAreFreeAsync(
            SentIds(stations.SelectMany(st => st.Items ?? []), r => r.Id),
            planStations.SelectMany(st => st.Items).Select(r => r.Id),
            _stationItemRepository.Query(),
            "group row");
    }

    private static List<Guid> SentIds<T>(IEnumerable<T>? entries, Func<T, Guid?> id) =>
        (entries ?? []).Select(id).Where(v => v.HasValue).Select(v => v!.Value).ToList();

    private static async Task EnsureIdsAreFreeAsync<T>(
        List<Guid> sent, IEnumerable<Guid> planOwn, IQueryable<T> all, string what)
        where T : BaseEntity
    {
        if (sent.Count == 0) return;

        if (sent.Distinct().Count() != sent.Count)
            throw new BadRequestException(
                $"The same {what} id appears twice in this payload", ErrorCodeEnum.ValidationError);

        var own = planOwn.ToHashSet();
        var foreign = sent.Where(id => !own.Contains(id)).ToList();
        if (foreign.Count == 0) return;

        if (await all.AnyAsync(e => foreign.Contains(e.Id)))
            throw new BadRequestException(
                $"A {what} id in this payload belongs to another plan", ErrorCodeEnum.ValidationError);
    }

    /// <summary>
    /// Sections resent by id keep their rows, so the items sitting in them keep their home. A
    /// section the payload no longer names goes, and its items are set loose by the FK rather
    /// than deleted with it.
    /// </summary>
    private void ReconcileSections(TrainingPlan plan, List<CreatePlanSectionDto> sections)
    {
        var kept = new HashSet<Guid>();

        foreach (var dto in sections.OrderBy(s => s.Order))
        {
            var section = dto.Id.HasValue ? plan.Sections.FirstOrDefault(s => s.Id == dto.Id.Value) : null;

            if (section == null)
            {
                section = new PlanSection { TemplateId = plan.Id, Name = dto.Name, Order = dto.Order };
                // The wizard mints an id before the row is saved; honouring it is what lets the
                // next save recognise the row rather than build a second one.
                if (dto.Id.HasValue) section.Id = dto.Id.Value;

                plan.Sections.Add(section);
                _sectionRepository.Add(section);
            }
            else
            {
                section.Name = dto.Name;
                section.Order = dto.Order;
                section.UpdatedAt = DateTime.UtcNow;
                _sectionRepository.Update(section);
            }

            kept.Add(section.Id);
        }

        foreach (var gone in plan.Sections.Where(s => !kept.Contains(s.Id)).ToList())
        {
            _sectionRepository.Delete(gone);
            plan.Sections.Remove(gone);
        }
    }

    /// <summary>
    /// Items resent by id keep their rows. A new row states itself against the item set as well
    /// as joining the plan's collection: BaseEntity gives every entity an id in its constructor,
    /// so a row merely added to a tracked collection reads to EF as an existing one and is saved
    /// as an UPDATE that matches nothing. Same reason as the run reconcile in RunService.
    /// </summary>
    private async Task ReconcileItemsAsync(TrainingPlan plan, List<CreatePlanItemDto> items)
    {
        var storedValues = await _dialValueRepository.Query()
            .Where(v => v.PlanId == plan.Id)
            .ToListAsync();

        var wantedValues = new List<PlanItemDialValue>();
        var kept = new HashSet<Guid>();
        var fallbackOrder = 1;

        foreach (var dto in items)
        {
            var order = fallbackOrder++;
            var item = dto.Id.HasValue ? plan.Items.FirstOrDefault(i => i.Id == dto.Id.Value) : null;

            if (item == null)
            {
                item = BuildItem(plan.Id, dto, order, wantedValues);
                plan.Items.Add(item);
                _itemRepository.Add(item);
            }
            else
            {
                ValidateItemShapes([dto]);
                ApplyItemFields(item, dto, order);
                item.UpdatedAt = DateTime.UtcNow;
                ReconcileStations(plan.Id, item, dto, wantedValues);
                CollectDialValues(plan.Id, item.Id, inStationGroup: false, dto.DialValues, wantedValues);
                _itemRepository.Update(item);
            }

            kept.Add(item.Id);
        }

        foreach (var gone in plan.Items.Where(i => !kept.Contains(i.Id)).ToList())
        {
            _itemRepository.Delete(gone);
            plan.Items.Remove(gone);
        }

        ReconcileDialValues(storedValues, wantedValues);
    }

    /// <summary>
    /// A group resent by id keeps its row — and with it the coaches assigned to it, which are
    /// never written from here. Recreating the row is what made the lead coach distribute the
    /// staff again after every edit of the plan.
    /// </summary>
    private void ReconcileStations(
        Guid planId, PlanItem item, CreatePlanItemDto dto, ICollection<PlanItemDialValue> dialValues)
    {
        // A row that has stopped being a Stations block keeps no groups.
        List<CreatePlanStationDto> wanted = dto.Kind == ItemKind.Stations ? dto.Stations ?? [] : [];

        var kept = new HashSet<Guid>();
        var order = 0;

        foreach (var stationDto in wanted.OrderBy(st => st.Order))
        {
            var station = stationDto.Id.HasValue
                ? item.Stations.FirstOrDefault(st => st.Id == stationDto.Id.Value)
                : null;

            if (station == null)
            {
                station = new PlanStation { PlanItemId = item.Id, Name = stationDto.Name, Order = order };
                if (stationDto.Id.HasValue) station.Id = stationDto.Id.Value;

                item.Stations.Add(station);
                _stationRepository.Add(station);
            }
            else
            {
                station.Name = stationDto.Name;
                station.Order = order;
                station.UpdatedAt = DateTime.UtcNow;
                _stationRepository.Update(station);
            }

            order++;
            kept.Add(station.Id);
            ReconcileStationItems(planId, station, stationDto, dialValues);
        }

        foreach (var gone in item.Stations.Where(st => !kept.Contains(st.Id)).ToList())
        {
            _stationRepository.Delete(gone);
            item.Stations.Remove(gone);
        }
    }

    private void ReconcileStationItems(
        Guid planId, PlanStation station, CreatePlanStationDto dto, ICollection<PlanItemDialValue> dialValues)
    {
        var kept = new HashSet<Guid>();
        var order = 0;

        foreach (var rowDto in (dto.Items ?? []).OrderBy(r => r.Order))
        {
            var row = rowDto.Id.HasValue ? station.Items.FirstOrDefault(r => r.Id == rowDto.Id.Value) : null;

            if (row == null)
            {
                row = BuildStationItem(rowDto, order);
                row.StationId = station.Id;

                station.Items.Add(row);
                _stationItemRepository.Add(row);
            }
            else
            {
                ApplyStationItemFields(row, rowDto, order);
                row.UpdatedAt = DateTime.UtcNow;
                _stationItemRepository.Update(row);
            }

            order++;
            kept.Add(row.Id);
            CollectDialValues(planId, row.Id, inStationGroup: true, rowDto.DialValues, dialValues);
        }

        foreach (var gone in station.Items.Where(r => !kept.Contains(r.Id)).ToList())
        {
            _stationItemRepository.Delete(gone);
            station.Items.Remove(gone);
        }
    }

    /// <summary>
    /// The payload says what every row it carries sets its dials to, so the stored rows are
    /// brought to match: a changed answer is updated in place, a new one added, and anything the
    /// payload no longer names removed — including the answers of a row that has just been
    /// deleted, which nothing else would take, since these rows hold no key back to the item.
    /// </summary>
    private void ReconcileDialValues(List<PlanItemDialValue> stored, List<PlanItemDialValue> wanted)
    {
        static (Guid?, Guid?, string) Use(PlanItemDialValue value) =>
            (value.ItemId, value.StationItemId, value.DialName);

        var byUse = new Dictionary<(Guid?, Guid?, string), PlanItemDialValue>();
        foreach (var value in stored)
            byUse[Use(value)] = value;

        var claimed = new HashSet<Guid>();

        foreach (var value in wanted)
        {
            if (!byUse.TryGetValue(Use(value), out var match))
            {
                _dialValueRepository.Add(value);
                continue;
            }

            claimed.Add(match.Id);
            if (match.Value == value.Value) continue;

            match.Value = value.Value;
            _dialValueRepository.Update(match);
        }

        foreach (var gone in stored.Where(v => !claimed.Contains(v.Id)))
            _dialValueRepository.Delete(gone);
    }

    private static PlanItem BuildItem(
        Guid planId, CreatePlanItemDto dto, int fallbackOrder, ICollection<PlanItemDialValue> dialValues)
    {
        ValidateItemShapes([dto]);

        var item = new PlanItem { TemplateId = planId };
        if (dto.Id.HasValue) item.Id = dto.Id.Value;

        ApplyItemFields(item, dto, fallbackOrder);
        item.Stations = BuildStations(planId, dto, dialValues);

        CollectDialValues(planId, item.Id, inStationGroup: false, dto.DialValues, dialValues);
        return item;
    }

    /// <summary>
    /// What a payload entry says a row is. One place, so the build and the reconcile cannot
    /// drift on what a kind is allowed to carry.
    /// </summary>
    private static void ApplyItemFields(PlanItem item, CreatePlanItemDto dto, int fallbackOrder)
    {
        item.Kind = dto.Kind;
        // A kind that has no drill keeps no stale reference to one, and vice versa.
        item.DrillId = dto.Kind.HasDrill() ? dto.DrillId : null;
        item.Title = dto.Kind.HasDrill() ? null : dto.Title;
        item.SectionId = dto.SectionId;
        item.Duration = dto.Duration;
        item.Notes = dto.Notes;
        item.Order = dto.Order ?? fallbackOrder;
        item.PlannedDuration = dto.Kind == ItemKind.Stations ? dto.PlannedDuration : null;
    }

    /// <summary>
    /// The groups of a Stations row, built with it so one SaveChangesAsync writes the block
    /// and its groups together rather than leaving a split half-stored.
    /// </summary>
    private static List<PlanStation> BuildStations(
        Guid planId, CreatePlanItemDto dto, ICollection<PlanItemDialValue> dialValues)
    {
        if (dto.Kind != ItemKind.Stations) return [];

        var stations = new List<PlanStation>();
        var stationIndex = 0;

        foreach (var stationDto in (dto.Stations ?? []).OrderBy(st => st.Order))
        {
            var station = new PlanStation { Name = stationDto.Name, Order = stationIndex++ };
            if (stationDto.Id.HasValue) station.Id = stationDto.Id.Value;

            var rowIndex = 0;

            foreach (var row in (stationDto.Items ?? []).OrderBy(r => r.Order))
            {
                var built = BuildStationItem(row, rowIndex++);

                station.Items.Add(built);
                CollectDialValues(planId, built.Id, inStationGroup: true, row.DialValues, dialValues);
            }

            stations.Add(station);
        }

        return stations;
    }

    private static PlanStationItem BuildStationItem(CreatePlanStationItemDto dto, int order)
    {
        var row = new PlanStationItem();
        if (dto.Id.HasValue) row.Id = dto.Id.Value;

        ApplyStationItemFields(row, dto, order);
        return row;
    }

    private static void ApplyStationItemFields(PlanStationItem row, CreatePlanStationItemDto dto, int order)
    {
        row.Kind = dto.Kind;
        row.DrillId = dto.Kind.HasDrill() ? dto.DrillId : null;
        row.Title = dto.Kind.HasDrill() ? null : dto.Title;
        row.Duration = dto.Duration;
        row.Notes = dto.Notes;
        row.Order = order;
    }

    /// <summary>
    /// A use's dial values are written beside it rather than on it: the rows hang off the plan
    /// and reach the use by id alone, so a save that drops the use has to take them by hand.
    /// A name no dial answers to is stored untouched — a dial removed from the drill leaves its
    /// answers behind, and dropping them would lose the coach's work if it came back.
    /// </summary>
    private static void CollectDialValues(
        Guid planId,
        Guid useId,
        bool inStationGroup,
        Dictionary<string, string>? values,
        ICollection<PlanItemDialValue> into)
    {
        if (values is null) return;

        foreach (var (dialName, value) in values)
        {
            // The name a dial no longer goes by is still a name; one that was never a dial name
            // is a client bug, and storing it would put a key on the wire that comes back
            // changed. See DialTokens.
            if (!DialTokens.IsValidName(dialName))
                throw new BadRequestException(
                    $"'{dialName}' is not a dial name",
                    ErrorCodeEnum.ValidationError);

            if (value?.Length > PlanItemDialValue.ValueMaxLength)
                throw new BadRequestException(
                    $"A dial value is longer than {PlanItemDialValue.ValueMaxLength} characters",
                    ErrorCodeEnum.ValidationError);

            into.Add(new PlanItemDialValue
            {
                PlanId = planId,
                ItemId = inStationGroup ? null : useId,
                StationItemId = inStationGroup ? useId : null,
                DialName = dialName,
                Value = value ?? string.Empty,
            });
        }
    }

    /// <summary>
    /// Fills each row's values in after the map. They are keyed to the items by id but stored
    /// against the plan, so one query answers for the whole plan and nothing walks per item.
    /// </summary>
    private async Task AttachDialValuesAsync(TrainingPlanDetailDto? plan)
    {
        if (plan is null) return;

        var values = await _dialValueRepository.Query()
            .Where(v => v.PlanId == plan.Id)
            .ToListAsync();

        if (values.Count == 0) return;

        var byItem = values
            .Where(v => v.ItemId.HasValue)
            .GroupBy(v => v.ItemId!.Value)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.DialName, v => v.Value));

        var byStationItem = values
            .Where(v => v.StationItemId.HasValue)
            .GroupBy(v => v.StationItemId!.Value)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.DialName, v => v.Value));

        foreach (var item in plan.Items)
        {
            if (byItem.TryGetValue(item.Id, out var itemValues))
                item.DialValues = itemValues;

            foreach (var row in item.Stations.SelectMany(st => st.Items))
                if (byStationItem.TryGetValue(row.Id, out var rowValues))
                    row.DialValues = rowValues;
        }
    }

    private async Task RecalculateTotalDurationAsync(Guid planId)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan == null) return;

        var items = await _itemRepository.GetByTemplateAsync(planId);
        plan.TotalDuration = items.Sum(i => i.Duration);
        plan.CoachedDuration = items.Where(i => i.Kind.IsCoached()).Sum(i => i.Duration);
        plan.UpdatedAt = DateTime.UtcNow;

        _planRepository.Update(plan);
        await _planRepository.SaveChangesAsync();
    }

    private async Task ValidatePlanAccessAsync(TrainingPlan plan, Guid? userId)
    {
        // Public plans can be viewed by anyone
        if (plan.Visibility == TemplateVisibility.Public)
            return;

        // Private plans require authentication
        if (!userId.HasValue)
            throw new ForbiddenException("This plan is private");

        // Owner can always access
        if (plan.CreatedByUserId == userId.Value)
            return;

        // Club plans: Check if user is club member when club service is available
        if (plan.ClubId.HasValue)
        {
            // TODO: Check club membership when club service is available
            // For now, allow club members (would need club service integration)
        }

        // Otherwise, deny access
        throw new ForbiddenException("You do not have permission to view this plan");
    }

    private static void ValidatePlanFields(
        string? name,
        string? description,
        List<CreatePlanSectionDto>? sections,
        List<CreatePlanItemDto>? items)
    {
        var errors = new List<FieldError>();

        if (name?.Length > TrainingPlan.NameMaxLength)
            errors.Add(new FieldError("name", "INVALID_LENGTH",
                $"Plan name must be at most {TrainingPlan.NameMaxLength} characters"));

        if (description?.Length > TrainingPlan.DescriptionMaxLength)
            errors.Add(new FieldError("description", "INVALID_LENGTH",
                $"Description must be at most {TrainingPlan.DescriptionMaxLength} characters"));

        if (sections != null)
            for (var i = 0; i < sections.Count; i++)
                if (sections[i].Name?.Length > PlanSection.NameMaxLength)
                    errors.Add(new FieldError($"sections[{i}].name", "INVALID_LENGTH",
                        $"Section name must be at most {PlanSection.NameMaxLength} characters"));

        if (items != null)
            for (var i = 0; i < items.Count; i++)
                if (items[i].Notes?.Length > PlanItem.NotesMaxLength)
                    errors.Add(new FieldError($"items[{i}].notes", "INVALID_LENGTH",
                        $"Item notes must be at most {PlanItem.NotesMaxLength} characters"));

        if (errors.Count > 0)
            throw new ValidationException("One or more fields exceed their maximum length", errors);
    }

    private async Task ValidatePlanEditAsync(TrainingPlan plan, Guid userId)
    {
        // The owner, or anyone who may run the event this plan belongs to — which now includes
        // club staff and the unit's own coaches, not only whoever was made an admin of the event.
        if (await PlanEditPolicy.CanEditAsync(plan, userId, _eventsGrpcClient))
            return;

        // A club TEMPLATE, as opposed to an event's plan, is still owner-only. Its natural gate is
        // library.manage, and wiring that wants the club id resolved here first; leaving the empty
        // branch that used to sit here only made it look answered.
        throw new ForbiddenException("Only the plan owner or an event admin can modify this plan");
    }

    private async Task<(IEnumerable<TrainingPlan> items, int totalCount)> ApplyFiltersAndPaginationAsync(
        IQueryable<TrainingPlan> query,
        PlanFilterRequest filter)
    {
        // Apply search filter
        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var searchLower = filter.SearchTerm.ToLower();
            query = query.Where(t =>
                t.Name.ToLower().Contains(searchLower) ||
                (t.Description != null && t.Description.ToLower().Contains(searchLower)));
        }

        // Apply duration filters
        if (filter.MinDuration.HasValue)
            query = query.Where(t => t.TotalDuration >= filter.MinDuration.Value);

        if (filter.MaxDuration.HasValue)
            query = query.Where(t => t.TotalDuration <= filter.MaxDuration.Value);

        // Apply level filter
        if (filter.Level.HasValue)
            query = query.Where(t => t.Level == filter.Level.Value);

        if (filter.Skills is { Count: > 0 })
        {
            var skills = filter.Skills
                .Select(s => Enum.TryParse<DrillSkill>(s, ignoreCase: true, out var parsed) ? parsed : (DrillSkill?)null)
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToArray();

            if (skills.Length > 0)
                query = query.Where(t => t.Items.Any(i => i.Drill != null && i.Drill.Skills.Any(sk => skills.Contains(sk))));
        }

        // Get total count before pagination
        var totalCount = await query.CountAsync();

        // Apply sorting
        // The clients send "shortest"/"mostLiked"-style names; the older "duration"/"likes" ones
        // are kept so an out-of-date caller keeps the sort it asked for rather than silently
        // falling through to newest.
        var sorted = filter.SortBy?.ToLower() switch
        {
            "name" => query.OrderBy(t => t.Name),
            "shortest" or "duration" => query.OrderBy(t => t.TotalDuration),
            "longest" => query.OrderByDescending(t => t.TotalDuration),
            "mostliked" or "likes" => query.OrderByDescending(t => t.LikeCount),
            "mostused" or "usage" => query.OrderByDescending(t => t.UsageCount),
            "oldest" => query.OrderBy(t => t.CreatedAt),
            _ => query.OrderByDescending(t => t.CreatedAt) // newest by default
        };

        // Apply pagination. Every split statement below re-applies the order beneath Skip/Take,
        // so it must be unique or the statements can pick different plans at a page's edge.
        var skip = (filter.Page - 1) * filter.PageSize;
        var items = await sorted
            .ThenBy(t => t.Id)
            .Skip(skip)
            .Take(filter.PageSize)
            .Include(t => t.Items)
                .ThenInclude(i => i.Drill)
            .Include(t => t.Items)
                .ThenInclude(i => i.Stations.OrderBy(st => st.Order))
                    .ThenInclude(st => st.Items.OrderBy(r => r.Order))
                        .ThenInclude(r => r.Drill)
            .Include(t => t.Sections)
            .Include(t => t.Creator)
            .AsSplitQuery()
            .ToListAsync();

        return (items, totalCount);
    }

    /// <summary>
    /// Enriches plan DTOs with club info from clubs-service (batch operation for performance)
    /// </summary>
    /// <summary>
    /// Stamps each plan with the viewer's own like/bookmark state. Mirrors the drill
    /// list: two batched lookups, and every field left null for an anonymous read so a
    /// client can tell "not liked" apart from "nobody asked".
    /// </summary>
    private async Task EnrichWithUserInteractionsAsync(IEnumerable<TrainingPlanDto> plans, Guid? userId)
    {
        if (!userId.HasValue) return;

        var planList = plans.ToList();
        if (planList.Count == 0) return;

        var planIds = planList.Select(p => p.Id).ToList();

        // Sequential on purpose: both repositories resolve the same scoped DbContext, and
        // EF throws "A second operation was started on this context instance" the moment
        // the two queries overlap.
        var likedSet = (await _likeRepository.GetUserLikedPlanIdsAsync(userId.Value, planIds)).ToHashSet();
        var bookmarkedSet = (await _bookmarkRepository.GetUserBookmarkedPlanIdsAsync(userId.Value, planIds)).ToHashSet();

        foreach (var plan in planList)
        {
            plan.IsLiked = likedSet.Contains(plan.Id);
            plan.IsBookmarked = bookmarkedSet.Contains(plan.Id);
        }
    }

    /// <summary>
    /// Puts names to the plan's coaches — its own and every station's — in a single lookup, so
    /// a practice split into ten groups still costs one query rather than eleven.
    /// </summary>
    private async Task EnrichWithCoachNamesAsync(TrainingPlanDetailDto plan)
    {
        var coaches = plan.Coaches
            .Concat(plan.Items.SelectMany(i => i.Stations).SelectMany(st => st.Coaches))
            .ToList();

        await _planCoachService.ResolveNamesAsync(coaches);
    }

    private async Task EnrichWithClubInfoAsync(IEnumerable<TrainingPlanDto> plans)
    {
        var planList = plans.ToList();
        if (planList.Count == 0) return;

        // Collect all club IDs that need to be fetched
        var clubIds = planList
            .Where(t => t.ClubId.HasValue)
            .Select(t => t.ClubId!.Value)
            .Distinct()
            .ToList();

        if (clubIds.Count == 0) return;

        try
        {
            // Batch fetch club info
            var clubInfos = await _clubsClient.GetClubInfoAsync(clubIds);

            // Enrich DTOs
            foreach (var plan in planList)
            {
                if (plan.ClubId.HasValue && clubInfos.TryGetValue(plan.ClubId.Value, out var clubInfo))
                {
                    plan.ClubName = clubInfo.Name;
                    plan.ClubLogoUrl = clubInfo.LogoUrl;
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - club info is nice-to-have
            _logger.LogWarning(ex, "Failed to enrich plans with club info");
        }
    }
}
