using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shared.Messaging.Contracts.Events.Coaching;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// Every piece of feedback a coach shares reaches the outbox as a praise snapshot, with its badge
/// if it has one; a badge put on, changed or taken off sends a new copy, and unsharing or deleting
/// sends it withdrawn.
/// </summary>
[TestFixture]
[Category("Integration")]
public class PraiseFactsControllerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private CoachingApiFactory _factory = null!;
    private WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;

    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _playerId = Guid.NewGuid();
    private readonly Guid _clubId = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory(isolateMessageBroker: true);
        await _factory.InitializeAsync();

        // S3 alone is substituted, as in the other feedback fixtures: the real signer is built
        // for every feedback read, and a real S3 client would look for credentials.
        _host = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton(Substitute.For<IAmazonS3>())));
        _client = _host.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [SetUp]
    public void SetUp()
    {
        _factory.ClubsGrpcClient.CanGiveFeedbackInClubAsync(_coachId, _clubId).Returns(true);
        _factory.ClubsGrpcClient.GetClubMemberIdsAsync(_clubId).Returns(new HashSet<Guid> { _playerId });
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
    }

    [Test]
    public async Task Create_SharedWithABadge_PutsItGivenInTheOutbox()
    {
        // Arrange
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/feedback", new
        {
            recipientUserId = _playerId,
            clubId = _clubId,
            content = "<p>Great session</p>",
            sharedWithPlayer = true,
            praise = new { message = "Chased every ball", badgeType = "Hustle" },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var feedbackId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var fact = (await OutboxAsync()).Should().ContainSingle().Subject;
        fact.Should().BeEquivalentTo(new
        {
            FeedbackId = feedbackId,
            CoachUserId = _coachId,
            PlayerUserId = _playerId,
            State = PraiseState.Given,
            Badge = "Hustle",
        });
        // The snapshot is taken from the feedback as written, and the database keeps microseconds.
        fact.GivenAt.Should().BeCloseTo((await StoredFeedbackAsync(feedbackId)).CreatedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Test]
    public async Task Create_Private_PutsNothingInTheOutbox()
    {
        // Arrange
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/feedback", new
        {
            recipientUserId = _playerId,
            clubId = _clubId,
            content = "<p>Notes for me</p>",
            sharedWithPlayer = false,
            praise = new { message = "Chased every ball", badgeType = "Hustle" },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await OutboxAsync()).Should().BeEmpty();
    }

    [Test]
    public async Task Share_PrivateFeedbackWithoutPraise_PutsItGivenWithNoBadge()
    {
        // Arrange
        var feedbackId = await SeedFeedbackAsync(shared: false);
        SetAuth(_coachId);

        // Act
        var response = await _client.PutAsync($"/v1/feedback/{feedbackId}/share?share=true", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fact = (await OutboxAsync()).Should().ContainSingle().Subject;
        fact.FeedbackId.Should().Be(feedbackId);
        fact.State.Should().Be(PraiseState.Given);
        fact.Badge.Should().BeNull();
    }

    [Test]
    public async Task ChangeBadge_OnSharedFeedback_PutsTheNewBadgeInTheOutbox()
    {
        // Arrange
        var feedbackId = await SeedFeedbackAsync(shared: true, BadgeType.Star);
        SetAuth(_coachId);

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/feedback/{feedbackId}/praise", new { badgeType = "Clutch" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fact = (await OutboxAsync()).Should().ContainSingle().Subject;
        fact.State.Should().Be(PraiseState.Given);
        fact.Badge.Should().Be("Clutch");
    }

    [Test]
    public async Task RemovePraise_ThenGiveItAgain_PutsTheBadgeOffAndBackOn()
    {
        // Arrange
        var feedbackId = await SeedFeedbackAsync(shared: true, BadgeType.Star);
        SetAuth(_coachId);

        // Act
        var removed = await _client.DeleteAsync($"/v1/feedback/{feedbackId}/praise");
        var readAfterRemoval = await _client.GetFromJsonAsync<JsonElement>($"/v1/feedback/{feedbackId}");
        var givenAgain = await _client.PostAsJsonAsync(
            $"/v1/feedback/{feedbackId}/praise", new { message = "Went for the big serve", badgeType = "BraveCall" });

        // Assert
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        readAfterRemoval.GetProperty("praise").ValueKind.Should().Be(JsonValueKind.Null);
        givenAgain.StatusCode.Should().Be(HttpStatusCode.OK, await givenAgain.Content.ReadAsStringAsync());
        (await OutboxAsync()).Select(f => (f.State, f.Badge)).Should().Equal(
            (PraiseState.Given, null),
            (PraiseState.Given, "BraveCall"));
    }

    [Test]
    public async Task Unshare_PutsItWithdrawnInTheOutbox()
    {
        // Arrange
        var feedbackId = await SeedFeedbackAsync(shared: true, BadgeType.Teamwork);
        SetAuth(_coachId);

        // Act
        var response = await _client.PutAsync($"/v1/feedback/{feedbackId}/share?share=false", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var fact = (await OutboxAsync()).Should().ContainSingle().Subject;
        fact.FeedbackId.Should().Be(feedbackId);
        fact.State.Should().Be(PraiseState.Withdrawn);
        fact.Badge.Should().BeNull();
    }

    [Test]
    public async Task UnshareByEdit_PutsItWithdrawnInTheOutbox()
    {
        // Arrange
        var feedbackId = await SeedFeedbackAsync(shared: true, BadgeType.Teamwork);
        SetAuth(_coachId);

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/feedback/{feedbackId}", new { sharedWithPlayer = false });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await OutboxAsync()).Should().ContainSingle().Which.State.Should().Be(PraiseState.Withdrawn);
    }

    [Test]
    public async Task Delete_SharedFeedback_PutsItWithdrawnInTheOutbox()
    {
        // Arrange
        var feedbackId = await SeedFeedbackAsync(shared: true, BadgeType.Effort);
        SetAuth(_coachId);

        // Act
        var response = await _client.DeleteAsync($"/v1/feedback/{feedbackId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var fact = (await OutboxAsync()).Should().ContainSingle().Subject;
        fact.FeedbackId.Should().Be(feedbackId);
        fact.State.Should().Be(PraiseState.Withdrawn);
    }

    [Test]
    public async Task Delete_PrivateFeedback_PutsNothingInTheOutbox()
    {
        // Arrange
        var feedbackId = await SeedFeedbackAsync(shared: false, BadgeType.Effort);
        SetAuth(_coachId);

        // Act
        var response = await _client.DeleteAsync($"/v1/feedback/{feedbackId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await OutboxAsync()).Should().BeEmpty();
    }

    /// A context of its own for each read or write, so nothing comes back from a change tracker.
    private CoachingDbContext Db() =>
        new(new DbContextOptionsBuilder<CoachingDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    /// <summary>Feedback the coach gave the player, written straight to the database.</summary>
    private async Task<Guid> SeedFeedbackAsync(bool shared, BadgeType? badge = null, bool deleted = false)
    {
        var feedback = new Feedback
        {
            RecipientUserId = _playerId,
            CoachUserId = _coachId,
            ClubId = _clubId,
            SharedWithPlayer = shared,
            IsDeleted = deleted,
            Content = "<p>Good session</p>",
        };
        if (badge is { } chosen)
            feedback.Praise = new Praise { FeedbackId = feedback.Id, Message = "Well played", BadgeType = chosen };

        await using var db = Db();
        db.Add(feedback);
        await db.SaveChangesAsync();
        return feedback.Id;
    }

    private async Task<Feedback> StoredFeedbackAsync(Guid feedbackId)
    {
        await using var db = Db();
        return await db.Set<Feedback>().AsNoTracking().SingleAsync(f => f.Id == feedbackId);
    }

    /// <summary>
    /// Every praise snapshot the outbox holds, in the order it was published. A row's body is the
    /// MassTransit envelope, with the message itself under "message".
    /// </summary>
    private async Task<List<PraiseGivenEvent>> OutboxAsync()
    {
        await using var db = Db();
        var rows = await db.Set<OutboxMessage>().AsNoTracking().OrderBy(m => m.SequenceNumber).ToListAsync();

        return
        [
            .. rows
                .Where(m => m.MessageType.Contains($":{nameof(PraiseGivenEvent)}"))
                .Select(m =>
                {
                    using var envelope = JsonDocument.Parse(m.Body);
                    return envelope.RootElement.GetProperty("message").Deserialize<PraiseGivenEvent>(JsonOptions)!;
                }),
        ];
    }

    private void SetAuth(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
            new Claim(ClaimTypes.Email, "coach@test.com"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
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
