using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// A feedback's links are stored as sent and later opened by a tap, so the API takes http and https
/// only — every write that accepts one answers a javascript: or data: link with a 400 naming the
/// field, and stores nothing (SPI-6458). Files uploaded to our bucket are https URLs and pass.
/// </summary>
[TestFixture]
[Category("Integration")]
public class FeedbackLinkSchemeControllerTests
{
    private const string Hostile = "javascript:alert(document.cookie)";

    private CoachingApiFactory _factory = null!;
    private WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;

    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _playerId = Guid.NewGuid();
    private readonly Guid _clubId = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();

        // Reads presign files in our bucket; S3 alone is substituted so the real signer runs
        // without credentials. Everything goes through this one derived host.
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns(call => $"https://cdn.test/{call.Arg<GetPreSignedUrlRequest>().Key}?X-Amz-Signature=read-only");
        _host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(s3)));
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

    private async Task<(Guid FeedbackId, Guid PointId)> SeedFeedbackWithPointAsync(params FeedbackMedia[] attachments)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        var feedback = new Feedback { RecipientUserId = _playerId, CoachUserId = _coachId, ClubId = _clubId, Content = "<p>Keep the platform still</p>" };
        var point = new ImprovementPoint { FeedbackId = feedback.Id, Description = "Platform", Order = 1 };
        foreach (var attachment in attachments)
            attachment.FeedbackId = feedback.Id;

        db.Set<Feedback>().Add(feedback);
        db.Set<ImprovementPoint>().Add(point);
        db.Set<FeedbackMedia>().AddRange(attachments);
        await db.SaveChangesAsync();
        return (feedback.Id, point.Id);
    }

    private async Task<T> ReadDbAsync<T>(Func<CoachingDbContext, Task<T>> read)
    {
        using var scope = _host.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<CoachingDbContext>());
    }

    /// <summary>The field each refusal names, read off the shared problem-details body.</summary>
    private static async Task<List<(string Field, string Code)>> FieldErrorsOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
        return body.RootElement.GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("field").GetString()!, e.GetProperty("code").GetString()!))
            .ToList();
    }

    [Test]
    public async Task Create_WithAJavascriptAttachment_Returns400NamingItAndStoresNothing()
    {
        // Arrange
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/feedback", new
        {
            recipientUserId = _playerId,
            clubId = _clubId,
            content = "<p>Watch this</p>",
            attachments = new[] { new { url = Hostile, type = "Video", title = "Tap me" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldErrorsOf(response)).Should().Equal(("attachments[0].url", "INVALID_URL"));
        (await ReadDbAsync(db => db.Set<Feedback>().CountAsync())).Should().Be(0);
    }

    [Test]
    public async Task Create_WithADataLinkOnAPoint_Returns400NamingThePointsLinkAndStoresNothing()
    {
        // Arrange
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/feedback", new
        {
            recipientUserId = _playerId,
            clubId = _clubId,
            improvementPoints = new[]
            {
                new
                {
                    description = "Footwork",
                    mediaLinks = new[] { new { url = "data:text/html,<script>alert(1)</script>", type = "Document", source = "Link" } },
                },
            },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldErrorsOf(response)).Should().Equal(("improvementPoints[0].mediaLinks[0].url", "INVALID_URL"));
        (await ReadDbAsync(db => db.Set<Feedback>().CountAsync())).Should().Be(0);
        (await ReadDbAsync(db => db.Set<ImprovementPoint>().CountAsync())).Should().Be(0);
    }

    [Test]
    public async Task Create_WithHttpsLinksAndAnUploadedFile_Returns201()
    {
        // Arrange
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/feedback", new
        {
            recipientUserId = _playerId,
            clubId = _clubId,
            attachments = new[] { new { url = $"https://cdn.test/feedback/{_coachId}/{Guid.NewGuid()}.jpg", type = "Image" } },
            improvementPoints = new[]
            {
                new { description = "Footwork", mediaLinks = new[] { new { url = "http://youtu.be/footwork", type = "Video", source = "Link" } } },
            },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await ReadDbAsync(db => db.Set<FeedbackMedia>().CountAsync())).Should().Be(1);
        (await ReadDbAsync(db => db.Set<ImprovementPointMedia>().CountAsync())).Should().Be(1);
    }

    [Test]
    public async Task Update_WithANewDataLink_Returns400AndChangesNothing()
    {
        // Arrange
        var (feedbackId, _) = await SeedFeedbackWithPointAsync();
        SetAuth(_coachId);

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/feedback/{feedbackId}", new
        {
            content = "<p>Rewritten</p>",
            attachments = new[] { new { url = "data:text/html,x", type = "Document" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldErrorsOf(response)).Should().Equal(("attachments[0].url", "INVALID_URL"));
        (await ReadDbAsync(db => db.Set<Feedback>().SingleAsync())).Content.Should().Be("<p>Keep the platform still</p>");
        (await ReadDbAsync(db => db.Set<FeedbackMedia>().CountAsync())).Should().Be(0);
    }

    [Test]
    public async Task Update_KeepingAStoredLinkById_DoesNotJudgeTheUrlItNeverStores()
    {
        // Arrange — a row written before the rule. A kept attachment takes nothing but its title,
        // type and place from the edit, so the url the entry echoes is not what would be stored;
        // refusing it would block every edit of that feedback until the coach found the link.
        var legacy = new FeedbackMedia { Url = "javascript:legacy()", Type = FeedbackMediaType.Video, Title = "Old", Order = 0 };
        var (feedbackId, _) = await SeedFeedbackWithPointAsync(legacy);
        SetAuth(_coachId);

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/feedback/{feedbackId}", new
        {
            content = "<p>Rewritten</p>",
            attachments = new[] { new { id = legacy.Id, url = legacy.Url, type = "Video", title = "Old" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadDbAsync(db => db.Set<Feedback>().SingleAsync())).Content.Should().Be("<p>Rewritten</p>");
    }

    [Test]
    public async Task AddImprovementPoint_WithAJavascriptLink_Returns400AndAddsNoPoint()
    {
        // Arrange
        var (feedbackId, _) = await SeedFeedbackWithPointAsync();
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync($"/v1/feedback/{feedbackId}/improvement-points", new
        {
            description = "Approach",
            mediaLinks = new[] { new { url = Hostile, type = "Video", source = "Link" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldErrorsOf(response)).Should().Equal(("mediaLinks[0].url", "INVALID_URL"));
        (await ReadDbAsync(db => db.Set<ImprovementPoint>().CountAsync())).Should().Be(1);
    }

    [Test]
    public async Task AddMediaToPoint_WithAJavascriptUrl_Returns400AndAddsNothing()
    {
        // Arrange
        var (feedbackId, pointId) = await SeedFeedbackWithPointAsync();
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/feedback/{feedbackId}/improvement-points/{pointId}/media",
            new { url = Hostile, type = "Video", source = "Link" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldErrorsOf(response)).Should().Equal(("url", "INVALID_URL"));
        (await ReadDbAsync(db => db.Set<ImprovementPointMedia>().CountAsync())).Should().Be(0);
    }

    [Test]
    public async Task AddMediaToPoint_WithAnHttpsLink_StoresIt()
    {
        // Arrange
        var (feedbackId, pointId) = await SeedFeedbackWithPointAsync();
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/feedback/{feedbackId}/improvement-points/{pointId}/media",
            new { url = "https://vimeo.com/drill-demo", type = "Video", source = "Link" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadDbAsync(db => db.Set<ImprovementPointMedia>().SingleAsync())).Url.Should().Be("https://vimeo.com/drill-demo");
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
