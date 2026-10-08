using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coaching.Application.DTOs.Templates;
using Coaching.Domain.Enums;
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
/// SPI-6800: a club's owner read and ran the plan of a session he was not on, and was refused
/// when he commented on it. Whoever may read an event's plan may read its comments; writing them
/// moved to social, and these routes answer 410 Gone.
/// </summary>
[TestFixture]
[Category("Integration")]
public class PlanCommentsControllerTests
{
    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid StaffId = Guid.NewGuid();
    private static readonly Guid PlayerId = Guid.NewGuid();
    private static readonly Guid StrangerId = Guid.NewGuid();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
    }

    [Test]
    public async Task Comments_ForClubStaffWhoAreNotOnTheEvent_AreListed()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        ResponsibleForTheEvent(eventId, StaffId);
        await SeedCommentAsync(planId, PlayerId, "Move the serving block before the water break");
        SetAuth(StaffId);

        // Act
        var listed = await _client.GetAsync($"/v1/plans/{planId}/comments");

        // Assert
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CommentsAsync(listed)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new { UserId = PlayerId, Content = "Move the serving block before the water break" },
                options => options.ExcludingMissingMembers());
    }

    [Test]
    public async Task Comments_ForAParticipant_AreListed_WithoutTheOnesRemoved()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        OnTheEvent(eventId, PlayerId);
        await SeedCommentAsync(planId, PlayerId, "Can we finish with a game?");
        await SeedCommentAsync(planId, StaffId, "Removed", isDeleted: true);
        SetAuth(PlayerId);

        // Act
        var listed = await _client.GetAsync($"/v1/plans/{planId}/comments");

        // Assert
        (await CommentsAsync(listed)).Should().ContainSingle().Which.UserId.Should().Be(PlayerId);
    }

    [Test]
    public async Task Comments_ForSomeoneWithNoStandingOnTheEvent_AreRefused()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        NoStandingOnTheEvent(eventId, StrangerId);
        SetAuth(StrangerId);

        // Act
        var listed = await _client.GetAsync($"/v1/plans/{planId}/comments");

        // Assert
        listed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Comments_OfAPlanWhoseEventIsGone_AreNotFound()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, PlayerId).Returns((false, false));
        SetAuth(PlayerId);

        // Act
        var listed = await _client.GetAsync($"/v1/plans/{planId}/comments");

        // Assert
        listed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Comments_OfAPlanThatDoesNotExist_AreNotFound()
    {
        // Arrange
        SetAuth(PlayerId);

        // Act
        var listed = await _client.GetAsync($"/v1/plans/{Guid.NewGuid()}/comments");

        // Assert
        listed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Comments_SignedOut_AreUnauthorized()
    {
        // Arrange
        var (_, planId) = await SeedEventPlanAsync();

        // Act
        var posted = await PostCommentAsync(planId, "Nice plan");
        var listed = await _client.GetAsync($"/v1/plans/{planId}/comments");
        var deleted = await _client.DeleteAsync($"/v1/plans/{planId}/comments/{Guid.NewGuid()}");

        // Assert
        posted.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        listed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        deleted.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task WritingComments_IsGone_BecauseSocialKeepsThemNow()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        ResponsibleForTheEvent(eventId, StaffId);
        var commentId = await SeedCommentAsync(planId, PlayerId, "Can we finish with a game?");
        SetAuth(StaffId);

        // Act
        var posted = await PostCommentAsync(planId, "Move the serving block");
        var deleted = await _client.DeleteAsync($"/v1/plans/{planId}/comments/{commentId}");

        // Assert
        foreach (var response in new[] { posted, deleted })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Gone);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            body["code"]!.GetValue<string>().Should().Be("COMMENTS_MOVED");
            body["status"]!.GetValue<int>().Should().Be(410);
        }

        (await CommentsAsync(await _client.GetAsync($"/v1/plans/{planId}/comments"))).Should().ContainSingle();
    }

    private void OnTheEvent(Guid eventId, Guid userId) =>
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, userId).Returns((true, true));

    /// <summary>Club staff, a host: answerable for the event without being on its roster.</summary>
    private void ResponsibleForTheEvent(Guid eventId, Guid userId)
    {
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, userId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventAdminAsync(eventId, userId).Returns(true);
    }

    private void NoStandingOnTheEvent(Guid eventId, Guid userId)
    {
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, userId).Returns((false, true));
        _factory.EventsGrpcClient.IsEventAdminAsync(eventId, userId).Returns(false);
    }

    private Task<HttpResponseMessage> PostCommentAsync(Guid planId, string content) =>
        _client.PostAsJsonAsync($"/v1/plans/{planId}/comments", new { content });

    private async Task<Guid> SeedCommentAsync(Guid planId, Guid userId, string content, bool isDeleted = false)
    {
        var comment = new PlanComment { TemplateId = planId, UserId = userId, Content = content, IsDeleted = isDeleted };

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.Add(comment);
        await db.SaveChangesAsync();
        return comment.Id;
    }

    private static async Task<List<PlanCommentDto>> CommentsAsync(HttpResponseMessage listed) =>
        (await listed.Content.ReadFromJsonAsync<PlanCommentsResponseDto>(JsonOptions))!.Items;

    /// <summary>The plan of an event, and a mirrored profile for everyone who may write on it.</summary>
    private async Task<(Guid EventId, Guid PlanId)> SeedEventPlanAsync()
    {
        var eventId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(
            Profile(CoachId, "Coach"),
            Profile(StaffId, "Owner"),
            Profile(PlayerId, "Player"),
            Profile(StrangerId, "Stranger"),
            new TrainingPlan
            {
                Id = planId,
                Name = "Thursday practice",
                CreatedByUserId = CoachId,
                PlanType = PlanType.Instance,
                EventId = eventId,
                Visibility = TemplateVisibility.Private
            });
        await db.SaveChangesAsync();

        return (eventId, planId);
    }

    private static UserProfile Profile(Guid id, string surname) => new()
    {
        Id = id,
        Name = "Test",
        Surname = surname,
        Email = $"{surname.ToLowerInvariant()}@test.local",
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
                new Claim(ClaimTypes.Email, "commenter@test.local"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
