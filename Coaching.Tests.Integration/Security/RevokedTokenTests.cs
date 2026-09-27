using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Coaching.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shared.DTOs.Errors;
using Shared.Enums;
using Shared.Models.Jwt;
using Shared.Services;

namespace Coaching.Tests.Integration.Security;

/// <summary>
/// A token whose account's sessions have ended since it was minted: auth has recorded a newer
/// session version than the one it carries. Where signing in is required it is refused with the
/// 401 a client acts on by signing out, not a 500 the client takes for a passing fault.
/// </summary>
[TestFixture]
[Category("Integration")]
public class RevokedTokenTests
{
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
    public async Task OneTimeTearDown() => await _factory.DisposeAsync();

    [Test]
    public async Task AStaleTokenWhereSigningInIsRequired_Is401TokenInvalid()
    {
        // Arrange
        var userId = await UserWhoseSessionsEndedAsync();

        // Act
        var response = await GetAsync($"/v1/drills/{Guid.NewGuid()}", userId);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>())!.Code
            .Should().Be(ErrorCodeEnum.TokenInvalid.ToStringCode());
    }

    [Test]
    public async Task AStaleTokenWhereSigningInIsOptional_IsAnsweredAsNoTokenIs()
    {
        // Arrange
        var userId = await UserWhoseSessionsEndedAsync();

        // Act
        var withStaleToken = await GetAsync("/health", userId);
        var withNoToken = await _client.GetAsync("/health");

        // Assert
        withStaleToken.StatusCode.Should().Be(withNoToken.StatusCode);
    }

    /// <summary>
    /// Records a version past the first for a new account, as auth does when its password is
    /// replaced; the tokens below carry no version, so they count as the first.
    /// </summary>
    private async Task<Guid> UserWhoseSessionsEndedAsync()
    {
        var userId = Guid.NewGuid();
        await _factory.Services.GetRequiredService<RevocationCache>().Cache
            .SetStringAsync(SessionVersions.CacheKey(userId.ToString()), (SessionVersions.Initial + 1).ToString());
        return userId;
    }

    private async Task<HttpResponseMessage> GetAsync(string path, Guid userId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(userId));
        return await _client.SendAsync(request);
    }

    private static string GenerateJwt(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(CoachingApiFactory.JwtSecret));
        var token = new JwtSecurityToken(
            issuer: CoachingApiFactory.JwtIssuer,
            audience: CoachingApiFactory.JwtAudience,
            claims: [new Claim(JwtRegisteredClaimNames.NameId, userId.ToString())],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
