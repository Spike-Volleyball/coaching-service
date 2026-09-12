using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Coaching.Domain.Enums;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Shared.Enums;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// The scope travels as the lowercase string the editor writes into every board it exports, so
/// these tests send and read it as the client does rather than through the server's own enum.
/// </summary>
[TestFixture]
[Category("Integration")]
public class TacticsControllerTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string Document = """{"frames":[{"id":"f1"}],"system":"5-1"}""";

    private sealed record BoardResponse(
        Guid Id, string Title, string Category, string System, string Scope,
        Guid? ClubId, Guid? TeamId, Guid? FolderId, bool IsFavorite,
        int FrameCount, int Version, DateTime UpdatedAt, string? Document);

    private sealed record FolderResponse(
        Guid Id, string Name, string Scope, Guid? ClubId, Guid? TeamId,
        Guid? ParentFolderId, int Position, DateTime UpdatedAt);

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
        _factory.ClubsGrpcClient.ClearSubstitute();
    }

    [Test]
    public async Task SaveBoard_WithoutAuthentication_ReturnsUnauthorizedAndPersistsNothing()
    {
        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-boards/{Guid.NewGuid()}", BoardRequest(), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountBoardsAsync()).Should().Be(0);
    }

    [Test]
    public async Task SaveBoard_NewPersonalBoard_StoresItAtTheClientsOwnId()
    {
        // Arrange — the editor picks the id, because it saves the board it is already holding.
        var userId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        SetAuth(userId);

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/tactics-boards/{boardId}", BoardRequest(), JsonOptions);

        // Assert - HTTP representation
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await response.Content.ReadFromJsonAsync<BoardResponse>(JsonOptions);
        saved.Should().NotBeNull();
        saved!.Id.Should().Be(boardId);
        saved.Scope.Should().Be("personal");
        saved.Version.Should().Be(1);
        saved.FrameCount.Should().Be(1);
        saved.Document.Should().Be(Document);

        // Assert - durable state
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var stored = await db.TacticsBoards.AsNoTracking().SingleAsync();
        stored.Id.Should().Be(boardId);
        stored.OwnerUserId.Should().Be(userId);
        stored.Scope.Should().Be(TacticsScope.Personal);
        stored.ClubId.Should().BeNull();
        stored.Version.Should().Be(1);
    }

    [Test]
    public async Task SaveBoard_TwiceWithTheVersionItRead_UpdatesTheOneBoard()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        await SaveAsync(boardId, BoardRequest());

        // Act
        var second = await _client.PutAsJsonAsync(
            $"/v1/tactics-boards/{boardId}",
            BoardRequest(title: "Serve receive, revised", expectedVersion: 1), JsonOptions);

        // Assert
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await second.Content.ReadFromJsonAsync<BoardResponse>(JsonOptions);
        saved!.Version.Should().Be(2);
        saved.Title.Should().Be("Serve receive, revised");
        (await CountBoardsAsync()).Should().Be(1);
    }

    [Test]
    public async Task SaveBoard_WithAVersionSomebodyElseMovedPast_ReturnsConflictAndKeepsTheirWork()
    {
        // Arrange — two coaches on one club shelf, both holding version 1.
        var clubId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _factory.ClubsGrpcClient.IsClubStaffAsync(Arg.Any<Guid>(), clubId).Returns(true);

        var boardId = Guid.NewGuid();
        SetAuth(first);
        await SaveAsync(boardId, BoardRequest(scope: "club", clubId: clubId));
        await SaveAsync(boardId, BoardRequest(
            scope: "club", clubId: clubId, title: "First coach's edit", expectedVersion: 1));

        // Act — the second coach saves what they read before that edit landed.
        SetAuth(second);
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-boards/{boardId}",
            BoardRequest(scope: "club", clubId: clubId, title: "Second coach's edit", expectedVersion: 1),
            JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var stored = await db.TacticsBoards.AsNoTracking().SingleAsync();
        stored.Title.Should().Be("First coach's edit");
        stored.Version.Should().Be(2);
    }

    [Test]
    public async Task SaveBoard_OverAnExistingBoardWithNoVersionAtAll_ReturnsConflict()
    {
        // Arrange — a client that never read the board cannot know what it is overwriting.
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        await SaveAsync(boardId, BoardRequest());

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-boards/{boardId}", BoardRequest(title: "Blind write"), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task SaveBoard_WithADocumentThatIsNotJson_ReturnsBadRequestAndPersistsNothing()
    {
        // Arrange
        SetAuth(Guid.NewGuid());

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-boards/{Guid.NewGuid()}", BoardRequest(document: "not a board"), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountBoardsAsync()).Should().Be(0);
    }

    [Test]
    public async Task SaveBoard_WithABlankTitle_ReturnsBadRequest()
    {
        // Arrange
        SetAuth(Guid.NewGuid());

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-boards/{Guid.NewGuid()}", BoardRequest(title: "   "), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetBoard_SomebodyElsesPersonalBoard_ReturnsForbidden()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        await SaveAsync(boardId, BoardRequest());

        // Act
        SetAuth(Guid.NewGuid());
        var response = await _client.GetAsync($"/v1/tactics-boards/{boardId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task GetBoards_PersonalShelf_ListsOnlyYourOwnAndLeavesOutTheDocument()
    {
        // Arrange
        var mine = Guid.NewGuid();
        SetAuth(mine);
        await SaveAsync(Guid.NewGuid(), BoardRequest(title: "Mine"));
        SetAuth(Guid.NewGuid());
        await SaveAsync(Guid.NewGuid(), BoardRequest(title: "Theirs"));

        // Act
        SetAuth(mine);
        var response = await _client.GetAsync("/v1/tactics-boards?scope=personal");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var listed = await response.Content.ReadFromJsonAsync<List<BoardResponse>>(JsonOptions);
        listed.Should().ContainSingle().Which.Title.Should().Be("Mine");
        // A shelf of boards would otherwise carry every scene of every board on it.
        listed![0].Document.Should().BeNull();
    }

    [Test]
    public async Task SaveBoard_OnAClubShelfWithoutStanding_ReturnsForbiddenAndPersistsNothing()
    {
        // Arrange — a member of the club, but not one of its staff.
        var clubId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _factory.ClubsGrpcClient.IsClubStaffAsync(userId, clubId).Returns(false);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(userId, clubId).Returns(true);
        SetAuth(userId);

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-boards/{Guid.NewGuid()}", BoardRequest(scope: "club", clubId: clubId), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountBoardsAsync()).Should().Be(0);
    }

    [Test]
    public async Task GetBoards_ClubShelf_ShowsEveryCoachesBoardToTheClubsStaff()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        _factory.ClubsGrpcClient.IsClubStaffAsync(Arg.Any<Guid>(), clubId).Returns(true);
        SetAuth(Guid.NewGuid());
        await SaveAsync(Guid.NewGuid(), BoardRequest(scope: "club", clubId: clubId, title: "Head coach's board"));

        // Act
        SetAuth(Guid.NewGuid());
        var response = await _client.GetAsync($"/v1/tactics-boards?scope=club&clubId={clubId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var listed = await response.Content.ReadFromJsonAsync<List<BoardResponse>>(JsonOptions);
        listed.Should().ContainSingle().Which.Title.Should().Be("Head coach's board");
        listed![0].Scope.Should().Be("club");
    }

    [Test]
    public async Task SaveBoard_OnATeamShelf_StoresTheClubTheTeamActuallyBelongsTo()
    {
        // Arrange — the caller names a club they do run; the team belongs to a different one.
        var teamId = Guid.NewGuid();
        var owningClub = Guid.NewGuid();
        var claimedClub = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _factory.ClubsGrpcClient.IsUnitStaffAsync(userId, ContextType.Team, teamId).Returns(true);
        _factory.ClubsGrpcClient.ResolveClubIdAsync(ContextType.Team, teamId).Returns(owningClub);
        SetAuth(userId);

        // Act
        await SaveAsync(Guid.NewGuid(), BoardRequest(scope: "team", teamId: teamId, clubId: claimedClub));

        // Assert
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var stored = await db.TacticsBoards.AsNoTracking().SingleAsync();
        stored.TeamId.Should().Be(teamId);
        stored.ClubId.Should().Be(owningClub);
    }

    [Test]
    public async Task DeleteBoard_ItsOwner_RemovesItAndReturnsNoContent()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        await SaveAsync(boardId, BoardRequest());

        // Act
        var response = await _client.DeleteAsync($"/v1/tactics-boards/{boardId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CountBoardsAsync()).Should().Be(0);
    }

    [Test]
    public async Task DeleteBoard_SomebodyElsesPersonalBoard_ReturnsForbiddenAndKeepsIt()
    {
        // Arrange
        var boardId = Guid.NewGuid();
        SetAuth(Guid.NewGuid());
        await SaveAsync(boardId, BoardRequest());

        // Act
        SetAuth(Guid.NewGuid());
        var response = await _client.DeleteAsync($"/v1/tactics-boards/{boardId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountBoardsAsync()).Should().Be(1);
    }

    [Test]
    public async Task GetBoard_AnIdThatIsNotThere_ReturnsNotFound()
    {
        // Arrange
        SetAuth(Guid.NewGuid());

        // Act
        var response = await _client.GetAsync($"/v1/tactics-boards/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task CreateFolder_ThenList_ReturnsItOnTheSameShelf()
    {
        // Arrange
        SetAuth(Guid.NewGuid());

        // Act
        var created = await _client.PostAsJsonAsync(
            "/v1/tactics-folders", new { scope = "personal", name = "Match day" }, JsonOptions);

        // Assert
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var listed = await _client.GetFromJsonAsync<List<FolderResponse>>("/v1/tactics-folders?scope=personal", JsonOptions);
        listed.Should().ContainSingle().Which.Name.Should().Be("Match day");
        listed![0].Scope.Should().Be("personal");
    }

    [Test]
    public async Task CreateFolder_AppendsEachOneAfterTheLast()
    {
        // Arrange — the order a coach made them in is the order they read back in, not alphabetical.
        SetAuth(Guid.NewGuid());

        // Act
        await CreateFolderAsync("Warmups");
        await CreateFolderAsync("Blocking");
        await CreateFolderAsync("Serve receive");

        // Assert
        var listed = await _client.GetFromJsonAsync<List<FolderResponse>>("/v1/tactics-folders?scope=personal", JsonOptions);
        listed!.Select(f => f.Name).Should().Equal("Warmups", "Blocking", "Serve receive");
        listed.Select(f => f.Position).Should().Equal(0, 1, 2);
    }

    [Test]
    public async Task MoveFolder_ReordersItsSiblingsAndReturnsTheWholeShelf()
    {
        // Arrange
        SetAuth(Guid.NewGuid());
        await CreateFolderAsync("Warmups");
        await CreateFolderAsync("Blocking");
        var third = await CreateFolderAsync("Serve receive");

        // Act — drag the last one to the front.
        var shelf = await MoveFolderAsync(third.Id, null, 0);

        // Assert — positions are dense, so "first" is always 0.
        shelf.Select(f => f.Name).Should().Equal("Serve receive", "Warmups", "Blocking");
        shelf.Select(f => f.Position).Should().Equal(0, 1, 2);
    }

    [Test]
    public async Task MoveFolder_IntoAnother_NestsIt()
    {
        // Arrange
        SetAuth(Guid.NewGuid());
        var season = await CreateFolderAsync("Season 26");
        var blocking = await CreateFolderAsync("Blocking");

        // Act
        var shelf = await MoveFolderAsync(blocking.Id, season.Id, 0);

        // Assert
        shelf.Single(f => f.Id == blocking.Id).ParentFolderId.Should().Be(season.Id);
        // Leaving the top level renumbers what is left there.
        shelf.Single(f => f.Id == season.Id).Position.Should().Be(0);
    }

    [Test]
    public async Task MoveFolder_IntoItself_ReturnsBadRequest()
    {
        // Arrange
        SetAuth(Guid.NewGuid());
        var folder = await CreateFolderAsync("Season 26");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-folders/{folder.Id}/placement", new { parentFolderId = folder.Id, position = 0 }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task MoveFolder_IntoItsOwnChild_ReturnsBadRequest()
    {
        // Arrange — the move would cut the subtree loose from the shelf entirely.
        SetAuth(Guid.NewGuid());
        var season = await CreateFolderAsync("Season 26");
        var blocking = await CreateFolderAsync("Blocking", season.Id);

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-folders/{season.Id}/placement", new { parentFolderId = blocking.Id, position = 0 }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task MoveFolder_DeeperThanTheCap_ReturnsBadRequest()
    {
        // Arrange — three levels is as deep as the rail can indent and still read.
        SetAuth(Guid.NewGuid());
        var one = await CreateFolderAsync("One");
        var two = await CreateFolderAsync("Two", one.Id);
        var three = await CreateFolderAsync("Three", two.Id);
        var four = await CreateFolderAsync("Four");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-folders/{four.Id}/placement", new { parentFolderId = three.Id, position = 0 }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task MoveFolder_ASubtreeThatWouldNotFit_ReturnsBadRequest()
    {
        // Arrange — the folder being dragged fits, but what is filed under it does not.
        SetAuth(Guid.NewGuid());
        var one = await CreateFolderAsync("One");
        var two = await CreateFolderAsync("Two", one.Id);
        var tall = await CreateFolderAsync("Tall");
        await CreateFolderAsync("Under it", tall.Id);

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-folders/{tall.Id}/placement", new { parentFolderId = two.Id, position = 0 }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task DeleteFolder_PromotesTheFoldersInsideItRatherThanTakingThemWithIt()
    {
        // Arrange
        SetAuth(Guid.NewGuid());
        var season = await CreateFolderAsync("Season 26");
        var blocking = await CreateFolderAsync("Blocking", season.Id);

        // Act
        var response = await _client.DeleteAsync($"/v1/tactics-folders/{season.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var listed = await _client.GetFromJsonAsync<List<FolderResponse>>("/v1/tactics-folders?scope=personal", JsonOptions);
        listed.Should().ContainSingle().Which.Id.Should().Be(blocking.Id);
        listed![0].ParentFolderId.Should().BeNull();
    }

    [Test]
    public async Task MoveFolder_SomebodyElsesPersonalFolder_ReturnsForbidden()
    {
        // Arrange
        SetAuth(Guid.NewGuid());
        var folder = await CreateFolderAsync("Match day");

        // Act
        SetAuth(Guid.NewGuid());
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-folders/{folder.Id}/placement", new { parentFolderId = (Guid?)null, position = 0 }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task DeleteFolder_LeavesTheBoardsFiledInItOnTheShelf()
    {
        // Arrange — losing a folder must never lose the work inside it.
        SetAuth(Guid.NewGuid());
        var folder = await CreateFolderAsync("Match day");
        var boardId = Guid.NewGuid();
        await SaveAsync(boardId, BoardRequest(folderId: folder.Id));

        // Act
        var response = await _client.DeleteAsync($"/v1/tactics-folders/{folder.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var stored = await db.TacticsBoards.AsNoTracking().SingleAsync();
        stored.Id.Should().Be(boardId);
        stored.FolderId.Should().BeNull();
    }

    [Test]
    public async Task RenameFolder_SomebodyElsesPersonalFolder_ReturnsForbidden()
    {
        // Arrange
        SetAuth(Guid.NewGuid());
        var folder = await CreateFolderAsync("Match day");

        // Act
        SetAuth(Guid.NewGuid());
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-folders/{folder.Id}", new { name = "Renamed" }, JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task SaveBoard_IntoAFolderOnAnotherShelf_ReturnsBadRequest()
    {
        // Arrange — a club board hidden inside somebody's personal folder would be unreachable
        // from the shelf it is authorised against.
        var owner = Guid.NewGuid();
        var clubId = Guid.NewGuid();
        _factory.ClubsGrpcClient.IsClubStaffAsync(owner, clubId).Returns(true);
        SetAuth(owner);
        var personalFolder = await CreateFolderAsync("Mine");

        // Act
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-boards/{Guid.NewGuid()}",
            BoardRequest(scope: "club", clubId: clubId, folderId: personalFolder.Id), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SeedBoards_OnAnEmptyShelf_WritesThemInTheOrderTheyWereAuthored()
    {
        // Arrange
        SetAuth(Guid.NewGuid());

        // Act
        var response = await _client.PostAsJsonAsync("/v1/tactics-boards/seed", SeedRequest(), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var seeded = await response.Content.ReadFromJsonAsync<List<BoardResponse>>(JsonOptions);
        seeded!.Select(b => b.Title).Should().Equal("First", "Second", "Third");
        seeded.Should().OnlyContain(b => b.Version == 1);
    }

    [Test]
    public async Task SeedBoards_AskedTwice_LeavesOneOfEveryStarterBoard()
    {
        // Arrange — React runs its effects twice in development, so the client will ask twice.
        SetAuth(Guid.NewGuid());
        await _client.PostAsJsonAsync("/v1/tactics-boards/seed", SeedRequest(), JsonOptions);

        // Act
        var second = await _client.PostAsJsonAsync("/v1/tactics-boards/seed", SeedRequest(), JsonOptions);

        // Assert
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var listed = await second.Content.ReadFromJsonAsync<List<BoardResponse>>(JsonOptions);
        listed!.Select(b => b.Title).Should().BeEquivalentTo(["First", "Second", "Third"]);
        (await CountBoardsAsync()).Should().Be(3);
    }

    [Test]
    public async Task GetBoards_AFreshlySeededShelf_ComesBackInTheSameOrderEveryTime()
    {
        // Arrange — one batch means one timestamp, so recency alone cannot order them.
        SetAuth(Guid.NewGuid());
        await _client.PostAsJsonAsync("/v1/tactics-boards/seed", SeedRequest(), JsonOptions);

        // Act
        var first = await _client.GetFromJsonAsync<List<BoardResponse>>("/v1/tactics-boards?scope=personal", JsonOptions);
        var again = await _client.GetFromJsonAsync<List<BoardResponse>>("/v1/tactics-boards?scope=personal", JsonOptions);

        // Assert
        again!.Select(b => b.Id).Should().Equal(first!.Select(b => b.Id));
    }

    [Test]
    public async Task SeedBoards_OnAClubShelfWithoutStanding_ReturnsForbidden()
    {
        // Arrange
        var clubId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _factory.ClubsGrpcClient.IsClubStaffAsync(userId, clubId).Returns(false);
        SetAuth(userId);

        // Act
        var response = await _client.PostAsJsonAsync(
            "/v1/tactics-boards/seed", SeedRequest(scope: "club", clubId: clubId), JsonOptions);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CountBoardsAsync()).Should().Be(0);
    }

    private static object SeedRequest(string scope = "personal", Guid? clubId = null) => new
    {
        scope,
        clubId,
        teamId = (Guid?)null,
        boards = new[] { "First", "Second", "Third" }.Select(title => new
        {
            id = Guid.NewGuid(),
            title,
            category = "Match day",
            system = "5-1",
            folderId = (Guid?)null,
            isFavorite = false,
            frameCount = 1,
            document = Document
        }).ToArray()
    };

    private async Task<FolderResponse> CreateFolderAsync(string name, Guid? parentFolderId = null)
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/tactics-folders", new { scope = "personal", name, parentFolderId }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<FolderResponse>(JsonOptions))!;
    }

    private async Task<List<FolderResponse>> MoveFolderAsync(Guid folderId, Guid? parentFolderId, int position)
    {
        var response = await _client.PutAsJsonAsync(
            $"/v1/tactics-folders/{folderId}/placement", new { parentFolderId, position }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<FolderResponse>>(JsonOptions))!;
    }

    private async Task SaveAsync(Guid boardId, object request)
    {
        var response = await _client.PutAsJsonAsync($"/v1/tactics-boards/{boardId}", request, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<int> CountBoardsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        return await db.TacticsBoards.CountAsync();
    }

    private static object BoardRequest(
        string title = "Serve receive",
        string scope = "personal",
        Guid? clubId = null,
        Guid? teamId = null,
        Guid? folderId = null,
        string document = Document,
        int? expectedVersion = null) => new
        {
            title,
            category = "Match day",
            system = "5-1",
            scope,
            clubId,
            teamId,
            folderId,
            isFavorite = false,
            frameCount = 1,
            document,
            expectedVersion
        };

    private void SetAuth(Guid userId)
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GenerateJwt(userId));
    }

    private static string GenerateJwt(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
            new Claim(ClaimTypes.Email, "test@example.com"),
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
