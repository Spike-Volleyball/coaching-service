using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NUnit.Framework;
using Shared.Security.Output;
using Coaching.Domain.Models.Drills;
using Coaching.Tests.Integration.Fixtures;

namespace Coaching.Tests.Integration.Security;

/// <summary>
/// Both of the service's serializers refuse an entity, so one that escapes a service method
/// fails loudly instead of shipping whatever EF had loaded (SPI-6446, SPI-6437).
/// </summary>
[TestFixture]
[Category("Integration")]
public class NoEntityOnTheWireTests
{
    private CoachingApiFactory _factory = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new CoachingApiFactory();
        await _factory.InitializeAsync();
        _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _factory.DisposeAsync();

    [Test]
    public void TheApisSerializer_RefusesAnEntity()
    {
        // Arrange
        var settings = _factory.Services.GetRequiredService<IOptions<MvcNewtonsoftJsonOptions>>().Value.SerializerSettings;

        // Act
        var act = () => JsonConvert.SerializeObject(new Drill { Name = "Serve receive" }, settings);

        // Assert
        act.Should().Throw<EntityExposureException>();
    }

    [Test]
    public void TheHubsSerializer_RefusesAnEntity()
    {
        // Arrange
        var options = _factory.Services.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;

        // Act
        var act = () => System.Text.Json.JsonSerializer.Serialize(new Drill { Name = "Serve receive" }, options);

        // Assert
        act.Should().Throw<EntityExposureException>();
    }
}
