using AutoMapper;
using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Extensions;
using Coaching.Application.Interfaces.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Mappings;

/// <summary>
/// The feedback media URLs are produced by AutoMapper value resolvers that take
/// <see cref="IFeedbackMediaUrlSigner"/> in their constructor. Those resolvers can only be
/// constructed through DI, so the map has to be exercised against a real container (SPI-5376).
/// </summary>
[TestFixture]
[Category("Unit")]
public class FeedbackMappingProfileTests : UnitTestBase
{
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private IMapper _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();

        var signer = Substitute.For<IFeedbackMediaUrlSigner>();
        signer.SignReadUrl(Arg.Any<string>()).Returns(call => $"signed:{call.Arg<string>()}");
        signer.IsStored(Arg.Any<string>()).Returns(call => call.Arg<string>().StartsWith("s3://"));

        var services = new ServiceCollection();
        services.AddApplicationMappings();
        services.AddScoped(_ => signer);

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _sut = _scope.ServiceProvider.GetRequiredService<IMapper>();
    }

    [TearDown]
    public override void TearDown()
    {
        _scope.Dispose();
        _provider.Dispose();
        base.TearDown();
    }

    [Test]
    public void Map_FeedbackWithImprovementPointMedia_SignsMediaLinkUrls()
    {
        // Arrange
        var feedback = new Feedback
        {
            RecipientUserId = Guid.NewGuid(),
            CoachUserId = Guid.NewGuid(),
            ImprovementPoints =
            [
                new ImprovementPoint
                {
                    Description = "Keep the platform still",
                    MediaLinks = [new ImprovementPointMedia { Url = "s3://clip.mp4", Type = FeedbackMediaType.Video }],
                },
            ],
        };

        // Act
        var dto = _sut.Map<FeedbackDto>(feedback);

        // Assert
        dto.ImprovementPoints.Should().ContainSingle()
            .Which.MediaLinks.Should().ContainSingle()
            .Which.Url.Should().Be("signed:s3://clip.mp4");
    }

    [Test]
    public void Map_FeedbackWithAttachments_SignsAttachmentUrls()
    {
        // Arrange
        var feedback = new Feedback
        {
            RecipientUserId = Guid.NewGuid(),
            CoachUserId = Guid.NewGuid(),
            Media = [new FeedbackMedia { Url = "s3://photo.jpg", Type = FeedbackMediaType.Image }],
        };

        // Act
        var dto = _sut.Map<FeedbackDto>(feedback);

        // Assert
        dto.Attachments.Should().ContainSingle()
            .Which.Url.Should().Be("signed:s3://photo.jpg");
    }

    [Test]
    public void Map_FeedbackWithAttachments_SaysWhichAreUploadedFilesAndWhichAreLinks()
    {
        // Arrange — the row stores no source; the web editor needs one to put a file in the
        // uploader and a link in the link dialog, and our bucket is what tells them apart.
        var feedback = new Feedback
        {
            RecipientUserId = Guid.NewGuid(),
            CoachUserId = Guid.NewGuid(),
            Media =
            [
                new FeedbackMedia { Url = "s3://photo.jpg", Type = FeedbackMediaType.Image, Order = 0 },
                new FeedbackMedia { Url = "https://youtu.be/abc", Type = FeedbackMediaType.Video, Order = 1 },
            ],
        };

        // Act
        var dto = _sut.Map<FeedbackDto>(feedback);

        // Assert
        dto.Attachments.Select(a => a.Source).Should().Equal(FeedbackMediaSource.File, FeedbackMediaSource.Link);
    }
}
