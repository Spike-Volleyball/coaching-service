using AutoMapper;
using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Models.Feedback;
using Coaching.Domain.Models.Drills;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Models;
using Shared.Options;
using Shared.Services.FileStorage.Intefaces;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class FeedbackMediaUploadTests : UnitTestBase
{
    private IFeedbackRepository _feedbackRepository = null!;
    private IRepository<ImprovementPoint> _pointRepository = null!;
    private IRepository<ImprovementPointDrill> _drillLinkRepository = null!;
    private IRepository<ImprovementPointMedia> _mediaRepository = null!;
    private IRepository<FeedbackMedia> _feedbackMediaRepository = null!;
    private IRepository<Praise> _praiseRepository = null!;
    private IRepository<Drill> _drillRepository = null!;
    private IFeedbackAuthorizationService _authorizationService = null!;
    private IRepository<UserProfile> _userProfileRepository = null!;
    private IMapper _mapper = null!;
    private IFileService _fileService = null!;
    private IOptions<S3Settings> _s3Settings = null!;
    private FeedbackService _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _feedbackRepository = Substitute.For<IFeedbackRepository>();
        _pointRepository = Substitute.For<IRepository<ImprovementPoint>>();
        _drillLinkRepository = Substitute.For<IRepository<ImprovementPointDrill>>();
        _mediaRepository = Substitute.For<IRepository<ImprovementPointMedia>>();
        _feedbackMediaRepository = Substitute.For<IRepository<FeedbackMedia>>();
        _praiseRepository = Substitute.For<IRepository<Praise>>();
        _drillRepository = Substitute.For<IRepository<Drill>>();
        _authorizationService = Substitute.For<IFeedbackAuthorizationService>();
        _userProfileRepository = Substitute.For<IRepository<UserProfile>>();
        _mapper = Substitute.For<IMapper>();
        _fileService = Substitute.For<IFileService>();
        _s3Settings = Options.Create(new S3Settings { Bucket = "test-bucket", PublicBaseUrl = "https://cdn.example.com" });

        _fileService.GetPresignedUploadLink(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult("https://put.example.com/signed"));
        _fileService.GetPublicUrl(Arg.Any<string>())
            .Returns("https://cdn.example.com/feedback/key");

        _sut = new FeedbackService(
            _feedbackRepository,
            _pointRepository,
            _drillLinkRepository,
            _mediaRepository,
            _feedbackMediaRepository,
            _praiseRepository,
            _drillRepository,
            _authorizationService,
            Substitute.For<IEventsGrpcClient>(),
            _userProfileRepository,
            _mapper,
            _fileService,
            _s3Settings,
            TimeProvider,
            Substitute.For<IPublishEndpoint>());
    }

    [Test]
    public async Task GetMediaUploadUrl_RejectsDisallowedType()
    {
        // Arrange
        var request = new FeedbackMediaUploadRequestDto
        {
            FileName = "x.exe",
            ContentType = "application/x-msdownload",
            FileSize = 10
        };

        // Act & Assert
        await _sut.Invoking(s => s.GetMediaUploadUrlAsync(request, Guid.NewGuid()))
            .Should().ThrowAsync<Shared.Exceptions.BadRequestException>();
    }

    [Test]
    public async Task GetMediaUploadUrl_RejectsOversizeImage()
    {
        // Arrange
        var request = new FeedbackMediaUploadRequestDto
        {
            FileName = "big.jpg",
            ContentType = "image/jpeg",
            FileSize = 11L * 1024 * 1024
        };

        // Act & Assert
        await _sut.Invoking(s => s.GetMediaUploadUrlAsync(request, Guid.NewGuid()))
            .Should().ThrowAsync<Shared.Exceptions.BadRequestException>();
    }

    [Test]
    public async Task GetMediaUploadUrl_ReturnsUrls_ForValidImage()
    {
        // Arrange
        var request = new FeedbackMediaUploadRequestDto
        {
            FileName = "ok.jpg",
            ContentType = "image/jpeg",
            FileSize = 1024
        };

        // Act
        var result = await _sut.GetMediaUploadUrlAsync(request, Guid.NewGuid());

        // Assert
        result.UploadUrl.Should().NotBeNullOrEmpty();
        result.FileUrl.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task GetMediaUploadUrl_AcceptsImageAtExactSizeLimit()
    {
        // Arrange
        var request = new FeedbackMediaUploadRequestDto
        {
            FileName = "limit.jpg",
            ContentType = "image/jpeg",
            FileSize = 10L * 1024 * 1024
        };

        // Act
        var result = await _sut.GetMediaUploadUrlAsync(request, Guid.NewGuid());

        // Assert
        result.UploadUrl.Should().NotBeNullOrEmpty();
    }
}
