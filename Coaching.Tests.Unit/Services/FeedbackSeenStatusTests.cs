using AutoMapper;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
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

[TestFixture]
[Category("Unit")]
public class FeedbackSeenStatusTests : UnitTestBase
{
    private IFeedbackRepository _feedbackRepository = null!;
    private IFeedbackAuthorizationService _authorizationService = null!;
    private IMapper _mapper = null!;
    private FeedbackService _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _feedbackRepository = Substitute.For<IFeedbackRepository>();
        _authorizationService = Substitute.For<IFeedbackAuthorizationService>();
        _mapper = Substitute.For<IMapper>();

        _sut = new FeedbackService(
            _feedbackRepository,
            Substitute.For<IRepository<ImprovementPoint>>(),
            Substitute.For<IRepository<ImprovementPointDrill>>(),
            Substitute.For<IRepository<ImprovementPointMedia>>(),
            Substitute.For<IRepository<FeedbackMedia>>(),
            Substitute.For<IRepository<Praise>>(),
            Substitute.For<IRepository<Drill>>(),
            _authorizationService,
            Substitute.For<IEventsGrpcClient>(),
            Substitute.For<IRepository<UserProfile>>(),
            _mapper,
            Substitute.For<IFileService>(),
            Options.Create(new S3Settings { Bucket = "b", PublicBaseUrl = "https://cdn" }),
            TimeProvider,
            Substitute.For<IPublishEndpoint>());
    }

    [Test]
    public async Task MarkSeenAsync_WhenRecipientOpensSharedFeedback_SetsSeenAtAndSaves()
    {
        // Arrange
        var recipientId = Guid.NewGuid();
        var feedback = new Feedback { Id = Guid.NewGuid(), RecipientUserId = recipientId, SharedWithPlayer = true, SeenAt = null };
        _feedbackRepository.GetByIdAsync(feedback.Id).Returns(feedback);

        // Act
        await _sut.MarkSeenAsync(feedback.Id, recipientId);

        // Assert
        feedback.SeenAt.Should().Be(Now);
        feedback.LastSeenAt.Should().Be(Now);
        _feedbackRepository.Received(1).Update(feedback);
        await _feedbackRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task MarkSeenAsync_WhenAlreadySeen_KeepsTheReceiptAndMovesLastSeen()
    {
        // Arrange
        var recipientId = Guid.NewGuid();
        var firstSeen = PastDate(2);
        var feedback = new Feedback { Id = Guid.NewGuid(), RecipientUserId = recipientId, SharedWithPlayer = true, SeenAt = firstSeen, LastSeenAt = firstSeen };
        _feedbackRepository.GetByIdAsync(feedback.Id).Returns(feedback);

        // Act
        await _sut.MarkSeenAsync(feedback.Id, recipientId);

        // Assert
        feedback.SeenAt.Should().Be(firstSeen);
        feedback.LastSeenAt.Should().Be(Now);
        await _feedbackRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task MarkSeenAsync_WhenNotRecipient_ThrowsForbidden()
    {
        // Arrange
        var feedback = new Feedback { Id = Guid.NewGuid(), RecipientUserId = Guid.NewGuid(), SharedWithPlayer = true };
        _feedbackRepository.GetByIdAsync(feedback.Id).Returns(feedback);

        // Act & Assert
        await _sut.Invoking(s => s.MarkSeenAsync(feedback.Id, Guid.NewGuid()))
            .Should().ThrowAsync<ForbiddenException>();
        await _feedbackRepository.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task MarkSeenAsync_WhenNotShared_ThrowsForbidden()
    {
        // Arrange
        var recipientId = Guid.NewGuid();
        var feedback = new Feedback { Id = Guid.NewGuid(), RecipientUserId = recipientId, SharedWithPlayer = false };
        _feedbackRepository.GetByIdAsync(feedback.Id).Returns(feedback);

        // Act & Assert
        await _sut.Invoking(s => s.MarkSeenAsync(feedback.Id, recipientId))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Test]
    public async Task MarkSeenAsync_WhenFeedbackMissing_ThrowsNotFound()
    {
        // Arrange
        _feedbackRepository.GetByIdAsync(Arg.Any<Guid>()).Returns((Feedback?)null);

        // Act & Assert
        await _sut.Invoking(s => s.MarkSeenAsync(Guid.NewGuid(), Guid.NewGuid()))
            .Should().ThrowAsync<EntityNotFoundException>();
    }

    [Test]
    public async Task GetUnseenCountAsync_ReturnsRepositoryCount()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _feedbackRepository.GetUnseenCountAsync(userId).Returns(3);

        // Act
        var count = await _sut.GetUnseenCountAsync(userId);

        // Assert
        count.Should().Be(3);
    }
}
