using Coaching.Application.DTOs.Templates;
using Coaching.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Coaching.Tests.Unit.Hubs;

/// <summary>
/// On 09-28 every phone watching a run got the coach's controls: the broadcast carried the
/// canControl of whoever made the change to everyone. Each room now gets its own copy, and the
/// builds in the field — which write a broadcast straight into their cache — read the right one.
/// </summary>
[TestFixture]
[Category("Unit")]
public class SignalRRunBroadcasterTests
{
    private readonly Guid _eventId = Guid.NewGuid();

    private IClientProxy _controllers = null!;
    private IClientProxy _viewers = null!;
    private SignalRRunBroadcaster _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _controllers = Substitute.For<IClientProxy>();
        _viewers = Substitute.For<IClientProxy>();

        var hub = Substitute.For<IHubContext<TrainingRunHub>>();
        hub.Clients.Group(TrainingRunHub.ControllersGroup(_eventId)).Returns(_controllers);
        hub.Clients.Group(TrainingRunHub.ViewersGroup(_eventId)).Returns(_viewers);
        _sut = new SignalRRunBroadcaster(hub);
    }

    [Test]
    public async Task BroadcastRunUpdatedAsync_TheControllersRoom_GetsTheRunWithControls()
    {
        // Act
        await _sut.BroadcastRunUpdatedAsync(_eventId, Run(canControl: false));

        // Assert
        SentTo(_controllers).CanControl.Should().BeTrue();
    }

    [Test]
    public async Task BroadcastRunUpdatedAsync_TheViewersRoom_GetsTheRunWithoutControls()
    {
        // Act — the controller who made the change is answered with controls; nobody else is.
        await _sut.BroadcastRunUpdatedAsync(_eventId, Run(canControl: true));

        // Assert
        SentTo(_viewers).CanControl.Should().BeFalse();
    }

    [Test]
    public async Task BroadcastRunUpdatedAsync_EachRoom_GetsItsOwnCopyOfTheSameRun()
    {
        // Arrange
        var run = Run(canControl: true);

        // Act
        await _sut.BroadcastRunUpdatedAsync(_eventId, run);

        // Assert
        var toControllers = SentTo(_controllers);
        var toViewers = SentTo(_viewers);
        toControllers.Should().NotBeSameAs(toViewers);
        toControllers.Should().BeEquivalentTo(run);
        toViewers.Should().BeEquivalentTo(run, o => o.Excluding(r => r.CanControl));
        run.CanControl.Should().BeTrue("the caller's own answer is not the viewers'");
    }

    private static RunDto SentTo(IClientProxy room)
    {
        var call = room.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync));
        var arguments = call.GetArguments();
        arguments[0].Should().Be("RunUpdated");
        return ((object?[])arguments[1]!).Single().Should().BeOfType<RunDto>().Subject;
    }

    private RunDto Run(bool canControl) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = Guid.NewGuid(),
        EventId = _eventId,
        CurrentItemId = Guid.NewGuid(),
        CanControl = canControl,
        Items = [new RunItemDto { Id = Guid.NewGuid(), PlanItemId = Guid.NewGuid(), Order = 1 }]
    };
}
