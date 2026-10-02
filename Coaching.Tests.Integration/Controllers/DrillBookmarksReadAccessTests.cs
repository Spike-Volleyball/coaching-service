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
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shared.Models;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// SPI-6801: the bookmarked drills list kept a drill, with its name, after its author made it
/// private or after the reader left the club it belongs to. The list holds what the reader may still
/// read, by the rule that opens a drill.
/// </summary>
[TestFixture]
[Category("Integration")]
public class DrillBookmarksReadAccessTests
{
    private static readonly Guid AuthorId = Guid.NewGuid();
    private static readonly Guid ReaderId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();

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

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _factory.ClubsGrpcClient.ClearReceivedCalls();
        await _factory.DatabaseResetter.ResetAsync();
    }

    [Test]
    public async Task MyBookmarks_HoldOnlyTheDrillsTheReaderMayStillRead()
    {
        // Arrange — four bookmarks: still public, taken private, in a club the reader is in, in a
        // club the reader has left.
        var stillPublic = Drill("Still public", DrillVisibility.Public);
        var takenPrivate = Drill("Taken private", DrillVisibility.Private);
        var ownClub = Drill("Club drill, still a member", DrillVisibility.Private, ClubId);
        var leftClub = Drill("Club drill, left the club", DrillVisibility.Private, Guid.NewGuid());
        await SeedAsync(ReaderId, stillPublic, takenPrivate, ownClub, leftClub);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(ReaderId, ClubId).Returns(true);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(ReaderId, leftClub.ClubId!.Value).Returns(false);
        SetAuth(ReaderId);

        // Act
        var bookmarks = await MyBookmarksAsync();

        // Assert
        bookmarks.Select(b => b.Name).Should().BeEquivalentTo("Still public", "Club drill, still a member");
    }

    [Test]
    public async Task MyBookmarks_KeepTheAuthorsOwnPrivateDrill()
    {
        // Arrange
        var own = Drill("My private drill", DrillVisibility.Private);
        await SeedAsync(AuthorId, own);
        SetAuth(AuthorId);

        // Act
        var bookmarks = await MyBookmarksAsync();

        // Assert
        bookmarks.Should().ContainSingle().Which.Name.Should().Be("My private drill");
    }

    private async Task<List<BookmarkedDrillDto>> MyBookmarksAsync()
    {
        var response = await _client.GetAsync("/v1/me/drills/bookmarks");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<BookmarkedDrillDto>>(JsonOptions))!;
    }

    private static Drill Drill(string name, DrillVisibility visibility, Guid? clubId = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CreatedByUserId = AuthorId,
        Visibility = visibility,
        ClubId = clubId
    };

    private async Task SeedAsync(Guid bookmarkedBy, params Drill[] drills)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(Profile(AuthorId, "Author"), Profile(ReaderId, "Reader"));
        db.AddRange(drills);
        db.AddRange(drills.Select(drill => new DrillBookmark { DrillId = drill.Id, UserId = bookmarkedBy }));
        await db.SaveChangesAsync();
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
                new Claim(ClaimTypes.Email, "reader@test.local"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
