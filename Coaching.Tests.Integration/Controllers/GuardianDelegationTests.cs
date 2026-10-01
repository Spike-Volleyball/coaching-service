using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
using Shared.DTOs.Errors;
using Shared.Models;
using Shared.Services;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// The subject contract at the edge: the one marked read is evaluated for the subject the header
/// names, and every unmarked route refuses the header outright instead of answering for the actor.
/// </summary>
[TestFixture]
[Category("Integration")]
public class GuardianDelegationTests
{
    private CoachingApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid GuardianId = Guid.NewGuid();
    private static readonly Guid WardId = Guid.NewGuid();

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
        await _factory.DisposeAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        await _factory.DatabaseResetter.ResetAsync();
        _factory.EventsGrpcClient.ClearSubstitute();
        _factory.GuardianCacheService.ClearSubstitute();
    }

    [Test]
    public async Task GetMyPlans_WithActingAsHeader_Returns400ActingAsValidationFailed()
    {
        // Arrange — reject-by-default: a route nobody marked must refuse the header.
        SetAuth(GuardianId);

        // Act
        var response = await SendActingAs("/v1/me/plans", WardId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(JsonOptions);
        problem!.Code.Should().Be("ACTING_AS_VALIDATION_FAILED");
    }

    [Test]
    public async Task GetEventPlan_AsGuardianForTheirWard_IsEvaluatedForTheWard()
    {
        // Arrange — only the ward takes part in the event; the guardian is nobody to it.
        var (eventId, planId) = await SeedEventPlanAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, WardId).Returns((true, true));
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, GuardianId).Returns((false, true));
        LinkGuardian(GuardianId, WardId);
        SetAuth(GuardianId);

        // Act
        var response = await SendActingAs($"/v1/events/{eventId}/plans", WardId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await response.Content.ReadFromJsonAsync<TrainingPlanDetailDto>(JsonOptions);
        plan!.Id.Should().Be(planId);
        await _factory.EventsGrpcClient.Received(1).IsEventParticipantAsync(eventId, WardId);
        await _factory.EventsGrpcClient.DidNotReceive().IsEventParticipantAsync(eventId, GuardianId);
    }

    [Test]
    public async Task GetEventPlan_HeaderForANonWard_Returns403BeforeTheReadRuns()
    {
        // Arrange
        var (eventId, _) = await SeedEventPlanAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, Arg.Any<Guid>()).Returns((true, true));
        DenyGuardian(GuardianId, WardId);
        SetAuth(GuardianId);

        // Act
        var response = await SendActingAs($"/v1/events/{eventId}/plans", WardId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.EventsGrpcClient.DidNotReceive().IsEventParticipantAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task GetEventPlan_WithoutAnyHeader_IsEvaluatedForTheCaller()
    {
        // Arrange — the no-header branch must keep every self-serve caller working.
        var (eventId, planId) = await SeedEventPlanAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, WardId).Returns((true, true));
        SetAuth(WardId);

        // Act
        var response = await _client.GetAsync($"/v1/events/{eventId}/plans");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await response.Content.ReadFromJsonAsync<TrainingPlanDetailDto>(JsonOptions);
        plan!.Id.Should().Be(planId);
        await _factory.GuardianCacheService.DidNotReceive().HasAccessWithCacheAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task GetEventPlan_NamingYourselfInTheHeader_IsEvaluatedForTheCallerWithoutAGuardianLookup()
    {
        // Arrange — installed app builds send the caller's own id (SPI-6662): it asks for nothing
        // the request without the header does not, and is answered as one.
        var (eventId, planId) = await SeedEventPlanAsync();
        _factory.EventsGrpcClient.IsEventParticipantAsync(eventId, WardId).Returns((true, true));
        SetAuth(WardId);

        // Act
        var response = await SendActingAs($"/v1/events/{eventId}/plans", WardId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await response.Content.ReadFromJsonAsync<TrainingPlanDetailDto>(JsonOptions);
        plan!.Id.Should().Be(planId);
        await _factory.GuardianCacheService.DidNotReceive().HasAccessWithCacheAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    // ---------- Helpers ----------

    private Task<HttpResponseMessage> SendActingAs(string url, Guid subjectUserId)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Add("X-Acting-As", subjectUserId.ToString());
        return _client.SendAsync(message);
    }

    private void LinkGuardian(Guid guardianId, Guid wardId)
    {
        _factory.GuardianCacheService.HasAccessWithCacheAsync(guardianId, wardId).Returns((true, "ProfilesGrpc"));
        _factory.GuardianCacheService.HasAccessFromDbAsync(guardianId, wardId).Returns((true, "ProfilesGrpc"));
        _factory.GuardianCacheService.GetRemovalNoticeStatusAsync(guardianId, wardId)
            .Returns((GuardianRemovalStatus?)null);
    }

    private void DenyGuardian(Guid guardianId, Guid wardId)
    {
        _factory.GuardianCacheService.HasAccessWithCacheAsync(guardianId, wardId).Returns((false, "None"));
        _factory.GuardianCacheService.HasAccessFromDbAsync(guardianId, wardId).Returns((false, "None"));
        _factory.GuardianCacheService.GetRemovalNoticeStatusAsync(guardianId, wardId)
            .Returns((GuardianRemovalStatus?)null);
    }

    private async Task<(Guid eventId, Guid planId)> SeedEventPlanAsync()
    {
        var eventId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();

        db.Set<UserProfile>().Add(new UserProfile
        {
            Id = CoachId,
            Name = "Coach",
            Surname = "Creator",
            Email = "coach.creator@test.com"
        });
        db.TrainingPlans.Add(new TrainingPlan
        {
            Id = planId,
            Name = "Instance Plan",
            CreatedByUserId = CoachId,
            PlanType = PlanType.Instance,
            EventId = eventId,
            Visibility = TemplateVisibility.Private
        });

        await db.SaveChangesAsync();
        return (eventId, planId);
    }

    private void SetAuth(Guid userId)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(userId));
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
