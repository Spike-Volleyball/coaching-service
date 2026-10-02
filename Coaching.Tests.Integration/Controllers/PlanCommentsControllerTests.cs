using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
/// when he commented on it. Whoever may read an event's plan may discuss it.
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
    public async Task Comment_ByClubStaffWhoAreNotOnTheEvent_IsKeptAndListed()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        ResponsibleForTheEvent(eventId, StaffId);
        SetAuth(StaffId);

        // Act
        var posted = await PostCommentAsync(planId, "Move the serving block before the water break");
        var listed = await _client.GetAsync($"/v1/plans/{planId}/comments");

        // Assert
        posted.StatusCode.Should().Be(HttpStatusCode.Created);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CommentsAsync(listed)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new { UserId = StaffId, Content = "Move the serving block before the water break" },
                options => options.ExcludingMissingMembers());
    }

    [Test]
    public async Task Comment_ByAParticipant_IsKeptAndListed()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        OnTheEvent(eventId, PlayerId);
        SetAuth(PlayerId);

        // Act
        var posted = await PostCommentAsync(planId, "Can we finish with a game?");
        var listed = await _client.GetAsync($"/v1/plans/{planId}/comments");

        // Assert
        posted.StatusCode.Should().Be(HttpStatusCode.Created);
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
        var posted = await PostCommentAsync(planId, "Nice plan");
        var listed = await _client.GetAsync($"/v1/plans/{planId}/comments");

        // Assert
        posted.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
        var posted = await PostCommentAsync(Guid.NewGuid(), "Nice plan");
        var listed = await _client.GetAsync($"/v1/plans/{Guid.NewGuid()}/comments");

        // Assert
        posted.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
    public async Task Comment_WithNoText_IsBadRequest()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        ResponsibleForTheEvent(eventId, StaffId);
        SetAuth(StaffId);

        // Act
        var posted = await PostCommentAsync(planId, "   ");

        // Assert
        posted.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task DeleteComment_OfAPlayers_ByClubStaffWhoAreNotOnTheEvent_RemovesIt()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        OnTheEvent(eventId, PlayerId);
        ResponsibleForTheEvent(eventId, StaffId);
        var commentId = await CommentAsAsync(PlayerId, planId, "Can we finish with a game?");
        SetAuth(StaffId);

        // Act
        var deleted = await _client.DeleteAsync($"/v1/plans/{planId}/comments/{commentId}");

        // Assert
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CommentsAsync(await _client.GetAsync($"/v1/plans/{planId}/comments"))).Should().BeEmpty();
    }

    [Test]
    public async Task DeleteComment_OfSomebodyElses_ByAParticipant_IsRefused()
    {
        // Arrange
        var (eventId, planId) = await SeedEventPlanAsync();
        OnTheEvent(eventId, PlayerId);
        ResponsibleForTheEvent(eventId, StaffId);
        var commentId = await CommentAsAsync(StaffId, planId, "Bring the ball cart");
        SetAuth(PlayerId);

        // Act
        var deleted = await _client.DeleteAsync($"/v1/plans/{planId}/comments/{commentId}");

        // Assert
        deleted.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
        _client.PostAsJsonAsync($"/v1/plans/{planId}/comments", new CreatePlanCommentDto(content));

    private async Task<Guid> CommentAsAsync(Guid userId, Guid planId, string content)
    {
        SetAuth(userId);
        var posted = await PostCommentAsync(planId, content);
        posted.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await posted.Content.ReadFromJsonAsync<PlanCommentDto>(JsonOptions))!.Id;
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
