using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Feedback;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shared.Models;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// The point endpoints authorize the caller against the feedback in the route, so the point in the
/// route has to be on that feedback. They did not check: the coach of any feedback could attach
/// links and drills to, or strip them from, a point on someone else's — their player's view of it
/// included — by naming their own feedback and the other one's point.
/// </summary>
[TestFixture]
[Category("Integration")]
public class FeedbackPointOwnershipControllerTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private readonly Guid _intruderId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    private sealed record Seeded(Guid IntrudersFeedbackId, Guid OthersPointId, Guid OthersMediaId, Guid OthersDrillId, Guid SpareDrillId);

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

    /// <summary>The intruder's own feedback, and another coach's with a point carrying a link and a drill.</summary>
    private async Task<Seeded> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        var intruders = new Feedback { RecipientUserId = Guid.NewGuid(), CoachUserId = _intruderId };
        var others = new Feedback { RecipientUserId = Guid.NewGuid(), CoachUserId = _ownerId };
        var point = new ImprovementPoint { FeedbackId = others.Id, Description = "Platform", Order = 1 };
        var media = new ImprovementPointMedia { ImprovementPointId = point.Id, Url = "https://youtu.be/platform", Type = FeedbackMediaType.Video };
        var drill = new Drill { Name = "Pepper", CreatedByUserId = _ownerId, Visibility = DrillVisibility.Public };
        var spare = new Drill { Name = "Butterfly", CreatedByUserId = _intruderId, Visibility = DrillVisibility.Public };
        var link = new ImprovementPointDrill { ImprovementPointId = point.Id, DrillId = drill.Id };

        db.AddRange(Profile(_ownerId), Profile(_intruderId), intruders, others, point, media, drill, spare, link);
        await db.SaveChangesAsync();
        return new Seeded(intruders.Id, point.Id, media.Id, drill.Id, spare.Id);
    }

    /// <summary>A drill's author is a mirrored profile row, by foreign key.</summary>
    private static UserProfile Profile(Guid id) =>
        new() { Id = id, Name = "Coach", Surname = id.ToString()[..8], Email = $"{id}@test.local", IsActive = true };

    private async Task<(int Media, int Drills)> LiveOnPointAsync(Guid pointId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var media = await db.Set<ImprovementPointMedia>().CountAsync(m => m.ImprovementPointId == pointId && !m.IsDeleted);
        var drills = await db.Set<ImprovementPointDrill>().CountAsync(d => d.ImprovementPointId == pointId && !d.IsDeleted);
        return (media, drills);
    }

    [Test]
    public async Task AddMediaToPoint_OnAnotherFeedbacksPoint_Returns404AndAddsNothing()
    {
        // Arrange
        var seeded = await SeedAsync();
        SetAuth(_intruderId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/feedback/{seeded.IntrudersFeedbackId}/improvement-points/{seeded.OthersPointId}/media",
            new { url = "https://example.com/not-yours", type = "Video", source = "Link" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LiveOnPointAsync(seeded.OthersPointId)).Should().Be((1, 1));
    }

    [Test]
    public async Task RemoveMediaFromPoint_OnAnotherFeedbacksPoint_Returns404AndRemovesNothing()
    {
        // Arrange
        var seeded = await SeedAsync();
        SetAuth(_intruderId);

        // Act
        var response = await _client.DeleteAsync(
            $"/v1/feedback/{seeded.IntrudersFeedbackId}/improvement-points/{seeded.OthersPointId}/media/{seeded.OthersMediaId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LiveOnPointAsync(seeded.OthersPointId)).Should().Be((1, 1));
    }

    [Test]
    public async Task AddDrillToPoint_OnAnotherFeedbacksPoint_Returns404AndAddsNothing()
    {
        // Arrange
        var seeded = await SeedAsync();
        SetAuth(_intruderId);

        // Act
        var response = await _client.PostAsync(
            $"/v1/feedback/{seeded.IntrudersFeedbackId}/improvement-points/{seeded.OthersPointId}/drills/{seeded.SpareDrillId}", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LiveOnPointAsync(seeded.OthersPointId)).Should().Be((1, 1));
    }

    [Test]
    public async Task RemoveDrillFromPoint_OnAnotherFeedbacksPoint_Returns404AndRemovesNothing()
    {
        // Arrange
        var seeded = await SeedAsync();
        SetAuth(_intruderId);

        // Act
        var response = await _client.DeleteAsync(
            $"/v1/feedback/{seeded.IntrudersFeedbackId}/improvement-points/{seeded.OthersPointId}/drills/{seeded.OthersDrillId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LiveOnPointAsync(seeded.OthersPointId)).Should().Be((1, 1));
    }

    private void SetAuth(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.NameId, userId.ToString()),
            new Claim(ClaimTypes.Email, "coach@test.com"),
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
