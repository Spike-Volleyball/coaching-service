using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Shared.Messaging.Contracts.Events.Coaching;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// A board somebody makes reaches the outbox once, when it is first saved. Editing it sends nothing
/// more, and neither do the starter boards a shelf is seeded with.
/// </summary>
[TestFixture]
[Category("Integration")]
public class TacticsBoardFactsTests
{
    private const string Document = """{"frames":[{"id":"f1"}],"system":"5-1"}""";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory(isolateMessageBroker: true);
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
    public async Task SaveBoard_ANewBoard_PutsItsMakerInTheOutbox()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        SetAuth(userId);

        // Act
        await SaveAsync(boardId, expectedVersion: null);

        // Assert
        var made = (await OutboxAsync()).Should().ContainSingle().Subject;
        made.BoardId.Should().Be(boardId);
        made.CreatorUserId.Should().Be(userId);
        made.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        made.SnapshotAt.Should().Be(made.CreatedAt);
    }

    [Test]
    public async Task SaveBoard_AnEditToABoard_PublishesNothingMore()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        await SaveAsync(boardId, expectedVersion: null);

        // Act
        await SaveAsync(boardId, expectedVersion: 1);

        // Assert
        (await OutboxAsync()).Should().ContainSingle();
    }

    [Test]
    public async Task SeedBoards_StarterBoards_PublishNothing()
    {
        // Arrange
        SetAuth(Guid.NewGuid());

        // Act
        var response = await _client.PostAsJsonAsync("/v1/tactics-boards/seed", new
        {
            scope = "personal",
            boards = new[]
            {
                new { id = Guid.NewGuid(), title = "Starter", category = "Match day", system = "5-1", isFavorite = false, frameCount = 1, document = Document },
            },
        }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await OutboxAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Every board the outbox says somebody made, in the order it was published. A row's body is the
    /// MassTransit envelope, with the message itself under "message".
    /// </summary>
    private async Task<List<TacticsBoardCreatedEvent>> OutboxAsync()
    {
        await using var db = new CoachingDbContext(
            new DbContextOptionsBuilder<CoachingDbContext>().UseNpgsql(_factory.ConnectionString).Options);
        var rows = await db.Set<OutboxMessage>().AsNoTracking().OrderBy(m => m.SequenceNumber).ToListAsync();

        return
        [
            .. rows
                .Where(m => m.MessageType.Contains($":{nameof(TacticsBoardCreatedEvent)}"))
                .Select(m =>
                {
                    using var envelope = JsonDocument.Parse(m.Body);
                    return envelope.RootElement.GetProperty("message").Deserialize<TacticsBoardCreatedEvent>(JsonOptions)!;
                }),
        ];
    }

    private async Task SaveAsync(Guid boardId, int? expectedVersion)
    {
        var response = await _client.PutAsJsonAsync($"/v1/tactics-boards/{boardId}", new
        {
            title = "Serve receive",
            category = "Match day",
            system = "5-1",
            scope = "personal",
            isFavorite = false,
            frameCount = 1,
            document = Document,
            expectedVersion,
        }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private void SetAuth(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var token = new JwtSecurityToken(
            issuer: CoachingApiFactory.JwtIssuer,
            audience: CoachingApiFactory.JwtAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
                new Claim(ClaimTypes.Email, "test@example.com"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
