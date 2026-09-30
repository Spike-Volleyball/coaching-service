using FluentAssertions;
using NSubstitute;
using NSubstitute.Core;
using Shared.Services.Analytics;

namespace Coaching.Tests.Unit.Analytics;

/// <summary>
/// What every analytics test asserts: this event, exactly once, against this acting user, with
/// these properties — or nothing at all when the operation refused. Reading the recorded calls
/// says that more plainly than an argument matcher spread across four lines does.
/// </summary>
public static class AnalyticsAssertions
{
    public static IReadOnlyDictionary<string, object?> CapturedOnce(
        this IAnalyticsCapture analytics, string eventName, Guid userId)
    {
        var matching = CaptureCalls(analytics)
            .Where(arguments => (string)arguments[1]! == eventName)
            .ToList();

        matching.Should().HaveCount(1, "{0} is emitted exactly once", eventName);
        ((Guid)matching[0][0]!).Should().Be(userId, "the acting user is the distinct_id");

        return (IReadOnlyDictionary<string, object?>)matching[0][2]!;
    }

    /// <summary>Every capture of <paramref name="eventName"/>, in the order they were made.</summary>
    public static List<(Guid UserId, IReadOnlyDictionary<string, object?> Properties)> CapturedEach(
        this IAnalyticsCapture analytics, string eventName) =>
        CaptureCalls(analytics)
            .Where(arguments => (string)arguments[1]! == eventName)
            .Select(arguments => ((Guid)arguments[0]!, (IReadOnlyDictionary<string, object?>)arguments[2]!))
            .ToList();

    public static void CapturedNothing(this IAnalyticsCapture analytics) =>
        CaptureCalls(analytics).Should().BeEmpty("a failed operation is not a fact worth recording");

    public static void CapturedNone(this IAnalyticsCapture analytics, string eventName) =>
        CaptureCalls(analytics).Should().NotContain(
            arguments => (string)arguments[1]! == eventName, "{0} is not what happened", eventName);

    private static List<object?[]> CaptureCalls(IAnalyticsCapture analytics) =>
        analytics.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAnalyticsCapture.Capture))
            .Select(call => call.GetArguments())
            .ToList();
}
