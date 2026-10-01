using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Tactics;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Shared.Messaging.Contracts.Events.Coaching;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// A board somebody made reaches the outbox once it has a drawing on it, and again at every save
/// after, with the tools it uses then. A blank new board waits for its drawing, and a starter board
/// or one from before origins were kept never tells at all.
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
        _factory.ClubsGrpcClient.ClearSubstitute();
    }

    [Test]
    public async Task SaveBoard_ABlankNewBoard_WaitsForItsDrawing()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());

        // Act
        await SaveAsync(boardId);

        // Assert
        (await OutboxAsync()).Should().BeEmpty();
        var stored = await StoredAsync(boardId);
        stored.Origin.Should().Be(TacticsBoardOrigin.Made);
        stored.DrawnAt.Should().BeNull();
    }

    [Test]
    public async Task SaveBoard_TheSaveAfterABlankOne_IsTheDrawing()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        SetAuth(userId);
        await SaveAsync(boardId);

        // Act
        await SaveAsync(boardId, expectedVersion: 1, tools: ["arrow", "heatmap"]);

        // Assert
        var drawn = (await OutboxAsync()).Should().ContainSingle().Subject;
        drawn.BoardId.Should().Be(boardId);
        drawn.MakerUserId.Should().Be(userId);
        drawn.Tools.Should().Equal("arrow", "heatmap");
        drawn.DrawnAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        drawn.SnapshotAt.Should().BeCloseTo(drawn.DrawnAt, TimeSpan.FromMilliseconds(1));
        (await StoredAsync(boardId)).DrawnAt.Should().Be(drawn.DrawnAt);
    }

    [TestCase(new[] { "projection" }, 1, TestName = "SaveBoard_ANewBoardThatUsesATool_IsDrawnAlready")]
    [TestCase(new string[0], 3, TestName = "SaveBoard_ANewBoardWithMoreThanOneScene_IsDrawnAlready")]
    public async Task SaveBoard_ANewBoardWithADrawing_IsDrawnAlready(string[] tools, int frameCount)
    {
        // Arrange — a copy, an import, or a board drawn before its first save.
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());

        // Act
        await SaveAsync(boardId, tools: tools, frameCount: frameCount);

        // Assert
        (await OutboxAsync()).Should().ContainSingle().Which.Tools.Should().Equal(tools);
    }

    [Test]
    public async Task SaveBoard_EverySaveAfterTheDrawing_SendsWhatItUsesThen_FromTheSameMoment()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        await SaveAsync(boardId, tools: ["arrow"]);

        // Act
        await SaveAsync(boardId, expectedVersion: 1, tools: ["arrow", "heatmap", "projection"]);
        await SaveAsync(boardId, expectedVersion: 2);

        // Assert
        var sent = await OutboxAsync();
        sent.Select(copy => string.Join(",", copy.Tools)).Should().Equal("arrow", "arrow,heatmap,projection", "");
        sent.Select(copy => copy.DrawnAt).Distinct().Should().ContainSingle();
        sent.Select(copy => copy.SnapshotAt).Should().BeInAscendingOrder();
    }

    [Test]
    public async Task SaveBoard_ToolsInAnyCaseAndTwice_AreSentOnceEachInLowerCase()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());

        // Act
        await SaveAsync(boardId, tools: [" Heatmap ", "heatmap", "ARROW"]);

        // Assert
        (await OutboxAsync()).Should().ContainSingle().Which.Tools.Should().Equal("heatmap", "arrow");
    }

    [Test]
    public async Task SaveBoard_WithMoreToolsThanABoardHas_ReturnsBadRequestAndPersistsNothing()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        var tools = Enumerable.Range(0, TacticsBoardService.MaxTools + 1).Select(i => $"tool-{i}").ToArray();

        // Act
        var response = await PutAsync(boardId, tools: tools);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await OutboxAsync()).Should().BeEmpty();
        (await FindAsync(boardId)).Should().BeNull();
    }

    [Test]
    public async Task SaveBoard_AStarterBoardDrawnOn_NeverTells()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        var seeded = await _client.PostAsJsonAsync("/v1/tactics-boards/seed", new
        {
            scope = "personal",
            boards = new[]
            {
                new { id = boardId, title = "Starter", category = "Match day", system = "5-1", isFavorite = false, frameCount = 3, document = Document },
            },
        }, JsonOptions);
        seeded.StatusCode.Should().Be(HttpStatusCode.OK, await seeded.Content.ReadAsStringAsync());

        // Act
        await SaveAsync(boardId, expectedVersion: 1, tools: ["arrow", "heatmap"], frameCount: 3);

        // Assert
        (await OutboxAsync()).Should().BeEmpty();
        var stored = await StoredAsync(boardId);
        stored.Origin.Should().Be(TacticsBoardOrigin.Starter);
        stored.DrawnAt.Should().BeNull();
    }

    [Test]
    public async Task SaveBoard_ABoardFromBeforeOriginsWereKept_NeverTells()
    {
        // Arrange — as the migration leaves every board that was already there.
        var userId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        await using (var db = Db())
        {
            db.TacticsBoards.Add(new TacticsBoard
            {
                Id = boardId,
                Title = "Serve receive",
                Category = "Match day",
                System = "5-1",
                Scope = TacticsScope.Personal,
                OwnerUserId = userId,
                FrameCount = 1,
                Version = 1,
                Document = Document,
            });
            await db.SaveChangesAsync();
        }
        SetAuth(userId);

        // Act
        await SaveAsync(boardId, expectedVersion: 1, tools: ["arrow"]);

        // Assert
        (await OutboxAsync()).Should().BeEmpty();
        (await StoredAsync(boardId)).Origin.Should().Be(TacticsBoardOrigin.Unrecorded);
    }

    [Test]
    public async Task SaveBoard_AnotherCoachDrawingOnABlankClubBoard_IsNotItsMakersDrawing_UntilTheMakerSaves()
    {
        // Arrange — two coaches on one club shelf; the first makes a board and leaves it blank.
        var clubId = Guid.NewGuid();
        var maker = Guid.NewGuid();
        var other = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        _factory.ClubsGrpcClient.IsClubStaffAsync(Arg.Any<Guid>(), clubId).Returns(true);
        SetAuth(maker);
        await SaveAsync(boardId, scope: "club", clubId: clubId);

        // Act
        SetAuth(other);
        await SaveAsync(boardId, expectedVersion: 1, tools: ["arrow"], scope: "club", clubId: clubId);
        var afterTheOther = await OutboxAsync();
        SetAuth(maker);
        await SaveAsync(boardId, expectedVersion: 2, tools: ["arrow", "ink"], scope: "club", clubId: clubId);
        SetAuth(other);
        await SaveAsync(boardId, expectedVersion: 3, tools: ["arrow", "ink", "heatmap"], scope: "club", clubId: clubId);

        // Assert — once it is its maker's drawing, whoever saves it sends what it uses.
        afterTheOther.Should().BeEmpty();
        var sent = await OutboxAsync();
        sent.Should().HaveCount(2).And.OnlyContain(copy => copy.MakerUserId == maker);
        sent[1].Tools.Should().Equal("arrow", "ink", "heatmap");
    }

    /// <summary>
    /// Every copy the outbox holds, in the order it was published. A row's body is the MassTransit
    /// envelope, with the message itself under "message".
    /// </summary>
    private async Task<List<TacticsBoardDrawnEvent>> OutboxAsync()
    {
        await using var db = Db();
        var rows = await db.Set<OutboxMessage>().AsNoTracking().OrderBy(m => m.SequenceNumber).ToListAsync();

        return
        [
            .. rows
                .Where(m => m.MessageType.Contains($":{nameof(TacticsBoardDrawnEvent)}"))
                .Select(m =>
                {
                    using var envelope = JsonDocument.Parse(m.Body);
                    return envelope.RootElement.GetProperty("message").Deserialize<TacticsBoardDrawnEvent>(JsonOptions)!;
                }),
        ];
    }

    /// A context of its own for each read or write, so nothing comes back from a change tracker.
    private CoachingDbContext Db() =>
        new(new DbContextOptionsBuilder<CoachingDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private async Task<TacticsBoard?> FindAsync(Guid boardId)
    {
        await using var db = Db();
        return await db.TacticsBoards.AsNoTracking().SingleOrDefaultAsync(b => b.Id == boardId);
    }

    private async Task<TacticsBoard> StoredAsync(Guid boardId) =>
        await FindAsync(boardId) ?? throw new InvalidOperationException($"No board {boardId} was stored.");

    private Task<HttpResponseMessage> PutAsync(
        Guid boardId,
        int? expectedVersion = null,
        string[]? tools = null,
        int frameCount = 1,
        string scope = "personal",
        Guid? clubId = null) =>
        _client.PutAsJsonAsync($"/v1/tactics-boards/{boardId}", new
        {
            title = "Serve receive",
            category = "Match day",
            system = "5-1",
            scope,
            clubId,
            isFavorite = false,
            frameCount,
            document = Document,
            tools,
            expectedVersion,
        }, JsonOptions);

    private async Task SaveAsync(
        Guid boardId,
        int? expectedVersion = null,
        string[]? tools = null,
        int frameCount = 1,
        string scope = "personal",
        Guid? clubId = null)
    {
        var response = await PutAsync(boardId, expectedVersion, tools, frameCount, scope, clubId);
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
