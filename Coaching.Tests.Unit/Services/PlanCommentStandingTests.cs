using AutoMapper;
using Coaching.Application.DTOs.Comments;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using MockQueryable;
using MockQueryable.NSubstitute;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Services.Analytics;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// What social is told before it serves or moderates a plan's comments. An event's plan is
/// discussed by whoever may read it and moderated by its creator and the event's admins; a
/// template is discussed by whoever may read it and moderated by its creator.
/// </summary>
[TestFixture]
[Category("Unit")]
public class PlanCommentStandingTests : UnitTestBase
{
    private ITrainingPlanRepository _planRepository = null!;
    private IEventsGrpcClient _eventsGrpcClient = null!;
    private TrainingPlanService _sut = null!;

    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid CreatorId = Guid.NewGuid();
    private static readonly Guid ParticipantId = Guid.NewGuid();
    private static readonly Guid EventAdminId = Guid.NewGuid();
    private static readonly Guid StrangerId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();

        _planRepository = Substitute.For<ITrainingPlanRepository>();
        _eventsGrpcClient = Substitute.For<IEventsGrpcClient>();

        _eventsGrpcClient.IsEventParticipantAsync(EventId, ParticipantId).Returns((true, true));
        _eventsGrpcClient.IsEventParticipantAsync(EventId, CreatorId).Returns((false, true));
        _eventsGrpcClient.IsEventParticipantAsync(EventId, EventAdminId).Returns((false, true));
        _eventsGrpcClient.IsEventParticipantAsync(EventId, StrangerId).Returns((false, true));
        _eventsGrpcClient.IsEventAdminAsync(EventId, EventAdminId).Returns(true);
        _eventsGrpcClient.IsEventAdminAsync(EventId, CreatorId).Returns(true);

        var dialValues = Substitute.For<IRepository<PlanItemDialValue>>();
        dialValues.Query().Returns(_ => new List<PlanItemDialValue>().BuildMock());

        _sut = new TrainingPlanService(
            _planRepository,
            Substitute.For<IPlanSectionRepository>(),
            Substitute.For<IPlanItemRepository>(),
            Substitute.For<IPlanLikeRepository>(),
            Substitute.For<IPlanBookmarkRepository>(),
            Substitute.For<IPlanCommentRepository>(),
            Substitute.For<IDrillRepository>(),
            dialValues,
            Substitute.For<IRepository<PlanStation>>(),
            Substitute.For<IRepository<PlanStationItem>>(),
            Substitute.For<IClubsGrpcClient>(),
            _eventsGrpcClient,
            Substitute.For<IPlanCoachService>(),
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<IMapper>(),
            Substitute.For<ILogger<TrainingPlanService>>(),
            Substitute.For<IAnalyticsCapture>());
    }

    private TrainingPlan StubPlan(PlanType type, TemplateVisibility visibility = TemplateVisibility.Private, Guid? eventId = null)
    {
        var plan = new TrainingPlan
        {
            Id = Guid.NewGuid(),
            Name = "Plan",
            CreatedByUserId = CreatorId,
            PlanType = type,
            EventId = eventId,
            Visibility = visibility
        };
        _planRepository.GetByIdAsync(plan.Id).Returns(plan);
        _planRepository.Query().Returns(_ => new List<TrainingPlan> { plan }.BuildMock());
        return plan;
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAnEventsPlan_ForSomeoneOnTheEvent_ReadsButDoesNotModerate()
    {
        // Arrange
        var plan = StubPlan(PlanType.Instance, eventId: EventId);

        // Act
        var standing = await _sut.GetCommentStandingAsync(plan.Id, ParticipantId);

        // Assert
        standing.Should().Be(new CommentStanding(Exists: true, CanRead: true, CanModerate: false));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAnEventsPlan_ForAStranger_CannotRead()
    {
        // Arrange
        var plan = StubPlan(PlanType.Instance, eventId: EventId);

        // Act
        var standing = await _sut.GetCommentStandingAsync(plan.Id, StrangerId);

        // Assert
        standing.Should().Be(new CommentStanding(true, false, false));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAnEventsPlan_ForAnEventAdmin_ReadsAndModerates()
    {
        // Arrange
        var plan = StubPlan(PlanType.Instance, eventId: EventId);
        plan.CreatedByUserId = Guid.NewGuid();

        // Act
        var standing = await _sut.GetCommentStandingAsync(plan.Id, EventAdminId);

        // Assert
        standing.Should().Be(new CommentStanding(true, true, true));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAnEventsPlan_ForItsCreator_Moderates()
    {
        // Arrange — a creator who is no longer an event admin still moderates their own plan
        var plan = StubPlan(PlanType.Instance, eventId: EventId);
        _eventsGrpcClient.IsEventParticipantAsync(EventId, CreatorId).Returns((true, true));
        _eventsGrpcClient.IsEventAdminAsync(EventId, CreatorId).Returns(false);

        // Act
        var standing = await _sut.GetCommentStandingAsync(plan.Id, CreatorId);

        // Assert
        standing.Should().Be(new CommentStanding(true, true, true));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAnEventsPlan_WhoseEventIsGone_IsNothing()
    {
        // Arrange
        var plan = StubPlan(PlanType.Instance, eventId: EventId);
        _eventsGrpcClient.IsEventParticipantAsync(EventId, ParticipantId).Returns((false, false));

        // Act
        var standing = await _sut.GetCommentStandingAsync(plan.Id, ParticipantId);

        // Assert
        standing.Should().Be(CommentStanding.Missing);
    }

    [Test]
    public async Task GetCommentStandingAsync_WhenTheEventsServiceIsDown_Throws()
    {
        // Arrange — not knowing is not a no
        var plan = StubPlan(PlanType.Instance, eventId: EventId);
        _eventsGrpcClient.IsEventParticipantAsync(EventId, ParticipantId)
            .Returns<(bool, bool)>(_ => throw new InvalidOperationException("events-service unavailable"));

        // Act
        var act = () => _sut.GetCommentStandingAsync(plan.Id, ParticipantId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAPrivateTemplate_ForItsCreator_ReadsAndModerates()
    {
        // Arrange
        var plan = StubPlan(PlanType.Template);

        // Act
        var standing = await _sut.GetCommentStandingAsync(plan.Id, CreatorId);

        // Assert
        standing.Should().Be(new CommentStanding(true, true, true));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAPrivateTemplate_ForAStranger_CannotRead()
    {
        // Arrange
        var plan = StubPlan(PlanType.Template);

        // Act
        var standing = await _sut.GetCommentStandingAsync(plan.Id, StrangerId);

        // Assert
        standing.Should().Be(new CommentStanding(true, false, false));
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAPublicTemplate_ForAStranger_ReadsButDoesNotModerate()
    {
        // Arrange — an event admin of some other event has no say over a template
        var plan = StubPlan(PlanType.Template, TemplateVisibility.Public);

        // Act
        var standing = await _sut.GetCommentStandingAsync(plan.Id, EventAdminId);

        // Assert
        standing.Should().Be(new CommentStanding(true, true, false));
        await _eventsGrpcClient.DidNotReceiveWithAnyArgs().IsEventAdminAsync(default, default);
    }

    [Test]
    public async Task GetCommentStandingAsync_OnAPlanThatDoesNotExist_IsNothing()
    {
        // Act
        var standing = await _sut.GetCommentStandingAsync(Guid.NewGuid(), CreatorId);

        // Assert
        standing.Should().Be(CommentStanding.Missing);
    }
}
