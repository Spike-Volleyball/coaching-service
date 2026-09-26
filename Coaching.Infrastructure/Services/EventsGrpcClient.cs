using System.Globalization;
using Coaching.Application.Interfaces.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shared.Contracts.Grpc;

namespace Coaching.Infrastructure.Services;

/// <summary>
/// gRPC client for events-service authorization checks and event summaries with in-memory caching.
/// An event's roster and its context are cached for 5 minutes to reduce cross-service calls.
/// </summary>
public class EventsGrpcClient : IEventsGrpcClient
{
    private readonly EventsInternalService.EventsInternalServiceClient _grpcClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EventsGrpcClient> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const string ParticipantsCacheKeyPrefix = "event_participants_";
    private const string EventContextCacheKeyPrefix = "event_context_";

    public EventsGrpcClient(
        EventsInternalService.EventsInternalServiceClient grpcClient,
        IMemoryCache cache,
        ILogger<EventsGrpcClient> logger)
    {
        _grpcClient = grpcClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool> IsEventAdminAsync(Guid eventId, Guid userId)
    {
        try
        {
            var response = await _grpcClient.IsEventAdminAsync(new IsEventAdminRequest
            {
                EventId = eventId.ToString(),
                UserId = userId.ToString()
            });
            return response.IsAdmin;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check event admin status via gRPC for event {EventId}, user {UserId}",
                eventId, userId);
            throw;
        }
    }

    public async Task<(bool IsParticipant, bool EventExists)> IsEventParticipantAsync(Guid eventId, Guid userId)
    {
        var participants = await GetEventParticipantIdsAsync(eventId, [userId]);
        // A roster that came back proves the event exists.
        return (participants.Contains(userId), true);
    }

    public async Task<IReadOnlySet<Guid>> GetEventParticipantIdsAsync(Guid eventId, IReadOnlyCollection<Guid> askingAbout)
    {
        var cacheKey = $"{ParticipantsCacheKeyPrefix}{eventId}";

        // Nothing evicts the roster when someone is invited, so a copy missing a person being
        // asked about may simply be older than their invitation. Only a hit is trusted; a miss
        // costs one fresh read, which then replaces the cached copy.
        if (_cache.TryGetValue(cacheKey, out IReadOnlySet<Guid>? cached) && cached != null
            && askingAbout.All(cached.Contains))
            return cached;

        try
        {
            var response = await _grpcClient.GetEventParticipantsAsync(new GetEventParticipantsRequest
            {
                EventId = eventId.ToString()
            });

            var participants = response.Participants.Select(p => Guid.Parse(p.UserId)).ToHashSet();
            _cache.Set(cacheKey, participants, CacheDuration);
            return participants;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch event participants via gRPC for event {EventId}", eventId);
            throw;
        }
    }

    public async Task<EventContext?> GetEventContextAsync(Guid eventId)
    {
        var cacheKey = $"{EventContextCacheKeyPrefix}{eventId}";

        if (_cache.TryGetValue(cacheKey, out EventContext? cached) && cached != null)
            return cached;

        try
        {
            var response = await _grpcClient.GetEventContextAsync(new GetEventContextRequest
            {
                EventId = eventId.ToString()
            });

            if (!response.Found)
                return null;

            var context = new EventContext(
                response.EventType,
                response.ContextType,
                Guid.TryParse(response.ContextId, out var contextId) ? contextId : null);
            _cache.Set(cacheKey, context, CacheDuration);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get event context via gRPC for event {EventId}", eventId);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<Guid, EventInfo>> GetEventInfoAsync(IReadOnlyCollection<Guid> eventIds)
    {
        var ids = eventIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, EventInfo>();

        try
        {
            var request = new GetEventSummariesRequest();
            request.EventIds.AddRange(ids.Select(id => id.ToString()));
            var response = await _grpcClient.GetEventSummariesAsync(request);

            return response.Events.ToDictionary(
                e => Guid.Parse(e.EventId),
                e => new EventInfo(
                    Guid.Parse(e.EventId),
                    e.Name,
                    DateTime.Parse(e.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    e.EventType));
        }
        catch (Exception ex)
        {
            // A list that cannot name its sessions is still a list: a row without a summary is
            // rendered from the event id by the clients, as every row was before.
            _logger.LogError(ex, "Failed to fetch summaries for {EventCount} events via gRPC", ids.Count);
            return new Dictionary<Guid, EventInfo>();
        }
    }
}
