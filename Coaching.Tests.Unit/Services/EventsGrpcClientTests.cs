using Coaching.Infrastructure.Services;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shared.Contracts.Grpc;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class EventsGrpcClientTests : UnitTestBase
{
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid OnTheRoster = Guid.NewGuid();
    private static readonly Guid JustInvited = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private EventsInternalService.EventsInternalServiceClient _grpc = null!;
    private MemoryCache _cache = null!;
    private EventsGrpcClient _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _grpc = Substitute.For<EventsInternalService.EventsInternalServiceClient>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _sut = new EventsGrpcClient(_grpc, _cache, NullLogger<EventsGrpcClient>.Instance);
    }

    [TearDown]
    public override void TearDown()
    {
        _cache.Dispose();
        base.TearDown();
    }

    [Test]
    public async Task IsEventParticipantAsync_SomeoneInvitedAfterTheRosterWasCached_IsFound()
    {
        // Arrange — the roster is cached before the invitation lands.
        RostersInOrder([OnTheRoster], [OnTheRoster, JustInvited]);
        await _sut.IsEventParticipantAsync(EventId, OnTheRoster);

        // Act
        var (isParticipant, eventExists) = await _sut.IsEventParticipantAsync(EventId, JustInvited);

        // Assert
        isParticipant.Should().BeTrue();
        eventExists.Should().BeTrue();
        RosterReads().Should().Be(2);
    }

    [Test]
    public async Task IsEventParticipantAsync_SomeoneOnTheCachedRoster_IsAnsweredFromTheCache()
    {
        // Arrange
        RostersInOrder([OnTheRoster]);
        await _sut.IsEventParticipantAsync(EventId, OnTheRoster);

        // Act
        var (isParticipant, _) = await _sut.IsEventParticipantAsync(EventId, OnTheRoster);

        // Assert
        isParticipant.Should().BeTrue();
        RosterReads().Should().Be(1);
    }

    [Test]
    public async Task IsEventParticipantAsync_AStranger_IsRefusedAfterASingleRecheck()
    {
        // Arrange
        RostersInOrder([OnTheRoster], [OnTheRoster]);
        await _sut.IsEventParticipantAsync(EventId, OnTheRoster);

        // Act
        var (isParticipant, _) = await _sut.IsEventParticipantAsync(EventId, Stranger);

        // Assert
        isParticipant.Should().BeFalse();
        RosterReads().Should().Be(2);
    }

    [Test]
    public async Task IsEventParticipantAsync_AfterARecheck_TheFreshRosterIsWhatGetsCached()
    {
        // Arrange
        RostersInOrder([OnTheRoster], [OnTheRoster, JustInvited]);
        await _sut.IsEventParticipantAsync(EventId, OnTheRoster);
        await _sut.IsEventParticipantAsync(EventId, JustInvited);

        // Act
        var (isParticipant, _) = await _sut.IsEventParticipantAsync(EventId, JustInvited);

        // Assert
        isParticipant.Should().BeTrue();
        RosterReads().Should().Be(2);
    }

    [Test]
    public async Task GetEventParticipantIdsAsync_AskingAboutSeveral_RechecksWhenAnyOfThemIsMissing()
    {
        // Arrange
        RostersInOrder([OnTheRoster], [OnTheRoster, JustInvited]);
        await _sut.GetEventParticipantIdsAsync(EventId, [OnTheRoster]);

        // Act
        var roster = await _sut.GetEventParticipantIdsAsync(EventId, [OnTheRoster, JustInvited]);

        // Assert
        roster.Should().BeEquivalentTo([OnTheRoster, JustInvited]);
        RosterReads().Should().Be(2);
    }

    private void RostersInOrder(params Guid[][] rosters)
    {
        var calls = rosters.Select(Roster).ToArray();
        _grpc.GetEventParticipantsAsync(
                Arg.Any<GetEventParticipantsRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(calls[0], calls[1..]);
    }

    private int RosterReads() => _grpc.ReceivedCalls()
        .Count(call => call.GetMethodInfo().Name == nameof(EventsInternalService.EventsInternalServiceClient.GetEventParticipantsAsync));

    private static AsyncUnaryCall<GetEventParticipantsResponse> Roster(Guid[] userIds)
    {
        var response = new GetEventParticipantsResponse();
        response.Participants.AddRange(userIds.Select(id => new ParticipantInfo { UserId = id.ToString() }));

        return new AsyncUnaryCall<GetEventParticipantsResponse>(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }
}
