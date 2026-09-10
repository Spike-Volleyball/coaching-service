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
/// unreachable on every environment. These pin the real call and its mapping.
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
