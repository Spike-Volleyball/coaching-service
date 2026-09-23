using AutoMapper;
using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Application.Validation;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Feedback;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;
using Shared.Models;
using Shared.Options;
using Shared.Services.FileStorage.Intefaces;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Every write that takes a link from a client refuses one that is not http or https, names the
/// field it came in, and writes nothing — a create saves in stages, so a late refusal would leave a
/// half-made feedback behind (SPI-6458).
/// </summary>
[TestFixture]
[Category("Unit")]
public class FeedbackLinkSchemeTests : UnitTestBase
{
    private const string Hostile = "javascript:alert(document.cookie)";
    private const string GoodLink = "https://youtu.be/serve";

    private IFeedbackRepository _feedbackRepository = null!;
    private IRepository<ImprovementPoint> _pointRepository = null!;
    private IRepository<ImprovementPointMedia> _mediaRepository = null!;
    private IRepository<FeedbackMedia> _feedbackMediaRepository = null!;
    private FeedbackService _sut = null!;

    private readonly Guid _coachId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _feedbackRepository = Substitute.For<IFeedbackRepository>();
        _pointRepository = Substitute.For<IRepository<ImprovementPoint>>();
        _mediaRepository = Substitute.For<IRepository<ImprovementPointMedia>>();
        _feedbackMediaRepository = Substitute.For<IRepository<FeedbackMedia>>();

        _sut = new FeedbackService(
            _feedbackRepository,
            _pointRepository,
            Substitute.For<IRepository<ImprovementPointDrill>>(),
            _mediaRepository,
            _feedbackMediaRepository,
            Substitute.For<IRepository<Praise>>(),
            Substitute.For<IRepository<Drill>>(),
            Substitute.For<IFeedbackAuthorizationService>(),
            Substitute.For<IEventsGrpcClient>(),
            Substitute.For<IRepository<UserProfile>>(),
            Substitute.For<IMapper>(),
            Substitute.For<IFileService>(),
            Options.Create(new S3Settings { Bucket = "test-bucket", PublicBaseUrl = "https://cdn.test" }),
            TimeProvider,
            Substitute.For<IPublishEndpoint>());
    }

    private Feedback CoachesFeedback() => new() { CoachUserId = _coachId, RecipientUserId = Guid.NewGuid() };

    [Test]
    public async Task CreateAsync_WithAJavascriptAttachment_ThrowsNamingItAndWritesNothing()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = Guid.NewGuid(),
            Attachments =
            [
                new CreateFeedbackMediaDto { Url = GoodLink, Type = FeedbackMediaType.Video },
                new CreateFeedbackMediaDto { Url = Hostile, Type = FeedbackMediaType.Video },
            ],
        };

        // Act
        var act = () => _sut.CreateAsync(request, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("attachments[1].url");
        error.FieldErrors.Single().Code.Should().Be(LinkUrl.NotHttpCode);
        _feedbackRepository.DidNotReceive().Add(Arg.Any<Feedback>());
        await _feedbackRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task CreateAsync_WithADataLinkOnAPoint_ThrowsNamingThePointsLinkAndWritesNothing()
    {
        // Arrange
        var request = new CreateFeedbackDto
        {
            RecipientUserId = Guid.NewGuid(),
            ImprovementPoints =
            [
                new CreateImprovementPointDto { Description = "Platform" },
                new CreateImprovementPointDto
                {
                    Description = "Footwork",
                    MediaLinks =
                    [
                        new CreateImprovementPointMediaDto { Url = GoodLink, Type = FeedbackMediaType.Video },
                        new CreateImprovementPointMediaDto { Url = "data:text/html,<script>alert(1)</script>", Type = FeedbackMediaType.Document },
                    ],
                },
            ],
        };

        // Act
        var act = () => _sut.CreateAsync(request, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("improvementPoints[1].mediaLinks[1].url");
        _feedbackRepository.DidNotReceive().Add(Arg.Any<Feedback>());
        _pointRepository.DidNotReceive().Add(Arg.Any<ImprovementPoint>());
    }

    [Test]
    public async Task UpdateAsync_WithANewJavascriptAttachment_ThrowsNamingItAndWritesNothing()
    {
        // Arrange
        var feedback = CoachesFeedback();
        _feedbackRepository.GetByIdAsync(feedback.Id).Returns(feedback);
        var request = new UpdateFeedbackDto
        {
            Content = "<p>Rewritten</p>",
            Attachments = [new UpdateFeedbackMediaDto { Url = Hostile, Type = FeedbackMediaType.Video }],
        };

        // Act
        var act = () => _sut.UpdateAsync(feedback.Id, request, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("attachments[0].url");
        feedback.Content.Should().BeNull();
        _feedbackMediaRepository.DidNotReceive().Add(Arg.Any<FeedbackMedia>());
        await _feedbackRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task AddImprovementPointAsync_WithAVbscriptLink_ThrowsNamingItAndWritesNothing()
    {
        // Arrange
        var feedback = CoachesFeedback();
        _feedbackRepository.GetByIdWithDetailsAsync(feedback.Id).Returns(feedback);
        var request = new AddImprovementPointDto
        {
            Description = "Approach",
            MediaLinks = [new CreateImprovementPointMediaDto { Url = "vbscript:msgbox(1)", Type = FeedbackMediaType.Video }],
        };

        // Act
        var act = () => _sut.AddImprovementPointAsync(feedback.Id, request, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("mediaLinks[0].url");
        _pointRepository.DidNotReceive().Add(Arg.Any<ImprovementPoint>());
        _mediaRepository.DidNotReceive().Add(Arg.Any<ImprovementPointMedia>());
    }

    [Test]
    public async Task AddMediaToPointAsync_WithAJavascriptUrl_ThrowsNamingItAndWritesNothing()
    {
        // Arrange
        var feedback = CoachesFeedback();
        _feedbackRepository.GetByIdAsync(feedback.Id).Returns(feedback);
        var request = new CreateImprovementPointMediaDto { Url = Hostile, Type = FeedbackMediaType.Video, Source = FeedbackMediaSource.Link };

        // Act
        var act = () => _sut.AddMediaToPointAsync(feedback.Id, Guid.NewGuid(), request, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("url");
        _mediaRepository.DidNotReceive().Add(Arg.Any<ImprovementPointMedia>());
        await _mediaRepository.DidNotReceive().SaveChangesAsync();
    }
}
