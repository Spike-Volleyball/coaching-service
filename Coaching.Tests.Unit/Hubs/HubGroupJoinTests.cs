using System.Security.Claims;
using Coaching.Application.Interfaces.Services;
using Coaching.Authorization;
using Coaching.Hubs;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shared.DataAccess.Providers;
using Shared.DataAccess.Providers.Interfaces;
using Shared.Security.Access;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Hubs;

/// <summary>
/// Anyone signed in could join any evaluation session's room and hear every score submitted in
/// it, or any event's training-run room (SPI-6437). Joining now asks the room's authority, as
/// reading the session or the run does.
/// </summary>
[TestFixture]
[Category("Unit")]
public class HubGroupJoinTests : UnitTestBase
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _resourceId = Guid.NewGuid();

    private IResourceAuthority<EvaluationSessionAccess> _sessions = null!;
    private IResourceAuthority<RunAccess> _runs = null!;
    private IGroupManager _groups = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _sessions = Substitute.For<IResourceAuthority<EvaluationSessionAccess>>();
        _runs = Substitute.For<IResourceAuthority<RunAccess>>();
        _groups = Substitute.For<IGroupManager>();
    }

    [Test]
    public async Task JoinSession_ASessionTheCallerMayRead_JoinsItsRoom()
    {
        // Arrange
        _sessions.CanAsync(_userId, _resourceId, EvaluationSessionAccess.Read, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await Signed(new EvaluationHub(Services(), Substitute.For<IEvaluationScoringService>())).JoinSession(_resourceId);

        // Assert
        await _groups.Received(1).AddToGroupAsync("connection-1", EvaluationHub.SessionGroup(_resourceId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task JoinSession_ASessionTheCallerMayNotRead_IsRefusedAndJoinsNothing()
    {
        // Arrange
        _sessions.CanAsync(_userId, _resourceId, EvaluationSessionAccess.Read, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var act = () => Signed(new EvaluationHub(Services(), Substitute.For<IEvaluationScoringService>())).JoinSession(_resourceId);

        // Assert
        await act.Should().ThrowAsync<HubException>();
        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task JoinRun_ACallerWhoMayControlTheRun_JoinsTheControllersRoomOnly()
    {
        // Arrange
        _runs.CanAsync(_userId, _resourceId, RunAccess.Control, Arg.Any<CancellationToken>()).Returns(true);
        _runs.CanAsync(_userId, _resourceId, RunAccess.Read, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await Signed(RunHub()).JoinRun(_resourceId);

        // Assert
        await _groups.Received(1).AddToGroupAsync("connection-1", TrainingRunHub.ControllersGroup(_resourceId), Arg.Any<CancellationToken>());
        await _groups.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), TrainingRunHub.ViewersGroup(_resourceId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task JoinRun_ACallerWhoMayOnlyWatch_JoinsTheViewersRoomOnly()
    {
        // Arrange — a player: every phone watching got the coach's controls on 09-28.
        _runs.CanAsync(_userId, _resourceId, RunAccess.Control, Arg.Any<CancellationToken>()).Returns(false);
        _runs.CanAsync(_userId, _resourceId, RunAccess.Read, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        await Signed(RunHub()).JoinRun(_resourceId);

        // Assert
        await _groups.Received(1).AddToGroupAsync("connection-1", TrainingRunHub.ViewersGroup(_resourceId), Arg.Any<CancellationToken>());
        await _groups.DidNotReceive().AddToGroupAsync(Arg.Any<string>(), TrainingRunHub.ControllersGroup(_resourceId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task JoinRun_ARunTheCallerMayNotWatch_IsRefusedAndJoinsNothing()
    {
        // Arrange
        _runs.CanAsync(_userId, _resourceId, Arg.Any<RunAccess>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var act = () => Signed(RunHub()).JoinRun(_resourceId);

        // Assert
        await act.Should().ThrowAsync<HubException>();
        await _groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task LeaveRun_LeavesWhicheverRoomTheCallerWasIn()
    {
        // Act
        await Signed(RunHub()).LeaveRun(_resourceId);

        // Assert
        await _groups.Received(1).RemoveFromGroupAsync("connection-1", TrainingRunHub.ControllersGroup(_resourceId), Arg.Any<CancellationToken>());
        await _groups.Received(1).RemoveFromGroupAsync("connection-1", TrainingRunHub.ViewersGroup(_resourceId), Arg.Any<CancellationToken>());
    }

    [Test]
    public void TheRunRooms_AreNamedPerEventAndAudience()
    {
        // Assert
        TrainingRunHub.ControllersGroup(_resourceId).Should().Be($"run:{_resourceId}:controllers");
        TrainingRunHub.ViewersGroup(_resourceId).Should().Be($"run:{_resourceId}:viewers");
    }

    private TrainingRunHub RunHub() => new(Services(), _runs, new JwtPayloadProvider());

    private IServiceProvider Services() =>
        new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_runs)
            .AddScoped<IJwtPayloadProvider, JwtPayloadProvider>()
            .BuildServiceProvider();

    private THub Signed<THub>(THub hub) where THub : Hub
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", _userId.ToString())], "Bearer"));
        hub.Context = new Caller(user);
        hub.Groups = _groups;
        hub.Clients = Substitute.For<IHubCallerClients>();
        return hub;
    }

    private sealed class Caller(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "connection-1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
