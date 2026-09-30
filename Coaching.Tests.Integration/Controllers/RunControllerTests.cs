using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Templates;
using Coaching.Hubs;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shared.Models;

namespace Coaching.Tests.Integration.Controllers;

[TestFixture]
[Category("Integration")]
public class RunControllerTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly Guid CreatorId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _factory.DisposeAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
        _factory.EventsGrpcClient.ClearReceivedCalls();
        _factory.RunBroadcaster.ClearReceivedCalls();
    }

    [Test]
    public async Task GetRun_NoRunStarted_Returns200WithNull()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        StubParticipant(eventId);
        SetAuth(CreatorId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Trim().Should().BeOneOf("null", string.Empty);
    }

    [Test]
    public async Task GetRun_Unauthenticated_Returns401()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetRun_AsPlanCreator_Returns200WithCanControlTrue()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CanControl.Should().BeTrue();
    }

    [Test]
    public async Task GetRun_AsEventParticipant_Returns200WithCanControlFalse()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, OtherUserId).Returns((true, true));
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CanControl.Should().BeFalse();
    }

    [Test]
    public async Task GetRun_AsEventAdmin_Returns200WithCanControlTrue()
    {
        // Arrange — a co-host who did not write the plan.
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        StubEventAdmin(eventId, OtherUserId);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CanControl.Should().BeTrue();
    }

    [Test]
    public async Task GetRun_AsUnrelatedUser_Returns403()
    {
        // Arrange — not the creator, not a participant, not an event host.
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, OtherUserId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventAdminAsync(eventId, OtherUserId).Returns(false);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GetRun_AsUnrelatedUser_NoRunStarted_Returns403SameAsWhenRunExists()
    {
        // Arrange — regression guard: an unauthorized caller must get the identical response
        // whether or not a run has been started, so the run's existence never leaks. Only
        // difference from GetRun_AsUnrelatedUser_Returns403 above is that /run/start is never
        // called here.
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, OtherUserId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventAdminAsync(eventId, OtherUserId).Returns(false);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GetRun_EventDoesNotExist_Returns404()
    {
        // Arrange — mirrors TrainingPlanService.GetByEventIdAsync's eventExists-404 vs
        // not-participant-403 distinction. No plan/run was ever seeded for this eventId.
        var eventId = Guid.NewGuid();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, OtherUserId).Returns((false, false));
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task StartRun_AsPlanCreator_Returns200RunningWithFirstItemCurrent()
    {
        // Arrange
        var (eventId, item1Id, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(item1Id);
        run.CanControl.Should().BeTrue();
        run.Items.Should().HaveCount(2);
        run.ServerTime.Should().NotBe(default);
    }

    [Test]
    public async Task StartRun_AsNonCreator_Returns403()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(OtherUserId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task StartRun_AsEventAdmin_Returns200Running()
    {
        // Arrange
        var (eventId, item1Id, _) = await SeedPlanWithTwoItemsAsync();
        StubEventAdmin(eventId, OtherUserId);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(item1Id);
        run.CanControl.Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StartRun_WhileTheRunIsInProgress_WithoutRestart_Returns409AndLeavesItAlone(bool paused)
    {
        // Arrange
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item1Id), JsonOptions);
        if (paused)
            await _client.PostAsync($"/v1/events/{eventId}/plans/run/pause", null);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var run = await GetRunAsync(eventId);
        run.CurrentItemId.Should().Be(item2Id);
        run.Status.Should().Be(paused ? RunStatus.Paused : RunStatus.Running);
    }

    [Test]
    public async Task StartRun_WhileTheRunIsInProgress_WithRestart_Returns200OnTheFirstItem()
    {
        // Arrange
        var (eventId, item1Id, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item1Id), JsonOptions);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/start", new StartRunDto(Restart: true), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CurrentItemId.Should().Be(item1Id);
        run.Items.Should().OnlyContain(i => i.CompletedAt == null && i.ActualElapsedSeconds == 0);
    }

    [Test]
    public async Task StartRun_AfterTheRunCompleted_WithNoBody_Returns200OnTheFirstItem()
    {
        // Arrange — the Start Over of the app builds already in the field sends no body.
        var (eventId, item1Id, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/complete", null);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(item1Id);
    }

    // ---------- Permissions ----------

    [Test]
    public async Task GetRunPermissions_AsPlanCreator_BeforeAnyRun_ReturnsCanControlTrue()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run/permissions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RunPermissionsDto>(JsonOptions))!.CanControl.Should().BeTrue();
    }

    [Test]
    public async Task GetRunPermissions_AsEventAdmin_BeforeAnyPlan_ReturnsCanControlTrue()
    {
        // Arrange — nothing has been attached to the event yet.
        var eventId = Guid.NewGuid();
        StubEventAdmin(eventId, OtherUserId);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run/permissions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RunPermissionsDto>(JsonOptions))!.CanControl.Should().BeTrue();
    }

    [Test]
    public async Task GetRunPermissions_AsParticipant_ReturnsCanControlFalse()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        StubParticipant(eventId);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run/permissions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RunPermissionsDto>(JsonOptions))!.CanControl.Should().BeFalse();
    }

    [Test]
    public async Task GetRunPermissions_ForAnEventThatDoesNotExist_AnswersLikeAnyOtherEventTheCallerCannotRun()
    {
        // Arrange — a stranger learns nothing: a real event they have no part in answers the same.
        var (realEventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(OtherUserId);

        // Act
        var real = await _client.GetAsync($"/v1/events/{realEventId}/plans/run/permissions");
        var missing = await _client.GetAsync($"/v1/events/{Guid.NewGuid()}/plans/run/permissions");

        // Assert
        real.StatusCode.Should().Be(HttpStatusCode.OK);
        missing.StatusCode.Should().Be(HttpStatusCode.OK);
        (await missing.Content.ReadAsStringAsync()).Should().Be(await real.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task GetRunPermissions_Unauthenticated_Returns401()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run/permissions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task StartRun_NoPlanForEvent_Returns404()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task StartRun_BroadcastsRunUpdated()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        await _factory.RunBroadcaster.Received(1)
            .BroadcastRunUpdatedAsync(eventId, Arg.Is<RunDto>(d => d.Status == RunStatus.Running));
    }

    [Test]
    public async Task PauseResume_RoundTrips()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Act
        var pauseResp = await _client.PostAsync($"/v1/events/{eventId}/plans/run/pause", null);
        var resumeResp = await _client.PostAsync($"/v1/events/{eventId}/plans/run/resume", null);

        // Assert
        var paused = await pauseResp.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        paused!.Status.Should().Be(RunStatus.Paused);
        paused.CurrentItemStartedAt.Should().BeNull();

        var resumed = await resumeResp.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        resumed!.Status.Should().Be(RunStatus.Running);
        resumed.CurrentItemStartedAt.Should().NotBeNull();
    }

    [Test]
    public async Task Advance_ThenAdvanceLast_CompletesRun()
    {
        // Arrange
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Act
        var first = await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item1Id), JsonOptions);
        var second = await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item2Id), JsonOptions);

        // Assert
        var afterFirst = await first.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        afterFirst!.CurrentItemId.Should().Be(item2Id);
        afterFirst.Status.Should().Be(RunStatus.Running);

        var afterSecond = await second.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        afterSecond!.Status.Should().Be(RunStatus.Completed);
        afterSecond.CurrentItemId.Should().BeNull();
        afterSecond.CompletedAt.Should().NotBeNull();
    }

    [Test]
    public async Task Advance_FromItemMismatch_LeavesRunUnchanged()
    {
        // Arrange
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Act — claim we're leaving item2 while the run is on item1.
        var response = await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item2Id), JsonOptions);

        // Assert
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CurrentItemId.Should().Be(item1Id);
        run.Status.Should().Be(RunStatus.Running);
    }

    [Test]
    public async Task Complete_Returns200Completed()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/complete", null);

        // Assert
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Completed);
        run.CurrentItemId.Should().BeNull();
    }

    [Test]
    public async Task Pause_NoRun_Returns404()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/pause", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Pause_WithTheTimeItWasTapped_StopsTheClockThen()
    {
        // Arrange — two minutes into the first drill; the pause was tapped at 90 seconds, sent
        // with the phone's own offset.
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        var entered = DateTime.UtcNow.AddMinutes(-2);
        await BackdateCurrentItemAsync(eventId, entered);
        var tapped = new DateTimeOffset(entered.AddSeconds(90)).ToOffset(TimeSpan.FromHours(2));

        // Act
        var response = await PostJsonAsync($"/v1/events/{eventId}/plans/run/pause",
            $$"""{ "occurredAt": "{{tapped:yyyy-MM-ddTHH:mm:ss.fffffffzzz}}" }""");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Paused);
        run.CurrentItemPausedElapsedSeconds.Should().Be(90);
    }

    [Test]
    public async Task Pause_WithATapTimeFromBeforeTheRunStarted_CountsFromTheStart()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Act
        var response = await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/pause",
            new RunTapDto(DateTimeOffset.UtcNow.AddMinutes(-5)), JsonOptions);

        // Assert
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CurrentItemPausedElapsedSeconds.Should().Be(0);
    }

    [Test]
    public async Task Advance_WhilePaused_LeavesTheRunRunningOnTheNextItem()
    {
        // Arrange — the 09-28 session: Next tapped while paused left the next drill paused.
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/pause", null);

        // Act
        var response = await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item1Id), JsonOptions);

        // Assert
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(item2Id);
        run.CurrentItemStartedAt.Should().NotBeNull();
    }

    // ---------- Previous, jumps and Resume session ----------

    [Test]
    public async Task GoTo_ThePreviousItem_ResumesItAndForgetsTheItemLeft()
    {
        // Arrange
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item1Id), JsonOptions);
        _factory.RunBroadcaster.ClearReceivedCalls();

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/goto", new GoToRunDto(item2Id, item1Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(item1Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var rows = await db.TrainingPlanRunItems.AsNoTracking().ToListAsync();
        var resumed = rows.Single(r => r.PlanItemId == item1Id);
        resumed.CompletedAtUtc.Should().BeNull();
        resumed.StartedAtUtc.Should().NotBeNull();
        var left = rows.Single(r => r.PlanItemId == item2Id);
        left.StartedAtUtc.Should().BeNull();
        left.CompletedAtUtc.Should().BeNull();
        left.ActualElapsedSeconds.Should().Be(0);
        await _factory.RunBroadcaster.Received(1)
            .BroadcastRunUpdatedAsync(eventId, Arg.Is<RunDto>(d => d.CurrentItemId == item1Id));
    }

    [Test]
    public async Task GoTo_FromAnItemThatIsNoLongerCurrent_LeavesTheRunUnchanged()
    {
        // Arrange
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/goto", new GoToRunDto(item2Id, item1Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CurrentItemId.Should().Be(item1Id);
    }

    [Test]
    public async Task GoTo_AsParticipant_Returns403()
    {
        // Arrange
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        StubParticipant(eventId);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/goto", new GoToRunDto(item1Id, item2Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GoTo_NoRun_Returns404()
    {
        // Arrange
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/goto", new GoToRunDto(item1Id, item2Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GoTo_Unauthenticated_Returns401()
    {
        // Arrange
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/goto", new GoToRunDto(item1Id, item2Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Reopen_AfterEndSession_ResumesTheItemItEndedOn()
    {
        // Arrange — End Session tapped by mistake on the second drill.
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item1Id), JsonOptions);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/complete", null);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/reopen", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Running);
        run.CompletedAt.Should().BeNull();
        run.CurrentItemId.Should().Be(item2Id);
        run.Items.Single(i => i.PlanItemId == item2Id).CompletedAt.Should().BeNull();
        (await GetRunAsync(eventId)).Status.Should().Be(RunStatus.Running);
    }

    [Test]
    public async Task Reopen_ARunStillGoing_LeavesItAlone()
    {
        // Arrange
        var (eventId, item1Id, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/reopen", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(item1Id);
    }

    [Test]
    public async Task Reopen_AsParticipant_Returns403()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/complete", null);
        StubParticipant(eventId);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/reopen", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Reopen_NoRun_Returns404()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/reopen", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Reopen_Unauthenticated_Returns401()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/reopen", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------- Auto-advance ----------

    [Test]
    public async Task StartRun_WithAutoAdvance_StartsTheRunMovingOnByItself()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/start", new StartRunDto(AutoAdvance: true), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions))!.AutoAdvance.Should().BeTrue();
        (await GetRunAsync(eventId)).AutoAdvance.Should().BeTrue();
    }

    [Test]
    public async Task StartRun_WithNoBody_StartsWithoutAutoAdvance()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        (await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions))!.AutoAdvance.Should().BeFalse();
    }

    [Test]
    public async Task StartRun_StartedOverWithNoBody_KeepsAutoAdvance()
    {
        // Arrange — the Start Over of builds that know nothing of auto-advance.
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/start", new StartRunDto(AutoAdvance: true), JsonOptions);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/complete", null);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions))!.AutoAdvance.Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SetAutoAdvance_AsPlanCreator_Returns200AndTellsEveryDevice(bool enabled)
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/start", new StartRunDto(AutoAdvance: !enabled), JsonOptions);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/auto-advance", new RunAutoAdvanceDto(enabled), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.AutoAdvance.Should().Be(enabled);
        run.CanControl.Should().BeTrue();
        (await GetRunAsync(eventId)).AutoAdvance.Should().Be(enabled);
        await _factory.RunBroadcaster.Received(1)
            .BroadcastRunUpdatedAsync(eventId, Arg.Is<RunDto>(d => d.AutoAdvance == enabled));
    }

    [Test]
    public async Task SetAutoAdvance_AsEventAdmin_Returns200()
    {
        // Arrange — a co-host who did not write the plan.
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        StubEventAdmin(eventId, OtherUserId);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/auto-advance", new RunAutoAdvanceDto(true), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions))!.AutoAdvance.Should().BeTrue();
    }

    [Test]
    public async Task SetAutoAdvance_AsParticipant_Returns403AndLeavesItAlone()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        StubParticipant(eventId);
        SetAuth(OtherUserId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/auto-advance", new RunAutoAdvanceDto(true), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        SetAuth(CreatorId);
        (await GetRunAsync(eventId)).AutoAdvance.Should().BeFalse();
    }

    [Test]
    public async Task SetAutoAdvance_NoRun_Returns404()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/auto-advance", new RunAutoAdvanceDto(true), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task SetAutoAdvance_Unauthenticated_Returns401()
    {
        // Arrange
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/auto-advance", new RunAutoAdvanceDto(true), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestCase("{}")]
    [TestCase("""{ "enabled": null }""")]
    [TestCase("")]
    public async Task SetAutoAdvance_NotSayingOnOrOff_Returns400AndLeavesItAlone(string body)
    {
        // Arrange — a body that says nothing is refused rather than read as off.
        var (eventId, _, _) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/start", new StartRunDto(AutoAdvance: true), JsonOptions);

        // Act
        var response = await PostJsonAsync($"/v1/events/{eventId}/plans/run/auto-advance", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await GetRunAsync(eventId)).AutoAdvance.Should().BeTrue();
    }

    [Test]
    public async Task GetRun_AfterTheItemsTimeRanOut_ReadsTheNextItemWithoutWritingIt()
    {
        // Arrange — five minutes and ten seconds into the first item's five.
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/start", new StartRunDto(AutoAdvance: true), JsonOptions);
        var entered = DateTime.UtcNow.AddSeconds(-310);
        await BackdateCurrentItemAsync(eventId, entered);

        // Act
        var run = await GetRunAsync(eventId);

        // Assert
        run.CurrentItemId.Should().Be(item2Id);
        run.CurrentItemStartedAt.Should().BeCloseTo(entered.AddSeconds(300), TimeSpan.FromMilliseconds(1));
        run.Items.Single(i => i.PlanItemId == item1Id).ActualElapsedSeconds.Should().Be(300);
        (await StoredCurrentItemIdAsync(eventId)).Should().Be(item1Id, "a read never writes");
    }

    [Test]
    public async Task Advance_FromTheItemTheRunMovedOnFrom_Returns200WithTheRunAsItIsNow()
    {
        // Arrange — Next tapped on the first item after its time ran out.
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/start", new StartRunDto(AutoAdvance: true), JsonOptions);
        await BackdateCurrentItemAsync(eventId, DateTime.UtcNow.AddSeconds(-310));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item1Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Status.Should().Be(RunStatus.Running);
        run.CurrentItemId.Should().Be(item2Id);
    }

    [Test]
    public async Task SetAutoAdvance_OnWhileTheItemRunsOver_MovesOnNow()
    {
        // Arrange — ten seconds past the first item's five minutes, by hand.
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await BackdateCurrentItemAsync(eventId, DateTime.UtcNow.AddSeconds(-310));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/auto-advance", new RunAutoAdvanceDto(true), JsonOptions);

        // Assert — the second item starts now, not ten seconds in
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CurrentItemId.Should().Be(item2Id);
        run.CurrentItemStartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        run.Items.Single(i => i.PlanItemId == item1Id).ActualElapsedSeconds.Should().BeGreaterThanOrEqualTo(310);
    }

    [Test]
    public async Task GoTo_BackToAnItemThatRanItsFullTime_PlaysItsFullTimeAgain()
    {
        // Arrange — the first item's five minutes ran out and the run moved on by itself.
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/start", new StartRunDto(AutoAdvance: true), JsonOptions);
        await BackdateCurrentItemAsync(eventId, DateTime.UtcNow.AddSeconds(-310));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/goto", new GoToRunDto(item2Id, item1Id), JsonOptions);

        // Assert — back on it from the start, rather than bounced straight forward again
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.CurrentItemId.Should().Be(item1Id);
        run.CurrentItemStartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        run.Items.Single(i => i.PlanItemId == item1Id).ActualElapsedSeconds.Should().Be(0);
    }

    [Test]
    public async Task Advance_FromTheItemTheRunMovedOnTo_SavesTheMoveWithTheTap()
    {
        // Arrange — the phone flipped to the second item at 0:00, and its Next ends the session.
        var (eventId, item1Id, item2Id) = await SeedPlanWithTwoItemsAsync();
        SetAuth(CreatorId);
        await _client.PostAsJsonAsync($"/v1/events/{eventId}/plans/run/start", new StartRunDto(AutoAdvance: true), JsonOptions);
        await BackdateCurrentItemAsync(eventId, DateTime.UtcNow.AddSeconds(-310));

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/events/{eventId}/plans/run/advance", new AdvanceRunDto(item2Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await GetRunAsync(eventId);
        run.Status.Should().Be(RunStatus.Completed);
        run.Items.Single(i => i.PlanItemId == item1Id).ActualElapsedSeconds.Should().Be(300);
        run.Items.Single(i => i.PlanItemId == item2Id).CompletedAt.Should().NotBeNull();
    }

    // ---------- Realtime ----------

    [Test]
    public void TheRunHub_IsBuiltFromTheContainerAsSignalRBuildsIt()
    {
        // Arrange — the unit tests build it by hand, so a dependency the container lacks would
        // only show as every JoinRun failing.
        using var scope = _factory.Services.CreateScope();

        // Act
        var act = () => ActivatorUtilities.CreateInstance<TrainingRunHub>(scope.ServiceProvider);

        // Assert
        act.Should().NotThrow();
    }

    // ---------- Stations ----------

    [Test]
    public async Task StartRun_WithAStationsBlock_ReturnsTheGroupsWithTheRun()
    {
        // Arrange
        var (eventId, _, stationsItemId) = await SeedPlanWithStationsAsync();
        SetAuth(CreatorId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        var block = run!.Items.Single(i => i.PlanItemId == stationsItemId);
        block.Kind.Should().Be(ItemKind.Stations);
        block.Stations.Select(s => s.Name).Should().ContainInOrder("Setters", "Hitters");
        block.Stations.Single(s => s.Name == "Hitters").Items.Should().HaveCount(2);
        block.Stations.SelectMany(s => s.Items).Should().Contain(r => r.Kind == ItemKind.Break && r.DrillId == null);
    }

    [Test]
    public async Task StartRun_Restarted_ReSnapshotsGroupsAndLeavesNoOrphanRows()
    {
        // Arrange — a finished run, then the coach reworks the block and starts again. The
        // reconcile reuses the run item (its timings are the run's) but its groups are pure
        // snapshot, so they are taken again. EF infers Added-vs-Modified from whether the key is
        // set and BaseEntity sets it in its constructor, so this is exactly the shape that
        // saves a never-inserted row as an UPDATE — it has to be exercised against a real
        // database, not a substitute.
        var (eventId, _, stationsItemId) = await SeedPlanWithStationsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/complete", null);
        await RenameTheOnlyRemainingGroupAsync(stationsItemId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        run!.Items.Single(i => i.PlanItemId == stationsItemId)
            .Stations.Select(s => s.Name).Should().Equal("Passers");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.RunStations.Should().HaveCount(1);
        db.RunStationItems.Should().HaveCount(1);
    }

    [Test]
    public async Task StartRun_Restarted_AfterARowWasAddedToThePlan_SnapshotsTheNewRow()
    {
        // Arrange — the other half of the reconcile: a run item that did not exist last time is
        // added to a run the context is already tracking.
        var (eventId, planId, _) = await SeedPlanWithStationsAsync();
        SetAuth(CreatorId);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);
        await _client.PostAsync($"/v1/events/{eventId}/plans/run/complete", null);
        var addedItemId = await AppendABreakToThePlanAsync(planId);

        // Act
        var response = await _client.PostAsync($"/v1/events/{eventId}/plans/run/start", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions);
        var added = run!.Items.Single(i => i.PlanItemId == addedItemId);
        added.Kind.Should().Be(ItemKind.Break);
        added.Title.Should().Be("Cool down");
    }

    // ---------- Helpers ----------

    /// <summary>Drops the second group and renames the first, as a coach reworking the block would.</summary>
    private async Task RenameTheOnlyRemainingGroupAsync(Guid stationsItemId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var stations = await db.PlanStations
            .Where(s => s.PlanItemId == stationsItemId)
            .OrderBy(s => s.Order)
            .ToListAsync();

        db.PlanStations.Remove(stations.Last());
        stations.First().Name = "Passers";
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AppendABreakToThePlanAsync(Guid planId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var item = new PlanItem
        {
            TemplateId = planId,
            Kind = ItemKind.Break,
            Title = "Cool down",
            Order = 3,
            Duration = 5
        };
        db.PlanItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    /// <summary>
    /// A water break, then a Stations block split into two groups — the second of which takes
    /// its own water while the first keeps playing.
    /// </summary>
    private async Task<(Guid eventId, Guid planId, Guid stationsItemId)> SeedPlanWithStationsAsync()
    {
        var eventId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        db.Set<UserProfile>().Add(new UserProfile
        {
            Id = CreatorId,
            Name = "Coach",
            Surname = "Creator",
            Email = "coach.creator@test.com"
        });

        var setters = new Drill { Id = Guid.NewGuid(), Name = "Hands", CreatedByUserId = CreatorId };
        var hitters = new Drill { Id = Guid.NewGuid(), Name = "Approach", CreatedByUserId = CreatorId };
        db.Drills.AddRange(setters, hitters);

        var planId = Guid.NewGuid();
        var stationsItemId = Guid.NewGuid();
        db.TrainingPlans.Add(new TrainingPlan
        {
            Id = planId,
            Name = "Instance Plan",
            CreatedByUserId = CreatorId,
            PlanType = PlanType.Instance,
            EventId = eventId,
            Visibility = TemplateVisibility.Private,
            Items =
            [
                new PlanItem
                {
                    TemplateId = planId, Kind = ItemKind.Break, Title = "Water", Order = 1, Duration = 5
                },
                new PlanItem
                {
                    Id = stationsItemId,
                    TemplateId = planId,
                    Kind = ItemKind.Stations,
                    Title = "Stations",
                    Order = 2,
                    Duration = 20,
                    PlannedDuration = 20,
                    Stations =
                    [
                        new PlanStation
                        {
                            Name = "Setters",
                            Order = 0,
                            Items = [new PlanStationItem { Kind = ItemKind.Drill, DrillId = setters.Id, Order = 0, Duration = 20 }]
                        },
                        new PlanStation
                        {
                            Name = "Hitters",
                            Order = 1,
                            Items =
                            [
                                new PlanStationItem { Kind = ItemKind.Drill, DrillId = hitters.Id, Order = 0, Duration = 12 },
                                new PlanStationItem { Kind = ItemKind.Break, Title = "Water", Order = 1, Duration = 8 }
                            ]
                        }
                    ]
                }
            ]
        });

        await db.SaveChangesAsync();
        return (eventId, planId, stationsItemId);
    }

    private void StubParticipant(Guid eventId) =>
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, Arg.Any<Guid>()).Returns((true, true));

    private void StubEventAdmin(Guid eventId, Guid userId)
    {
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, userId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventAdminAsync(eventId, userId).Returns(true);
    }

    /// <summary>
    /// Makes the current item's clock — and the run's last change — read as started at
    /// <paramref name="entered"/>. Written past SaveChanges, which stamps UpdatedAt itself.
    /// </summary>
    private async Task BackdateCurrentItemAsync(Guid eventId, DateTime entered)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        await db.TrainingPlanRuns
            .Where(r => r.EventId == eventId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.CurrentItemStartedAtUtc, entered)
                .SetProperty(r => r.UpdatedAt, entered));
    }

    /// <summary>The current item as the database holds it, whatever a read shows.</summary>
    private async Task<Guid?> StoredCurrentItemIdAsync(Guid eventId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        return await db.TrainingPlanRuns
            .Where(r => r.EventId == eventId)
            .Select(r => r.CurrentItemId)
            .SingleAsync();
    }

    private Task<HttpResponseMessage> PostJsonAsync(string url, string json) =>
        _client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>The run as the current caller reads it.</summary>
    private async Task<RunDto> GetRunAsync(Guid eventId)
    {
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans/run");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<RunDto>(JsonOptions))!;
    }

    private async Task<(Guid eventId, Guid item1Id, Guid item2Id)> SeedPlanWithTwoItemsAsync()
    {
        var eventId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        db.Set<UserProfile>().Add(new UserProfile
        {
            Id = CreatorId,
            Name = "Coach",
            Surname = "Creator",
            Email = "coach.creator@test.com"
        });

        var drill1 = new Drill { Id = Guid.NewGuid(), Name = "Drill 1", CreatedByUserId = CreatorId };
        var drill2 = new Drill { Id = Guid.NewGuid(), Name = "Drill 2", CreatedByUserId = CreatorId };
        db.Drills.AddRange(drill1, drill2);

        var planId = Guid.NewGuid();
        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();
        db.TrainingPlans.Add(new TrainingPlan
        {
            Id = planId,
            Name = "Instance Plan",
            CreatedByUserId = CreatorId,
            PlanType = PlanType.Instance,
            EventId = eventId,
            Visibility = TemplateVisibility.Private,
            Items = new List<PlanItem>
            {
                new() { Id = item1Id, TemplateId = planId, DrillId = drill1.Id, Order = 1, Duration = 5 },
                new() { Id = item2Id, TemplateId = planId, DrillId = drill2.Id, Order = 2, Duration = 10 }
            }
        });

        await db.SaveChangesAsync();
        return (eventId, item1Id, item2Id);
    }

    private void SetAuth(Guid userId)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(userId));
    }

    private static string GenerateJwt(Guid userId, string email = "test@example.com")
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
            new Claim(ClaimTypes.Email, email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(
            issuer: CoachingApiFactory.JwtIssuer,
            audience: CoachingApiFactory.JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
