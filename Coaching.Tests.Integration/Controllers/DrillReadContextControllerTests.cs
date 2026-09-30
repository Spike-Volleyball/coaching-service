using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coaching.Application.DTOs.Drills;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Feedback;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shared.Models;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// SPI-6690: on 2026-09-28 the co-coaches' phones got 404 ninety times for the head coach's
/// private drills while running the event's plan. A drill opens where the reader met it — the
/// plan of an event they may read, feedback they may read — and nowhere else.
/// </summary>
[TestFixture]
[Category("Integration")]
public class DrillReadContextControllerTests
{
    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid ReaderId = Guid.NewGuid();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

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
    public async Task OneTimeTearDown()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>Every drill here is the coach's, and a drill's author is a mirrored profile row.</summary>
    [SetUp]
    public Task SetUp() => SeedAsync([CoachProfile()]);

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
    }

    [Test]
    public async Task GetById_ThroughTheEventWhosePlanHoldsIt_OpensAPrivateDrillForAParticipant()
    {
        // Arrange
        var (eventId, drillId, _) = await SeedEventPlanAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, ReaderId).Returns((true, true));
        SetAuth(ReaderId);

        // Act
        var alone = await _client.GetAsync($"/v1/drills/{drillId}");
        var throughTheEvent = await _client.GetAsync($"/v1/drills/{drillId}?eventId={eventId}");

        // Assert
        alone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        throughTheEvent.StatusCode.Should().Be(HttpStatusCode.OK);
        (await throughTheEvent.Content.ReadFromJsonAsync<DrillDto>(JsonOptions))!.Id.Should().Be(drillId);
    }

    [Test]
    public async Task GetById_ThroughTheEvent_OpensAStationsDrillForAnEventAdminOffTheRoster()
    {
        // Arrange — a co-host running the session is an event admin, not a participant.
        var (eventId, _, stationDrillId) = await SeedEventPlanAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, ReaderId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventAdminAsync(eventId, ReaderId).Returns(true);
        SetAuth(ReaderId);

        // Act
        var response = await _client.GetAsync($"/v1/drills/{stationDrillId}?eventId={eventId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task GetById_ThroughAnEventTheReaderIsNotOn_IsAnsweredAsIfTheDrillWereNeverThere()
    {
        // Arrange
        var (eventId, drillId, _) = await SeedEventPlanAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, ReaderId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventAdminAsync(eventId, ReaderId).Returns(false);
        SetAuth(ReaderId);

        // Act / Assert
        await AssertRefusedLikeAMissingDrillAsync(drillId, $"eventId={eventId}");
    }

    [Test]
    public async Task GetById_ThroughAnEventWhosePlanDoesNotHoldIt_IsAnsweredAsIfTheDrillWereNeverThere()
    {
        // Arrange — being at one session opens that session's drills, not the rest of a library.
        var (eventId, _, _) = await SeedEventPlanAsync();
        var elsewhere = await SeedDrillAsync("Not in the plan");
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, ReaderId).Returns((true, true));
        SetAuth(ReaderId);

        // Act / Assert
        await AssertRefusedLikeAMissingDrillAsync(elsewhere, $"eventId={eventId}");
    }

    [Test]
    public async Task GetById_ThroughTheFeedbackItIsAttachedTo_OpensForThePlayerItWasSharedWith()
    {
        // Arrange
        var (feedbackId, drillId) = await SeedFeedbackAsync(shared: true);
        SetAuth(ReaderId);

        // Act
        var alone = await _client.GetAsync($"/v1/drills/{drillId}");
        var throughTheFeedback = await _client.GetAsync($"/v1/drills/{drillId}?feedbackId={feedbackId}");

        // Assert
        alone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        throughTheFeedback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await throughTheFeedback.Content.ReadFromJsonAsync<DrillDto>(JsonOptions))!.Id.Should().Be(drillId);
    }

    [Test]
    public async Task GetById_ThroughFeedbackNotYetShared_IsAnsweredAsIfTheDrillWereNeverThere()
    {
        // Arrange
        var (feedbackId, drillId) = await SeedFeedbackAsync(shared: false);
        SetAuth(ReaderId);

        // Act / Assert
        await AssertRefusedLikeAMissingDrillAsync(drillId, $"feedbackId={feedbackId}");
    }

    [Test]
    public async Task GetById_ThroughFeedbackTheDrillWasTakenOff_IsAnsweredAsIfTheDrillWereNeverThere()
    {
        // Arrange — detaching a drill only marks its link deleted.
        var (feedbackId, drillId) = await SeedFeedbackAsync(shared: true, linkDeleted: true);
        SetAuth(ReaderId);

        // Act / Assert
        await AssertRefusedLikeAMissingDrillAsync(drillId, $"feedbackId={feedbackId}");
    }

    [Test]
    public async Task GetById_SignedOut_WithAContext_IsUnauthorized()
    {
        // Arrange
        var (feedbackId, drillId) = await SeedFeedbackAsync(shared: true);

        // Act
        var response = await _client.GetAsync($"/v1/drills/{drillId}?feedbackId={feedbackId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task GetById_WithAContextThatIsNotAnId_IsBadRequest()
    {
        // Arrange
        var drillId = await SeedDrillAsync("Private drill");
        SetAuth(ReaderId);

        // Act
        var response = await _client.GetAsync($"/v1/drills/{drillId}?feedbackId=not-an-id");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A 404 whose body is the missing drill's, so the refusal says nothing either.</summary>
    private async Task AssertRefusedLikeAMissingDrillAsync(Guid drillId, string query)
    {
        var missingId = Guid.NewGuid();

        var refused = await _client.GetAsync($"/v1/drills/{drillId}?{query}");
        var neverThere = await _client.GetAsync($"/v1/drills/{missingId}?{query}");

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync()).Replace(drillId.ToString(), "{drill}")
            .Should().Be((await neverThere.Content.ReadAsStringAsync()).Replace(missingId.ToString(), "{drill}"));
    }

    /// <summary>An event plan holding one private drill as a row and another inside a station.</summary>
    private async Task<(Guid EventId, Guid DrillId, Guid StationDrillId)> SeedEventPlanAsync()
    {
        var eventId = Guid.NewGuid();
        var drill = PrivateDrill("Dot Shots");
        var stationDrill = PrivateDrill("Hands");
        var planId = Guid.NewGuid();
        var plan = new TrainingPlan
        {
            Id = planId,
            Name = "Practice 9/28",
            CreatedByUserId = CoachId,
            PlanType = PlanType.Instance,
            EventId = eventId,
            Items =
            [
                new PlanItem { TemplateId = planId, Kind = ItemKind.Drill, DrillId = drill.Id, Order = 1, Duration = 10 },
                new PlanItem
                {
                    TemplateId = planId,
                    Kind = ItemKind.Stations,
                    Title = "Stations",
                    Order = 2,
                    Duration = 20,
                    Stations =
                    [
                        new PlanStation
                        {
                            Name = "Setters",
                            Order = 0,
                            Items = [new PlanStationItem { Kind = ItemKind.Drill, DrillId = stationDrill.Id, Order = 0, Duration = 20 }]
                        }
                    ]
                }
            ]
        };

        await SeedAsync([drill, stationDrill, plan]);
        return (eventId, drill.Id, stationDrill.Id);
    }

    private async Task<(Guid FeedbackId, Guid DrillId)> SeedFeedbackAsync(bool shared, bool linkDeleted = false)
    {
        var drill = PrivateDrill("Platform angles");
        var feedback = new Feedback { CoachUserId = CoachId, RecipientUserId = ReaderId, SharedWithPlayer = shared };
        var point = new ImprovementPoint { FeedbackId = feedback.Id, Description = "Platform", Order = 0 };
        var link = new ImprovementPointDrill { ImprovementPointId = point.Id, DrillId = drill.Id, IsDeleted = linkDeleted };

        await SeedAsync([drill, feedback, point, link]);
        return (feedback.Id, drill.Id);
    }

    private async Task<Guid> SeedDrillAsync(string name)
    {
        var drill = PrivateDrill(name);
        await SeedAsync([drill]);
        return drill.Id;
    }

    private async Task SeedAsync(IEnumerable<object> entities)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private static Drill PrivateDrill(string name) => new()
    {
        Name = name,
        CreatedByUserId = CoachId,
        Visibility = DrillVisibility.Private,
        Skills = [],
        Instructions = [],
        CoachingPoints = []
    };

    private static UserProfile CoachProfile() => new()
    {
        Id = CoachId,
        Name = "CJ",
        Surname = "Coach",
        Email = "cj.coach@test.local",
        IsActive = true
    };

    private void SetAuth(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var token = new JwtSecurityToken(
            issuer: CoachingApiFactory.JwtIssuer,
            audience: CoachingApiFactory.JwtAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
                new Claim(ClaimTypes.Email, "reader@test.local"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
