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
/// Reads presign files in our bucket, so an editor holds URLs that expire a day later. The web
/// editor sent those back when it re-added a point's saved file, and the service stored them as
/// they came, so the player's file broke once the signature lapsed. Every media write now stores a
/// file in our bucket as its bare object URL, whatever query arrived with it. S3 alone is
/// substituted, so the real signer presigns as it does in production.
/// </summary>
[TestFixture]
[Category("Integration")]
public class FeedbackMediaStoredUrlControllerTests
{
    /// <summary>CoachingApiFactory's S3:PublicBaseUrl.</summary>
    private const string PublicBaseUrl = "https://cdn.test";

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

        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns(call => $"{PublicBaseUrl}/{call.Arg<GetPreSignedUrlRequest>().Key}?X-Amz-Expires=86400&X-Amz-Signature=read-once");
        _host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(s3)));
        _client = _host.CreateClient();

        _factory.ClubsGrpcClient.CanGiveFeedbackInClubAsync(_coachId, _clubId).Returns(true);
        _factory.ClubsGrpcClient.GetClubMemberIdsAsync(_clubId).Returns(new HashSet<Guid> { _playerId });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
    }

    private string UploadedFileUrl() => $"{PublicBaseUrl}/feedback/{_coachId}/{Guid.NewGuid()}.jpg";

    private static string Presigned(string bareUrl) => $"{bareUrl}?X-Amz-Expires=86400&X-Amz-Signature=from-an-earlier-read";

    private async Task<(Guid FeedbackId, Guid PointId, string FileUrl)> SeedPointWithFileAsync()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        var fileUrl = UploadedFileUrl();
        var feedback = new Feedback { RecipientUserId = _playerId, CoachUserId = _coachId, ClubId = _clubId };
        var point = new ImprovementPoint { FeedbackId = feedback.Id, Description = "Platform", Order = 1 };
        var file = new ImprovementPointMedia { ImprovementPointId = point.Id, Url = fileUrl, Type = FeedbackMediaType.Image, Source = FeedbackMediaSource.File };

        db.AddRange(feedback, point, file);
        await db.SaveChangesAsync();
        return (feedback.Id, point.Id, fileUrl);
    }

    private async Task<List<string>> StoredUrlsAsync<TMedia>(Func<TMedia, string> url) where TMedia : class
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        return (await db.Set<TMedia>().AsNoTracking().ToListAsync()).Select(url).ToList();
    }

    [Test]
    public async Task AddMediaToPoint_ReAddingASavedFileWithThePresignedUrlItRead_StoresTheBareObjectUrl()
    {
        // Arrange — what the web editor did: read the feedback, then re-add the point's file with
        // the URL that read handed out.
        var (feedbackId, pointId, fileUrl) = await SeedPointWithFileAsync();
        SetAuth(_coachId);
        using var read = JsonDocument.Parse(await _client.GetStringAsync($"/v1/feedback/{feedbackId}"));
        var readUrl = read.RootElement.GetProperty("improvementPoints")[0].GetProperty("mediaLinks")[0].GetProperty("url").GetString()!;
        readUrl.Should().Contain("X-Amz-Signature", "the read presigns files in our bucket");

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/feedback/{feedbackId}/improvement-points/{pointId}/media",
            new { url = readUrl, type = "Image", title = "Serve", source = "File" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredUrlsAsync<ImprovementPointMedia>(m => m.Url)).Should().OnlyContain(url => url == fileUrl);
    }

    [Test]
    public async Task Create_WithPresignedUrls_StoresTheBareObjectUrls()
    {
        // Arrange
        var attachment = UploadedFileUrl();
        var pointFile = UploadedFileUrl();
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync("/v1/feedback", new
        {
            recipientUserId = _playerId,
            clubId = _clubId,
            attachments = new[] { new { url = Presigned(attachment), type = "Image" } },
            improvementPoints = new[]
            {
                new { description = "Footwork", mediaLinks = new[] { new { url = Presigned(pointFile), type = "Image", source = "File" } } },
            },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        (await StoredUrlsAsync<FeedbackMedia>(m => m.Url)).Should().Equal(attachment);
        (await StoredUrlsAsync<ImprovementPointMedia>(m => m.Url)).Should().Equal(pointFile);
    }

    [Test]
    public async Task Update_AddingAnAttachmentWithAPresignedUrl_StoresTheBareObjectUrl()
    {
        // Arrange
        var (feedbackId, _, _) = await SeedPointWithFileAsync();
        var attachment = UploadedFileUrl();
        SetAuth(_coachId);

        // Act
        var response = await _client.PutAsJsonAsync($"/v1/feedback/{feedbackId}", new
        {
            attachments = new[] { new { url = Presigned(attachment), type = "Image" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredUrlsAsync<FeedbackMedia>(m => m.Url)).Should().Equal(attachment);
    }

    [Test]
    public async Task AddImprovementPoint_WithAPresignedFile_StoresTheBareObjectUrl()
    {
        // Arrange
        var (feedbackId, _, fileUrl) = await SeedPointWithFileAsync();
        var pointFile = UploadedFileUrl();
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync($"/v1/feedback/{feedbackId}/improvement-points", new
        {
            description = "Approach",
            mediaLinks = new[] { new { url = Presigned(pointFile), type = "Image", source = "File" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredUrlsAsync<ImprovementPointMedia>(m => m.Url)).Should().BeEquivalentTo([fileUrl, pointFile]);
    }

    [Test]
    public async Task AddMediaToPoint_WithAnExternalLink_StoresItWithItsQuery()
    {
        // Arrange
        var (feedbackId, pointId, fileUrl) = await SeedPointWithFileAsync();
        SetAuth(_coachId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/v1/feedback/{feedbackId}/improvement-points/{pointId}/media",
            new { url = "https://www.youtube.com/watch?v=serve&t=42", type = "Video", source = "Link" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredUrlsAsync<ImprovementPointMedia>(m => m.Url))
            .Should().BeEquivalentTo([fileUrl, "https://www.youtube.com/watch?v=serve&t=42"]);
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
