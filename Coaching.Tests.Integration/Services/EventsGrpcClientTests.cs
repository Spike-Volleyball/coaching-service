using Coaching.Application.Interfaces.Services;
using Coaching.Infrastructure.Services;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shared.Contracts.Grpc;

namespace Coaching.Tests.Integration.Services;

/// <summary>
/// GetEventContext shipped as a stub that answered "TrainingSession / None" for every event
/// without asking events-service, so the club and unit branches of the feedback rules were
/// unreachable on every environment. These pin the real call and its mapping, and the page-wide
/// summary call the feedback lists are built from.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EventsGrpcClientTests
{
    [Test]
    public async Task GetEventContextAsync_ClubEvent_AnswersTypeContextAndClubId()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var clubId = Guid.NewGuid();
        var grpcClient = GrpcClientAnswering(new GetEventContextResponse
        {
            Found = true,
            EventType = "Match",
            ContextType = "Club",
            ContextId = clubId.ToString()
        });
        var sut = BuildSut(grpcClient);

        // Act
        var context = await sut.GetEventContextAsync(eventId);

        // Assert — not awaited: the generated method answers AsyncUnaryCall, which is null
        // while the substitute is being queried rather than called.
        context.Should().Be(new EventContext("Match", "Club", clubId));
        grpcClient.Received(1).GetEventContextAsync(
            Arg.Is<GetEventContextRequest>(r => r.EventId == eventId.ToString()),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [TestCase("Team")]
    [TestCase("Group")]
    public async Task GetEventContextAsync_UnitEvent_AnswersTheUnit(string contextType)
    {
        // Arrange
        var unitId = Guid.NewGuid();
        var sut = BuildSut(GrpcClientAnswering(new GetEventContextResponse
        {
            Found = true,
            EventType = "TrainingSession",
            ContextType = contextType,
            ContextId = unitId.ToString()
        }));

        // Act
        var context = await sut.GetEventContextAsync(Guid.NewGuid());

        // Assert
        context.Should().Be(new EventContext("TrainingSession", contextType, unitId));
    }

    [Test]
    public async Task GetEventContextAsync_EventWithoutContext_LeavesContextIdNull()
    {
        // Arrange
        var sut = BuildSut(GrpcClientAnswering(new GetEventContextResponse
        {
            Found = true,
            EventType = "CasualPlay",
            ContextType = "None",
            ContextId = ""
        }));

        // Act
        var context = await sut.GetEventContextAsync(Guid.NewGuid());

        // Assert
        context.Should().Be(new EventContext("CasualPlay", "None", null));
    }

    [Test]
    public async Task GetEventContextAsync_UnknownEvent_ReturnsNull()
    {
        // Arrange
        var sut = BuildSut(GrpcClientAnswering(new GetEventContextResponse { Found = false }));

        // Act
        var context = await sut.GetEventContextAsync(Guid.NewGuid());

        // Assert
        context.Should().BeNull();
    }

    [Test]
    public async Task GetEventContextAsync_AskedTwice_AsksEventsServiceOnce()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var grpcClient = GrpcClientAnswering(new GetEventContextResponse
        {
            Found = true,
            EventType = "Trial",
            ContextType = "Team",
            ContextId = Guid.NewGuid().ToString()
        });
        var sut = BuildSut(grpcClient);

        // Act
        var first = await sut.GetEventContextAsync(eventId);
        var second = await sut.GetEventContextAsync(eventId);

        // Assert
        second.Should().Be(first);
        grpcClient.Received(1).GetEventContextAsync(
            Arg.Any<GetEventContextRequest>(),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEventContextAsync_UnknownEvent_IsAskedAgainNextTime()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var grpcClient = GrpcClientAnswering(new GetEventContextResponse { Found = false });
        var sut = BuildSut(grpcClient);

        // Act
        await sut.GetEventContextAsync(eventId);
        await sut.GetEventContextAsync(eventId);

        // Assert
        grpcClient.Received(2).GetEventContextAsync(
            Arg.Any<GetEventContextRequest>(),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEventContextAsync_EventsServiceUnreachable_Throws()
    {
        // Arrange
        var grpcClient = Substitute.For<EventsInternalService.EventsInternalServiceClient>();
        grpcClient.GetEventContextAsync(
                Arg.Any<GetEventContextRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "down")));
        var sut = BuildSut(grpcClient);

        // Act
        var act = () => sut.GetEventContextAsync(Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<RpcException>();
    }

    [Test]
    public async Task GetEventInfoAsync_AsksOnceForTheDistinctIds_AndKeysTheAnswerById()
    {
        // Arrange
        var friday = Guid.NewGuid();
        var cup = Guid.NewGuid();
        var grpcClient = Substitute.For<EventsInternalService.EventsInternalServiceClient>();
        grpcClient.GetEventSummariesAsync(
                Arg.Any<GetEventSummariesRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(CompletedCall(Summaries(
                Summary(friday, "Friday session", "2026-09-11T18:30:00.0000000Z", "TrainingSession"),
                Summary(cup, "Cup quarter-final", "2026-09-13T14:00:00.0000000Z", "Match"))));
        var sut = BuildSut(grpcClient);

        // Act
        var info = await sut.GetEventInfoAsync([friday, cup, friday, Guid.Empty]);

        // Assert
        info.Should().BeEquivalentTo(new Dictionary<Guid, EventInfo>
        {
            [friday] = new(friday, "Friday session", new DateTime(2026, 9, 11, 18, 30, 0, DateTimeKind.Utc), "TrainingSession"),
            [cup] = new(cup, "Cup quarter-final", new DateTime(2026, 9, 13, 14, 0, 0, DateTimeKind.Utc), "Match"),
        });
        info[friday].StartTime.Kind.Should().Be(DateTimeKind.Utc);
        grpcClient.Received(1).GetEventSummariesAsync(
            Arg.Is<GetEventSummariesRequest>(r =>
                r.EventIds.Count == 2
                && r.EventIds.Contains(friday.ToString())
                && r.EventIds.Contains(cup.ToString())),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEventInfoAsync_NoIds_AsksNothing()
    {
        // Arrange
        var grpcClient = Substitute.For<EventsInternalService.EventsInternalServiceClient>();
        var sut = BuildSut(grpcClient);

        // Act
        var info = await sut.GetEventInfoAsync([Guid.Empty]);

        // Assert
        info.Should().BeEmpty();
        grpcClient.DidNotReceiveWithAnyArgs().GetEventSummariesAsync(default!, default!, default, default);
    }

    [Test]
    public async Task GetEventInfoAsync_EventsServiceUnreachable_NamesNothing()
    {
        // Arrange
        var grpcClient = Substitute.For<EventsInternalService.EventsInternalServiceClient>();
        grpcClient.GetEventSummariesAsync(
                Arg.Any<GetEventSummariesRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "down")));
        var sut = BuildSut(grpcClient);

        // Act
        var info = await sut.GetEventInfoAsync([Guid.NewGuid()]);

        // Assert
        info.Should().BeEmpty();
    }

    private static EventSummary Summary(Guid eventId, string name, string startTime, string eventType) => new()
    {
        EventId = eventId.ToString(),
        Name = name,
        StartTime = startTime,
        EventType = eventType
    };

    private static GetEventSummariesResponse Summaries(params EventSummary[] summaries)
    {
        var response = new GetEventSummariesResponse();
        response.Events.AddRange(summaries);
        return response;
    }

    private static EventsInternalService.EventsInternalServiceClient GrpcClientAnswering(
        GetEventContextResponse response)
    {
        var grpcClient = Substitute.For<EventsInternalService.EventsInternalServiceClient>();
        grpcClient.GetEventContextAsync(
                Arg.Any<GetEventContextRequest>(),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(CompletedCall(response));
        return grpcClient;
    }

    private static EventsGrpcClient BuildSut(EventsInternalService.EventsInternalServiceClient grpcClient) =>
        new(grpcClient,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<ILogger<EventsGrpcClient>>());

    private static AsyncUnaryCall<T> CompletedCall<T>(T response) => new(
        Task.FromResult(response),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => new Metadata(),
        () => { });
}
