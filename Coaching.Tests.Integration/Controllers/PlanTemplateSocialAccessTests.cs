using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coaching.Application.DTOs.Templates;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shared.Models;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// SPI-6801: a private template's likes, bookmarks and comments were open to anyone holding its id,
/// and the liked and bookmarked lists kept showing a template after its author made it private.
/// Whoever may read a template may like, bookmark and discuss it; to anyone else it is not there.
/// </summary>
[TestFixture]
[Category("Integration")]
public class PlanTemplateSocialAccessTests
{
    private static readonly Guid AuthorId = Guid.NewGuid();
    private static readonly Guid ReaderId = Guid.NewGuid();

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
        await _factory.DatabaseResetter.ResetAsync();
    }

    [Test]
    public async Task Social_OnSomeoneElsesPrivateTemplate_IsAnsweredAsIfThePlanDidNotExist()
    {
        // Arrange
        var planId = await SeedTemplateAsync(TemplateVisibility.Private);
        SetAuth(ReaderId);

        // Act
        var answers = new[]
        {
            await _client.PostAsync($"/v1/plans/{planId}/like", null),
            await _client.DeleteAsync($"/v1/plans/{planId}/like"),
            await _client.GetAsync($"/v1/plans/{planId}/like"),
            await _client.PostAsync($"/v1/plans/{planId}/bookmark", null),
            await _client.DeleteAsync($"/v1/plans/{planId}/bookmark"),
            await _client.PostAsJsonAsync($"/v1/plans/{planId}/comments", new CreatePlanCommentDto("Nice plan")),
            await _client.GetAsync($"/v1/plans/{planId}/comments"),
        };

        // Assert
        answers.Select(answer => answer.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
        var (likeCount, likes, bookmarks, comments) = await StoredSocialAsync(planId);
        likeCount.Should().Be(0);
        likes.Should().Be(0);
        bookmarks.Should().Be(0);
        comments.Should().Be(0);
    }

    [Test]
    public async Task Social_OnAPrivateTemplate_IsOpenToItsAuthor()
    {
        // Arrange
        var planId = await SeedTemplateAsync(TemplateVisibility.Private);
        SetAuth(AuthorId);

        // Act
        var liked = await _client.PostAsync($"/v1/plans/{planId}/like", null);
        var bookmarked = await _client.PostAsync($"/v1/plans/{planId}/bookmark", null);
        var commented = await _client.PostAsJsonAsync($"/v1/plans/{planId}/comments", new CreatePlanCommentDto("Swap the drills"));

        // Assert
        liked.StatusCode.Should().Be(HttpStatusCode.OK);
        bookmarked.StatusCode.Should().Be(HttpStatusCode.OK);
        commented.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task Social_OnAPublicTemplate_IsOpenToAnyone()
    {
        // Arrange
        var planId = await SeedTemplateAsync(TemplateVisibility.Public);
        SetAuth(ReaderId);

        // Act
        var liked = await _client.PostAsync($"/v1/plans/{planId}/like", null);
        var bookmarked = await _client.PostAsync($"/v1/plans/{planId}/bookmark", null);
        var commented = await _client.PostAsJsonAsync($"/v1/plans/{planId}/comments", new CreatePlanCommentDto("Using this on Thursday"));

        // Assert
        liked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await liked.Content.ReadFromJsonAsync<PlanLikeStatusDto>(JsonOptions))!.LikeCount.Should().Be(1);
        bookmarked.StatusCode.Should().Be(HttpStatusCode.OK);
        commented.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task LikedAndBookmarkedLists_LeaveOutATemplateItsAuthorMadePrivate()
    {
        // Arrange — liked and bookmarked while public, then taken private.
        var planId = await SeedTemplateAsync(TemplateVisibility.Public);
        SetAuth(ReaderId);
        (await _client.PostAsync($"/v1/plans/{planId}/like", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/v1/plans/{planId}/bookmark", null)).EnsureSuccessStatusCode();
        await MakePrivateAsync(planId);

        // Act
        var bookmarks = await ListAsync("/v1/me/plans/bookmarks");
        var likes = await ListAsync("/v1/me/plans/likes");

        // Assert
        bookmarks.Items.Should().BeEmpty();
        bookmarks.TotalCount.Should().Be(0);
        likes.Items.Should().BeEmpty();
        likes.TotalCount.Should().Be(0);
    }

    [Test]
    public async Task LikedAndBookmarkedLists_KeepTheAuthorsOwnPrivateTemplate()
    {
        // Arrange
        var planId = await SeedTemplateAsync(TemplateVisibility.Private);
        SetAuth(AuthorId);
        (await _client.PostAsync($"/v1/plans/{planId}/like", null)).EnsureSuccessStatusCode();
        (await _client.PostAsync($"/v1/plans/{planId}/bookmark", null)).EnsureSuccessStatusCode();

        // Act
        var bookmarks = await ListAsync("/v1/me/plans/bookmarks");
        var likes = await ListAsync("/v1/me/plans/likes");

        // Assert
        bookmarks.Items.Should().ContainSingle().Which.Id.Should().Be(planId);
        bookmarks.TotalCount.Should().Be(1);
        likes.Items.Should().ContainSingle().Which.Id.Should().Be(planId);
        likes.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task Social_OnAnEventPlan_IsForTemplatesOnly()
    {
        // Arrange — an event's plan is liked and bookmarked nowhere: those are a library's verbs.
        var planId = await SeedTemplateAsync(TemplateVisibility.Public, PlanType.Instance);
        SetAuth(AuthorId);

        // Act
        var liked = await _client.PostAsync($"/v1/plans/{planId}/like", null);
        var bookmarked = await _client.PostAsync($"/v1/plans/{planId}/bookmark", null);

        // Assert
        liked.StatusCode.Should().Be(HttpStatusCode.NotFound);
        bookmarked.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<PlanListResponseDto> ListAsync(string path)
    {
        var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PlanListResponseDto>(JsonOptions))!;
    }

    private async Task<Guid> SeedTemplateAsync(TemplateVisibility visibility, PlanType planType = PlanType.Template)
    {
        var planId = Guid.NewGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(
            Profile(AuthorId, "Author"),
            Profile(ReaderId, "Reader"),
            new TrainingPlan
            {
                Id = planId,
                Name = "Serve receive ladder",
                CreatedByUserId = AuthorId,
                PlanType = planType,
                EventId = planType == PlanType.Instance ? Guid.NewGuid() : null,
                Visibility = visibility
            });
        await db.SaveChangesAsync();

        return planId;
    }

    private async Task MakePrivateAsync(Guid planId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        await db.TrainingPlans
            .Where(p => p.Id == planId)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Visibility, TemplateVisibility.Private));
    }

    private async Task<(int LikeCount, int Likes, int Bookmarks, int Comments)> StoredSocialAsync(Guid planId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        var likeCount = await db.TrainingPlans.Where(p => p.Id == planId).Select(p => p.LikeCount).SingleAsync();
        return (
            likeCount,
            await db.Set<PlanLike>().CountAsync(l => l.TemplateId == planId),
            await db.Set<PlanBookmark>().CountAsync(b => b.TemplateId == planId),
            await db.Set<PlanComment>().CountAsync(c => c.TemplateId == planId));
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
