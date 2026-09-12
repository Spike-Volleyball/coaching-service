using AutoMapper;
using Coaching.Application.DTOs.Feedback;
using Coaching.Application.Extensions;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Models.Drills;
using Coaching.Domain.Models.Feedback;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MockQueryable;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Models;
using Shared.Options;
using Shared.Services.FileStorage.Intefaces;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// A feedback row used to carry only the id of the session it was given at, so both clients
/// fetched every event themselves — one request per card. The service now names the session on
/// each row, asking events-service once per page for the distinct ids.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FeedbackEventEnrichmentTests : UnitTestBase
{
    private static readonly Guid RecipientId = Guid.NewGuid();
    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly DateTime FridayEvening = new(2026, 9, 11, 18, 30, 0, DateTimeKind.Utc);

    private IFeedbackRepository _feedbackRepository = null!;
    private IEventsGrpcClient _eventsClient = null!;
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private FeedbackService _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _feedbackRepository = Substitute.For<IFeedbackRepository>();
        _feedbackRepository.Query().Returns(new List<Feedback>().BuildMock());
        _eventsClient = Substitute.For<IEventsGrpcClient>();
        var userProfileRepository = Substitute.For<IRepository<UserProfile>>();
        userProfileRepository.Query().Returns(new List<UserProfile>().BuildMock());

        var services = new ServiceCollection();
        services.AddApplicationMappings();
        services.AddScoped(_ => Substitute.For<IFeedbackMediaUrlSigner>());
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        _sut = new FeedbackService(
            _feedbackRepository,
            Substitute.For<IRepository<ImprovementPoint>>(),
            Substitute.For<IRepository<ImprovementPointDrill>>(),
            Substitute.For<IRepository<ImprovementPointMedia>>(),
            Substitute.For<IRepository<FeedbackMedia>>(),
            Substitute.For<IRepository<Praise>>(),
            Substitute.For<IRepository<Drill>>(),
            Substitute.For<IFeedbackAuthorizationService>(),
            _eventsClient,
            userProfileRepository,
            _scope.ServiceProvider.GetRequiredService<IMapper>(),
            Substitute.For<IFileService>(),
            Options.Create(new S3Settings { Bucket = "b", PublicBaseUrl = "https://cdn" }),
            TimeProvider,
            Substitute.For<IPublishEndpoint>());
    }

    [TearDown]
    public override void TearDown()
    {
        _scope.Dispose();
        _provider.Dispose();
        base.TearDown();
    }

    [Test]
    public async Task GetReceivedFeedbackAsync_TwentyRowsOnThreeEvents_AsksEventsOnceForTheDistinctIds()
    {
        // Arrange
        var events = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var page = Enumerable.Range(0, 20).Select(i => FeedbackOn(events[i % 3])).ToList();
        _feedbackRepository.GetByRecipientIdAsync(RecipientId, 1, 20).Returns(page);
        _eventsClient.GetEventInfoAsync(Arg.Any<IReadOnlyCollection<Guid>>())
            .Returns(call => InfoFor(call.Arg<IReadOnlyCollection<Guid>>()));

        // Act
        var result = await _sut.GetReceivedFeedbackAsync(RecipientId, 1, 20);

        // Assert
        await _eventsClient.Received(1).GetEventInfoAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 3 && events.All(ids.Contains)));
        result.Items.Should().HaveCount(20);
        result.Items.Should().OnlyContain(f => f.Event != null && f.Event.Id == f.EventId);
    }

    [Test]
    public async Task GetReceivedFeedbackAsync_RowWithAKnownEvent_CarriesNameStartAndTypeAndKeepsEventId()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        _feedbackRepository.GetByRecipientIdAsync(RecipientId, 1, 20).Returns([FeedbackOn(eventId)]);
        _eventsClient.GetEventInfoAsync(Arg.Any<IReadOnlyCollection<Guid>>())
            .Returns(new Dictionary<Guid, EventInfo>
            {
                [eventId] = new(eventId, "Friday session", FridayEvening, "TrainingSession")
            });

        // Act
        var result = await _sut.GetReceivedFeedbackAsync(RecipientId, 1, 20);

        // Assert
        var row = result.Items.Single();
        row.EventId.Should().Be(eventId);
        row.Event.Should().BeEquivalentTo(new FeedbackEventDto
        {
            Id = eventId,
            Name = "Friday session",
            StartTime = FridayEvening,
            Type = "TrainingSession"
        });
    }

    [Test]
    public async Task GetGivenFeedbackAsync_EventUnknownToEventsService_LeavesTheSummaryEmptyButKeepsEventId()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        _feedbackRepository.GetByCoachIdAsync(CoachId, 1, 20).Returns([FeedbackOn(eventId)]);
        _eventsClient.GetEventInfoAsync(Arg.Any<IReadOnlyCollection<Guid>>())
            .Returns(new Dictionary<Guid, EventInfo>());

        // Act
        var result = await _sut.GetGivenFeedbackAsync(CoachId, 1, 20);

        // Assert
        var row = result.Items.Single();
        row.EventId.Should().Be(eventId);
        row.Event.Should().BeNull();
    }

    [Test]
    public async Task GetReceivedFeedbackAsync_NoRowNamesAnEvent_AsksEventsForNothing()
    {
        // Arrange
        _feedbackRepository.GetByRecipientIdAsync(RecipientId, 1, 20).Returns([FeedbackOn(null), FeedbackOn(null)]);

        // Act
        var result = await _sut.GetReceivedFeedbackAsync(RecipientId, 1, 20);

        // Assert
        result.Items.Should().HaveCount(2).And.OnlyContain(f => f.Event == null && f.EventId == null);
        await _eventsClient.DidNotReceiveWithAnyArgs().GetEventInfoAsync(default!);
    }

    [Test]
    public async Task GetByIdAsync_EventLinked_CarriesTheSummary()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var feedback = FeedbackOn(eventId);
        _feedbackRepository.GetByIdWithDetailsAsync(feedback.Id).Returns(feedback);
        _eventsClient.GetEventInfoAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == eventId))
            .Returns(InfoFor([eventId]));

        // Act
        var dto = await _sut.GetByIdAsync(feedback.Id, RecipientId);

        // Assert
        dto!.Event.Should().NotBeNull();
        dto.Event!.Id.Should().Be(eventId);
        dto.Event.Name.Should().Be(NameOf(eventId));
    }

    [Test]
    public async Task GetByEventIdAsync_WholePageOnOneEvent_AsksForThatOneId()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        _feedbackRepository.GetByEventIdAsync(eventId)
            .Returns(Enumerable.Range(0, 5).Select(_ => FeedbackOn(eventId)).ToList());
        _eventsClient.GetEventInfoAsync(Arg.Any<IReadOnlyCollection<Guid>>())
            .Returns(call => InfoFor(call.Arg<IReadOnlyCollection<Guid>>()));

        // Act
        var items = (await _sut.GetByEventIdAsync(eventId, CoachId)).ToList();

        // Assert
        items.Should().HaveCount(5).And.OnlyContain(f => f.Event != null && f.Event.Name == NameOf(eventId));
        await _eventsClient.Received(1).GetEventInfoAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == eventId));
    }

    private static Feedback FeedbackOn(Guid? eventId) => new()
    {
        Id = Guid.NewGuid(),
        RecipientUserId = RecipientId,
        CoachUserId = CoachId,
        EventId = eventId,
        SharedWithPlayer = true,
    };

    private static string NameOf(Guid eventId) => $"Session {eventId:N}"[..16];

    private static IReadOnlyDictionary<Guid, EventInfo> InfoFor(IEnumerable<Guid> eventIds) =>
        eventIds.ToDictionary(id => id, id => new EventInfo(id, NameOf(id), FridayEvening, "TrainingSession"));
}
