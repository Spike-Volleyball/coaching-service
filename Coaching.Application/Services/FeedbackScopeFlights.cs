using System.Collections.Concurrent;
using Coaching.Application.Interfaces.Services;

namespace Coaching.Application.Services;

/// <summary>
/// Resolutions of one feedback scope for one caller, shared between the requests that ask the
/// same thing at once. Clients from before the batch endpoint ask can-create once per roster
/// member, all together: on 2026-09-23 twenty-two identical resolutions in flight took
/// events-service's database pool to its limit and each took a second. Concurrent askers now
/// share one resolution, and a landed one keeps answering for <see cref="AnswersFor"/> so the
/// tail of a burst finds it too.
///
/// Keyed by caller as well as scope: a resolution is that caller's standing, and is never
/// anyone else's answer. A resolution that fails is dropped, so the next asker starts again.
/// </summary>
public sealed class FeedbackScopeFlights(TimeProvider timeProvider)
{
    public static readonly TimeSpan AnswersFor = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(FeedbackScope Scope, Guid CallerId), Flight> _flights = new();

    /// <summary>
    /// The resolution already under way or recently landed for this scope and caller, with
    /// <c>Joined</c> set; or a new one started with <paramref name="resolve"/>, without it. Every
    /// asker of one flight passes the same <typeparamref name="T"/>, because only one call site
    /// resolves scopes.
    /// </summary>
    public (Task<T> Resolution, bool Joined) Join<T>(FeedbackScope scope, Guid callerId, Func<Task<T>> resolve)
    {
        var key = (scope, callerId);
        while (true)
        {
            var now = timeProvider.GetUtcNow();
            if (_flights.TryGetValue(key, out var current))
            {
                if (current.AnswersAt(now))
                    return ((Task<T>)current.Resolution.Value, true);

                _flights.TryRemove(KeyValuePair.Create(key, current));
                continue;
            }

            DropExpired(now);
            var flight = new Flight(new Lazy<Task>(() => resolve()));
            if (!_flights.TryAdd(key, flight))
                continue;

            var resolution = (Task<T>)flight.Resolution.Value;
            _ = LandAsync(key, flight, resolution);
            return (resolution, false);
        }
    }

    private async Task LandAsync((FeedbackScope, Guid) key, Flight flight, Task resolution)
    {
        try
        {
            await resolution;
            flight.Land(timeProvider.GetUtcNow());
        }
        catch
        {
            // Its askers each see the failure through their own await; nobody later inherits it.
            _flights.TryRemove(KeyValuePair.Create(key, flight));
        }
    }

    // Flights outlive their window until something replaces them; one pass as a new flight
    // starts keeps the map to the scopes asked about in the last few seconds.
    private void DropExpired(DateTimeOffset now)
    {
        foreach (var entry in _flights)
        {
            if (!entry.Value.AnswersAt(now))
                _flights.TryRemove(entry);
        }
    }

    private sealed class Flight(Lazy<Task> resolution)
    {
        private long _landedAtTicks;

        public Lazy<Task> Resolution { get; } = resolution;

        public void Land(DateTimeOffset at) => Interlocked.Exchange(ref _landedAtTicks, at.UtcTicks);

        public bool AnswersAt(DateTimeOffset now)
        {
            if (Resolution.IsValueCreated && Resolution.Value is { IsFaulted: true } or { IsCanceled: true })
                return false;

            var landedAt = Interlocked.Read(ref _landedAtTicks);
            return landedAt == 0 || now.UtcTicks - landedAt < AnswersFor.Ticks;
        }
    }
}
