using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Models.Feedback;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// The feedback lists used to carry only an event id per row, so the web and mobile cards each
/// fetched their event — one request per card. The rows now name the session themselves, from
/// one summary call per page, and the wire shape both clients read is pinned here.
/// </summary>
[TestFixture]
[Category("Integration")]
public class FeedbackEventSummaryControllerTests
{
    private static readonly DateTime FridayEvening = new(2026, 9, 11, 18, 30, 0, DateTimeKind.Utc);

    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _factory.DisposeAsync();

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _factory.EventsGrpcClient.ClearSubstitute();
        await _factory.DatabaseResetter.ResetAsync();
    }

    [Test]
    public async Task GetReceivedFeedback_TwentyRowsOnThreeEvents_NamesEachSessionFromOneSummaryCall()
    {
        // Arrange
        var recipientId = Guid.NewGuid();
        var events = Enumerable.Range(0, 3).Select(i => (Id: Guid.NewGuid(), Name: $"Session {i}")).ToList();
        await SeedFeedbackAsync(recipientId, Enumerable.Range(0, 20).Select(i => events[i % 3].Id));
        _factory.EventsGrpcClient.GetEventInfoAsync(Arg.Any<IReadOnlyCollection<Guid>>())
            .Returns(events.ToDictionary(e => e.Id, e => new EventInfo(e.Id, e.Name, FridayEvening, "TrainingSession")));
        SetAuth(recipientId);

        // Act
        var response = await _client.GetAsync("/v1/me/feedback/received?pageSize=20");

        // Assert
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(20);
        foreach (var item in items)
        {
            var eventId = item.GetProperty("eventId").GetGuid();
            var summary = item.GetProperty("event");
            summary.GetProperty("id").GetGuid().Should().Be(eventId);
            summary.GetProperty("name").GetString().Should().Be(events.Single(e => e.Id == eventId).Name);
            summary.GetProperty("type").GetString().Should().Be("TrainingSession");
            summary.GetProperty("startTime").GetDateTime().ToUniversalTime().Should().Be(FridayEvening);
        }
        await _factory.EventsGrpcClient.Received(1).GetEventInfoAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 3 && events.All(e => ids.Contains(e.Id))));
    }

    [Test]
    public async Task GetReceivedFeedback_EventUnknownToEventsService_KeepsEventIdAndSendsNoSummary()
    {
        // Arrange
        var recipientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await SeedFeedbackAsync(recipientId, [eventId]);
        _factory.EventsGrpcClient.GetEventInfoAsync(Arg.Any<IReadOnlyCollection<Guid>>())
            .Returns(new Dictionary<Guid, EventInfo>());
        SetAuth(recipientId);

        // Act
        var response = await _client.GetAsync("/v1/me/feedback/received");

        // Assert
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = body.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("eventId").GetGuid().Should().Be(eventId);
        item.GetProperty("event").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Test]
    public async Task GetById_EventLinked_CarriesTheSummary()
    {
        // Arrange
        var recipientId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var feedbackId = (await SeedFeedbackAsync(recipientId, [eventId])).Single();
        _factory.EventsGrpcClient.GetEventInfoAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == eventId))
            .Returns(new Dictionary<Guid, EventInfo> { [eventId] = new(eventId, "Friday session", FridayEvening, "Match") });
        SetAuth(recipientId);

        // Act
        var response = await _client.GetAsync($"/v1/feedback/{feedbackId}");

        // Assert
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var summary = body.RootElement.GetProperty("event");
        summary.GetProperty("id").GetGuid().Should().Be(eventId);
        summary.GetProperty("name").GetString().Should().Be("Friday session");
        summary.GetProperty("type").GetString().Should().Be("Match");
    }

    private async Task<List<Guid>> SeedFeedbackAsync(Guid recipientId, IEnumerable<Guid> eventIds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var coachId = Guid.NewGuid();
        var rows = eventIds.Select(eventId => new Feedback
        {
            RecipientUserId = recipientId,
            CoachUserId = coachId,
            EventId = eventId,
            SharedWithPlayer = true,
            Content = "<p>Keep the platform still</p>",
        }).ToList();
        db.Set<Feedback>().AddRange(rows);
        await db.SaveChangesAsync();
        return rows.Select(r => r.Id).ToList();
    }

    private void SetAuth(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
            new Claim(ClaimTypes.Email, "player@test.com"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(
            issuer: CoachingApiFactory.JwtIssuer,
            audience: CoachingApiFactory.JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
