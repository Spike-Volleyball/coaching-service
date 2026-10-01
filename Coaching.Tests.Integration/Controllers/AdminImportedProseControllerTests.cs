using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coaching.Application.DTOs.Drills;
using Coaching.Application.RichText;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Infrastructure.Data.Context;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shared.Microservices.Authorization;
using Shared.Models;

namespace Coaching.Tests.Integration.Controllers;

/// <summary>
/// SPI-6502: the admin console rebuilds a coach's drills that an import flattened, reporting
/// before it writes, and leaving alone every drill its author has reworked since.
/// </summary>
[TestFixture]
[Category("Integration")]
public class AdminImportedProseControllerTests
{
    private const string ConsoleKey = "imported-prose-tests-admin-console-key";
    private const string RebuildUrl = "/v1/admin/drills/imported-prose/rebuild";

    private static readonly Guid ImporterId = Guid.NewGuid();
    private static readonly string[] FlatSteps = ["Split into 2 teams", "Variations:", "- Line", "- Sharp cross"];
    private const string StructuredSteps =
        "<ol><li><p>Split into 2 teams</p></li>" +
        "<li><p>Variations:</p><ul><li><p>Line</p></li><li><p>Sharp cross</p></li></ul></li></ol>";
    private const string ReworkedSteps = "<p>Two blockers hold the sheet.</p><ul><li><p>hold it higher</p></li></ul>";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private CoachingApiFactory _factory = null!;
    private WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AdminConsole:ApiKey"] = ConsoleKey })));
        _client = _host.CreateClient();
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

    [Test]
    public async Task Rebuild_WithTheConsoleKey_ReportsTheFlattenedDrillAndWritesNothing()
    {
        // Arrange
        var (flattened, reworked) = await SeedAsync();

        // Act
        var response = await _client.SendAsync(Rebuild(ConsoleKey, new { userId = ImporterId }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = (await response.Content.ReadFromJsonAsync<ImportedProseRebuildDto>(JsonOptions))!;
        result.Applied.Should().BeFalse();
        var reported = result.Drills.Should().ContainSingle().Subject;
        reported.DrillId.Should().Be(flattened);
        reported.Instructions.Should().Be(new ProseChangeDto(DrillRichText.FromLines(FlatSteps, ordered: true), StructuredSteps));

        (await FindAsync(flattened)).InstructionsHtml.Should().Be(DrillRichText.FromLines(FlatSteps, ordered: true));
        (await FindAsync(reworked)).InstructionsHtml.Should().Be(ReworkedSteps);
    }

    [Test]
    public async Task Rebuild_WhenApplied_RewritesTheFlattenedDrillAndNotTheReworkedOne()
    {
        // Arrange
        var (flattened, reworked) = await SeedAsync();

        // Act
        var response = await _client.SendAsync(Rebuild(ConsoleKey, new { userId = ImporterId, apply = true }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<ImportedProseRebuildDto>(JsonOptions))!.Applied.Should().BeTrue();

        var rebuilt = await FindAsync(flattened);
        rebuilt.InstructionsHtml.Should().Be(StructuredSteps);
        rebuilt.Instructions.Should().Equal("Split into 2 teams", "Variations:", "Line", "Sharp cross");
        (await FindAsync(reworked)).InstructionsHtml.Should().Be(ReworkedSteps);
    }

    [Test]
    public async Task Rebuild_WithoutNamingACoach_IsBadRequestAndWritesNothing()
    {
        // Arrange
        var (flattened, _) = await SeedAsync();

        // Act
        var response = await _client.SendAsync(Rebuild(ConsoleKey, new { apply = true }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FindAsync(flattened)).Instructions.Should().Equal(FlatSteps);
    }

    [Test]
    public async Task Rebuild_BySignedInUserWithoutTheConsoleKey_IsUnauthorized()
    {
        // Arrange — the importer's own token is no admin console.
        var (flattened, _) = await SeedAsync();
        SetAuth(ImporterId);

        // Act
        var response = await _client.PostAsJsonAsync(RebuildUrl, new { userId = ImporterId, apply = true });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await FindAsync(flattened)).Instructions.Should().Equal(FlatSteps);
    }

    [Test]
    public async Task Rebuild_WithTheWrongKey_IsUnauthorized()
    {
        // Arrange
        var (flattened, _) = await SeedAsync();

        // Act
        var response = await _client.SendAsync(Rebuild("not-the-console-key", new { userId = ImporterId, apply = true }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await FindAsync(flattened)).Instructions.Should().Equal(FlatSteps);
    }

    /// <summary>One drill as the flat import stored it; one its author rebuilt in the editor.</summary>
    private async Task<(Guid Flattened, Guid Reworked)> SeedAsync()
    {
        var flattened = new Drill
        {
            Name = "Dot Shots",
            CreatedByUserId = ImporterId,
            Visibility = DrillVisibility.Private,
            Instructions = FlatSteps,
            InstructionsHtml = DrillRichText.FromLines(FlatSteps, ordered: true),
            CoachingPoints = []
        };
        var reworked = new Drill
        {
            Name = "Sheet Defense",
            CreatedByUserId = ImporterId,
            Visibility = DrillVisibility.Private,
            Instructions = ["Two blockers hold the sheet.", "hold it higher"],
            InstructionsHtml = ReworkedSteps,
            CoachingPoints = []
        };

        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoachingDbContext>();
        db.AddRange(
            new UserProfile { Id = ImporterId, Name = "Club", Surname = "Director", Email = "director@test.local", IsActive = true },
            flattened,
            reworked);
        await db.SaveChangesAsync();
        return (flattened.Id, reworked.Id);
    }

    private async Task<Drill> FindAsync(Guid id)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CoachingDbContext>()
            .Drills.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private static HttpRequestMessage Rebuild(string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RebuildUrl) { Content = JsonContent.Create(body) };
        request.Headers.Add(AdminConsoleKey.HeaderName, key);
        return request;
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
                new Claim(ClaimTypes.Email, "director@test.local"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }
}
