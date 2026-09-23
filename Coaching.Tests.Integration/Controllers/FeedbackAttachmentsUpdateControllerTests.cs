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
/// Editing the attachments a feedback carries as a whole, beside its improvement points (SPI-5714).
/// The edit lists the attachments as they should stand: one it keeps is named by id, a new one
/// arrives whole, and whatever it leaves out goes.
///
/// A read hands out presigned URLs for files in our bucket, so an editor echoing one back must not
/// get it stored — it expires, and the player's file with it. S3 alone is substituted, so the real
/// signer presigns the way it does in production, without credentials. Everything goes through
/// that one derived host: touching the base factory's services would start a second one.
/// </summary>
[TestFixture]
[Category("Integration")]
public class FeedbackAttachmentsUpdateControllerTests
{
    /// <summary>CoachingApiFactory's S3:PublicBaseUrl.</summary>
    private const string PublicBaseUrl = "https://cdn.test";
    private const string LinkUrl = "https://youtu.be/serve-reference";

    private CoachingApiFactory _factory = null!;
    private WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;

    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _playerId = Guid.NewGuid();

    private sealed record Seeded(Guid FeedbackId, Guid FileId, string FileUrl, Guid LinkId);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();

        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns(call => $"{PublicBaseUrl}/{call.Arg<GetPreSignedUrlRequest>().Key}?X-Amz-Signature=read-only");
        _host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(s3)));
        _client = _host.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _host.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
    }

    /// <summary>A feedback the coach gave, carrying an uploaded file and then a pasted link.</summary>
    private async Task<Seeded> SeedFeedbackWithFileAndLinkAsync()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        var fileUrl = $"{PublicBaseUrl}/feedback/{_coachId}/{Guid.NewGuid()}.jpg";
        var feedback = new Feedback
        {
            RecipientUserId = _playerId,
            CoachUserId = _coachId,
            SharedWithPlayer = false,
            Content = "<p>Keep the platform still</p>",
        };
        var file = new FeedbackMedia { FeedbackId = feedback.Id, Url = fileUrl, Type = FeedbackMediaType.Image, Title = "Serve", Order = 0 };
        var link = new FeedbackMedia { FeedbackId = feedback.Id, Url = LinkUrl, Type = FeedbackMediaType.Video, Title = "Reference", Order = 1 };

        db.Set<Feedback>().Add(feedback);
        db.Set<FeedbackMedia>().AddRange(file, link);
        await db.SaveChangesAsync();

        return new Seeded(feedback.Id, file.Id, fileUrl, link.Id);
    }

    private async Task<List<FeedbackMedia>> StoredMediaAsync(Guid feedbackId)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        return await db.Set<FeedbackMedia>().AsNoTracking()
            .Where(m => m.FeedbackId == feedbackId)
            .OrderBy(m => m.Order)
            .ToListAsync();
    }

    private async Task<List<JsonElement>> ReadAttachmentsAsync(Guid feedbackId)
    {
        var response = await _client.GetAsync($"/v1/feedback/{feedbackId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("attachments").EnumerateArray().Select(a => a.Clone()).ToList();
    }

    private Task<HttpResponseMessage> PutAsync(Guid feedbackId, object body) =>
        _client.PutAsJsonAsync($"/v1/feedback/{feedbackId}", body);

    [Test]
    public async Task Update_KeepingAFileByIdWithThePresignedUrlItRead_StoresTheOriginalUrl()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_coachId);
        var readUrl = (await ReadAttachmentsAsync(seeded.FeedbackId))
            .Single(a => a.GetProperty("id").GetGuid() == seeded.FileId)
            .GetProperty("url").GetString();
        readUrl.Should().Contain("X-Amz-Signature", "the read presigns files in our bucket");

        // Act
        var response = await PutAsync(seeded.FeedbackId, new
        {
            attachments = new object[] { new { id = seeded.FileId, url = readUrl, type = "Image", title = "Serve" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var file = (await StoredMediaAsync(seeded.FeedbackId)).Single(m => m.Id == seeded.FileId);
        file.Url.Should().Be(seeded.FileUrl);
        file.IsDeleted.Should().BeFalse();
    }

    [Test]
    public async Task Update_LeavingAnAttachmentOut_RemovesIt()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(seeded.FeedbackId, new
        {
            attachments = new object[] { new { id = seeded.FileId, url = seeded.FileUrl, type = "Image", title = "Serve" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredMediaAsync(seeded.FeedbackId)).Single(m => m.Id == seeded.LinkId).IsDeleted.Should().BeTrue();
        (await ReadAttachmentsAsync(seeded.FeedbackId)).Select(a => a.GetProperty("id").GetGuid())
            .Should().Equal(seeded.FileId);
    }

    [Test]
    public async Task Update_WithANewAttachment_AddsItWhereTheListPutsIt()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(seeded.FeedbackId, new
        {
            attachments = new object[]
            {
                new { url = "https://vimeo.com/drill-demo", type = "Video", title = "Drill demo" },
                new { id = seeded.FileId, url = seeded.FileUrl, type = "Image", title = "Serve" },
                new { id = seeded.LinkId, url = LinkUrl, type = "Video", title = "Reference" },
            },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var attachments = await ReadAttachmentsAsync(seeded.FeedbackId);
        attachments.Select(a => a.GetProperty("url").GetString()).First().Should().Be("https://vimeo.com/drill-demo");
        attachments.Skip(1).Select(a => a.GetProperty("id").GetGuid()).Should().Equal(seeded.FileId, seeded.LinkId);
        attachments.Select(a => a.GetProperty("order").GetInt32()).Should().Equal(0, 1, 2);
    }

    [Test]
    public async Task Update_RetitlingAKeptLink_KeepsItsIdAndStoresTheTitle()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(seeded.FeedbackId, new
        {
            attachments = new object[]
            {
                new { id = seeded.FileId, url = seeded.FileUrl, type = "Image", title = "Serve" },
                new { id = seeded.LinkId, url = LinkUrl, type = "Video", title = "Watch the toss" },
            },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var link = (await StoredMediaAsync(seeded.FeedbackId)).Single(m => m.Id == seeded.LinkId);
        link.Title.Should().Be("Watch the toss");
        link.IsDeleted.Should().BeFalse();
    }

    [Test]
    public async Task Update_WithoutAttachments_LeavesThemAsTheyWere()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(seeded.FeedbackId, new { content = "<p>Keep the platform still, then serve</p>" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredMediaAsync(seeded.FeedbackId)).Should().OnlyContain(m => !m.IsDeleted).And.HaveCount(2);
    }

    [Test]
    public async Task Update_WithAnEmptyList_RemovesEveryAttachment()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(seeded.FeedbackId, new { attachments = Array.Empty<object>() });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAttachmentsAsync(seeded.FeedbackId)).Should().BeEmpty();
    }

    [Test]
    public async Task Update_NamingAnAttachmentThisFeedbackDoesNotHave_Returns404AndChangesNothing()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        var other = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(seeded.FeedbackId, new
        {
            content = "<p>Rewritten</p>",
            attachments = new object[] { new { id = other.FileId, url = other.FileUrl, type = "Image" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredMediaAsync(seeded.FeedbackId)).Should().OnlyContain(m => !m.IsDeleted).And.HaveCount(2);
        (await StoredMediaAsync(other.FeedbackId)).Should().OnlyContain(m => !m.IsDeleted).And.HaveCount(2);
    }

    [Test]
    public async Task Get_AttachmentsSayWhetherTheyAreUploadedFilesOrLinks()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_coachId);

        // Act
        var attachments = await ReadAttachmentsAsync(seeded.FeedbackId);

        // Assert
        attachments.Select(a => a.GetProperty("source").GetString()).Should().Equal("File", "Link");
    }

    [Test]
    public async Task Update_BySomeoneOtherThanTheCoach_Returns403()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();
        SetAuth(_playerId);

        // Act
        var response = await PutAsync(seeded.FeedbackId, new { attachments = Array.Empty<object>() });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredMediaAsync(seeded.FeedbackId)).Should().OnlyContain(m => !m.IsDeleted);
    }

    [Test]
    public async Task Update_OfAFeedbackThatDoesNotExist_Returns404()
    {
        // Arrange
        SetAuth(_coachId);

        // Act
        var response = await PutAsync(Guid.NewGuid(), new { attachments = Array.Empty<object>() });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Update_Anonymously_Returns401()
    {
        // Arrange
        var seeded = await SeedFeedbackWithFileAndLinkAsync();

        // Act
        var response = await PutAsync(seeded.FeedbackId, new { attachments = Array.Empty<object>() });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
