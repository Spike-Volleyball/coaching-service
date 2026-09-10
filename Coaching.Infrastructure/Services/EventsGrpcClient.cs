using Coaching.Application.Interfaces.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Shared.Contracts.Grpc;

namespace Coaching.Infrastructure.Services;

/// <summary>
/// gRPC client for events-service authorization checks with in-memory caching.
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
        var participants = await GetEventParticipantIdsAsync(eventId);
        // A roster that came back proves the event exists.
        return (participants.Contains(userId), true);
    }

    public async Task<IReadOnlySet<Guid>> GetEventParticipantIdsAsync(Guid eventId)
    {
        var cacheKey = $"{ParticipantsCacheKeyPrefix}{eventId}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlySet<Guid>? cached) && cached != null)
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
}
