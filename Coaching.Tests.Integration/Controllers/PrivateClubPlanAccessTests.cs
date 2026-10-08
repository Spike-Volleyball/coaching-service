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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Shared.Models;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// SPI-6900: a private template saved to a club showed in no list on the web, not even its
/// author's, because the club's list left private templates out. Private on a club's template
/// means it stays inside the club: its members read it, as they read the club's private drills.
/// </summary>
[TestFixture]
[Category("Integration")]
public class PrivateClubPlanAccessTests
{
    private static readonly Guid AuthorId = Guid.NewGuid();
    private static readonly Guid ClubMateId = Guid.NewGuid();
    private static readonly Guid OutsiderId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();
    private static readonly Guid OtherClubId = Guid.NewGuid();

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

    [SetUp]
    public async Task SetUp()
    {
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(AuthorId, ClubId).Returns(true);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(ClubMateId, ClubId).Returns(true);
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(ClubMateId, OtherClubId).Returns(true);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(Profile(AuthorId, "Author"), Profile(ClubMateId, "Mate"), Profile(OutsiderId, "Outsider"));
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _factory.ClubsGrpcClient.ClearSubstitute();
        await _factory.DatabaseResetter.ResetAsync();
    }

    [Test]
    public async Task ClubList_HoldsAPrivateClubPlan_ForItsAuthor()
    {
        // Arrange
        var planId = await SeedPlanAsync(ClubId);
        SetAuth(AuthorId);

        // Act
        var plans = await ListAsync($"/v1/clubs/{ClubId}/plans");

        // Assert
        plans.Items.Should().ContainSingle().Which.Id.Should().Be(planId);
        plans.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task ClubList_HoldsEachClubsOwnPrivatePlans_ForAMemberOfBoth()
    {
        // Arrange
        var inClub = await SeedPlanAsync(ClubId);
        var inOtherClub = await SeedPlanAsync(OtherClubId);
        await SeedPlanAsync(clubId: null);
        SetAuth(ClubMateId);

        // Act
        var club = await ListAsync($"/v1/clubs/{ClubId}/plans");
        var otherClub = await ListAsync($"/v1/clubs/{OtherClubId}/plans");

        // Assert
        club.Items.Should().ContainSingle().Which.Id.Should().Be(inClub);
        otherClub.Items.Should().ContainSingle().Which.Id.Should().Be(inOtherClub);
    }

    [Test]
    public async Task Opening_APrivateClubPlan_IsAllowedInsideTheClub_AndRefusedOutsideIt()
    {
        // Arrange
        var planId = await SeedPlanAsync(ClubId);

        // Act
        SetAuth(ClubMateId);
        var forClubMate = await _client.GetAsync($"/v1/plans/{planId}");
        SetAuth(OutsiderId);
        var forOutsider = await _client.GetAsync($"/v1/plans/{planId}");

        // Assert
        forClubMate.StatusCode.Should().Be(HttpStatusCode.OK);
        forOutsider.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Opening_APrivatePlanWithNoClub_StaysItsAuthorsOnly()
    {
        // Arrange
        var planId = await SeedPlanAsync(clubId: null);
        SetAuth(ClubMateId);

        // Act
        var opened = await _client.GetAsync($"/v1/plans/{planId}");

        // Assert
        opened.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Opening_AnEventsPlan_IsNotDecidedByClubMembership()
    {
        // Arrange — an event's plan is read through its event, whatever club it names.
        var planId = await SeedPlanAsync(ClubId, PlanType.Instance);
        SetAuth(ClubMateId);

        // Act
        var opened = await _client.GetAsync($"/v1/plans/{planId}");

        // Assert
        opened.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Social_OnAPrivateClubPlan_IsOpenToAClubMate()
    {
        // Arrange
        var planId = await SeedPlanAsync(ClubId);
        SetAuth(ClubMateId);

        // Act
        var liked = await _client.PostAsync($"/v1/plans/{planId}/like", null);
        var bookmarked = await _client.PostAsync($"/v1/plans/{planId}/bookmark", null);
        var comments = await _client.GetAsync($"/v1/plans/{planId}/comments");

        // Assert
        liked.StatusCode.Should().Be(HttpStatusCode.OK);
        bookmarked.StatusCode.Should().Be(HttpStatusCode.OK);
        comments.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Social_OnAPrivateClubPlan_IsAnsweredOutsideTheClubAsIfThePlanDidNotExist()
    {
        // Arrange
        var planId = await SeedPlanAsync(ClubId);
        SetAuth(OutsiderId);

        // Act
        var answers = new[]
        {
            await _client.PostAsync($"/v1/plans/{planId}/like", null),
            await _client.GetAsync($"/v1/plans/{planId}/like"),
            await _client.PostAsync($"/v1/plans/{planId}/bookmark", null),
            await _client.GetAsync($"/v1/plans/{planId}/comments"),
        };

        // Assert
        answers.Select(answer => answer.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task SavedAndLikedLists_KeepAClubMatesPrivatePlan_UntilTheReaderLeavesTheClub()
    {
        // Arrange — one saved and liked in each of two clubs; the reader then leaves the second.
        var kept = await SeedPlanAsync(ClubId);
        var left = await SeedPlanAsync(OtherClubId);
        SetAuth(ClubMateId);
        foreach (var planId in new[] { kept, left })
        {
            (await _client.PostAsync($"/v1/plans/{planId}/like", null)).EnsureSuccessStatusCode();
            (await _client.PostAsync($"/v1/plans/{planId}/bookmark", null)).EnsureSuccessStatusCode();
        }
        _factory.ClubsGrpcClient.IsUserClubMemberAsync(ClubMateId, OtherClubId).Returns(false);

        // Act
        var bookmarks = await ListAsync("/v1/me/plans/bookmarks");
        var likes = await ListAsync("/v1/me/plans/likes");

        // Assert
        bookmarks.Items.Should().ContainSingle().Which.Id.Should().Be(kept);
        bookmarks.TotalCount.Should().Be(1);
        likes.Items.Should().ContainSingle().Which.Id.Should().Be(kept);
        likes.TotalCount.Should().Be(1);
    }

    private async Task<PlanListResponseDto> ListAsync(string path)
    {
        var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PlanListResponseDto>(JsonOptions))!;
    }

    private async Task<Guid> SeedPlanAsync(Guid? clubId, PlanType planType = PlanType.Template)
    {
        var planId = Guid.NewGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.Add(new TrainingPlan
        {
            Id = planId,
            Name = "Serve receive ladder",
            CreatedByUserId = AuthorId,
            ClubId = clubId,
            PlanType = planType,
            EventId = planType == PlanType.Instance ? Guid.NewGuid() : null,
            Visibility = TemplateVisibility.Private
        });
        await db.SaveChangesAsync();

        return planId;
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
