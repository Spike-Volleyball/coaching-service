using FluentAssertions;
using NUnit.Framework;
using Shared.Testing.Security;
using Coaching.Tests.Integration.Fixtures;

namespace Coaching.Tests.Integration.Security;

/// <summary>
/// What coaching-service opens to signed-out callers, as a reviewed list, under deny-by-default.
/// Opening an endpoint changes public-surface.approved.txt beside this file, so it is seen in
/// review, and it cannot be opened without a reason (SPI-6446, SPI-6437).
/// </summary>
[TestFixture]
[Category("Integration")]
public class PublicSurfaceTests
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
    public void PublicSurface_IsTheReviewedList()
    {
        // Act
        var difference = ApprovedSnapshot.Compare(EndpointSurface.Public(_factory.Services), "public-surface");

        // Assert
        difference.Should().BeNull();
    }

    [Test]
    public void NoEndpoint_IsOpenedWithoutAReason()
    {
        // Act & Assert
        EndpointSurface.BareAnonymous(_factory.Services).Should().BeEmpty();
    }

    [Test]
    public void EveryAccessDeclaration_NamesARouteParameterItsRouteHas()
    {
        // Act & Assert
        EndpointSurface.MisnamedAccess(_factory.Services).Should().BeEmpty();
    }

    /// <summary>
    /// Id routes that declare neither [Access] nor [NoResourceScope] - the routes still checked ad hoc
    /// inside their services. The list may only shrink: a new id route declares its access.
    /// </summary>
    [Test]
    public void IdRoutesWithoutAnAccessDeclaration_AreTheKnownList()
    {
        // Act
        var difference = ApprovedSnapshot.Compare(EndpointSurface.UncheckedIdRoutes(_factory.Services), "unchecked-id-routes");

        // Assert
        difference.Should().BeNull();
    }

    [Test]
    public void TheInternalService_IsServedOnTheInternalListenerOnly()
    {
        // Act
        var internalEndpoints = EndpointSurface.Internal(_factory.Services);

        // Assert
        internalEndpoints.Should().Contain(e => e.StartsWith("POST /coaching.CoachingInternalService/"))
            .And.OnlyContain(e => e.EndsWith("internal listener :5061"));
    }
}
