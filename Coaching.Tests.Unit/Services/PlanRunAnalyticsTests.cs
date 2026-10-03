using AutoMapper;
using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Coaching.Tests.Unit.Analytics;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using MockQueryable;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;
using Shared.Services.Analytics;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// SPI-6282: journey 7's server-side events. The client already says a coach opened the plan
/// builder and the floor; these are the other half of that subtraction, and a run that ends by
/// advancing off the end of the plan has to count as finished or the drop-off is fiction.
/// </summary>
[TestFixture]
[Category("Unit")]
public class PlanRunAnalyticsTests : UnitTestBase
{
    private ITrainingPlanRepository _planRepository = null!;
    private IPlanItemRepository _itemRepository = null!;
    private ITrainingPlanRunRepository _runRepository = null!;
    private IEventsGrpcClient _eventsGrpcClient = null!;
    private IAnalyticsCapture _analytics = null!;
    private TrainingPlanService _planService = null!;
    private RunService _runService = null!;

    private TrainingPlan? _persisted;

    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();
    private static readonly Guid TemplateId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();
    private static readonly Guid Item1Id = Guid.NewGuid();
    private static readonly Guid Item2Id = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _persisted = null;

        _planRepository = Substitute.For<ITrainingPlanRepository>();
        _itemRepository = Substitute.For<IPlanItemRepository>();
        _runRepository = Substitute.For<ITrainingPlanRunRepository>();
        _eventsGrpcClient = Substitute.For<IEventsGrpcClient>();
        _analytics = Substitute.For<IAnalyticsCapture>();

        // Mirrors the database: the plan comes back from the re-fetch carrying the items that
        // were added against it, which is where item_count is read from.
        _planRepository.When(repository => repository.Add(Arg.Any<TrainingPlan>()))
            .Do(call => _persisted = call.Arg<TrainingPlan>());
        _itemRepository.When(repository => repository.Add(Arg.Any<PlanItem>()))
            .Do(call => _persisted?.Items.Add(call.Arg<PlanItem>()));
        _planRepository.GetByIdWithDetailsAsync(Arg.Any<Guid>())
            .Returns(call => call.Arg<Guid>() == _persisted?.Id ? _persisted : null);
        _planRepository.Query().Returns(_ => new List<TrainingPlan>().BuildMock());

        var mapper = Substitute.For<IMapper>();
        mapper.Map<TrainingPlanDetailDto>(Arg.Any<TrainingPlan?>())
            .Returns(new TrainingPlanDetailDto { Name = "Tuesday session" });

        var dialValues = Substitute.For<IRepository<PlanItemDialValue>>();
        dialValues.Query().Returns(_ => new List<PlanItemDialValue>().BuildMock());

        _planService = new TrainingPlanService(
            _planRepository,
            Substitute.For<IPlanSectionRepository>(),
            _itemRepository,
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
            mapper,
            Substitute.For<ILogger<TrainingPlanService>>(),
            _analytics);

        _runService = new RunService(
            _runRepository,
            Substitute.For<ITrainingPlanRunItemRepository>(),
            Substitute.For<IRunStationRepository>(),
            _planRepository,
            Substitute.For<IRunBroadcaster>(),
            _eventsGrpcClient,
            TimeProvider,
            _analytics);
    }

    [Test]
    public async Task CreateAsync_WithAPlanThatSaves_CapturesTrainingPlanCreatedOnce()
    {
        // Act
        await _planService.CreateAsync(
            new CreatePlanDto("Tuesday session", null, ClubId, Items: [WarmUpItem(), BreakItem()]), CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.TrainingPlanCreated, CoachId);
        properties["plan_id"].Should().Be(_persisted!.Id);
        properties["club_id"].Should().Be(ClubId);
        properties["event_id"].Should().BeNull();
        properties["from_template"].Should().Be(false);
        properties["item_count"].Should().Be(2);
    }

    [Test]
    public async Task CreateAsync_WithNoName_CapturesNothing()
    {
        // Act
        var act = () => _planService.CreateAsync(new CreatePlanDto("  ", null, ClubId), CoachId);

        // Assert
        await act.Should().ThrowAsync<BadRequestException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task CreateEventPlanAsync_FromATemplate_CapturesThePlanAgainstItsEvent()
    {
        // Arrange
        var template = Template();
        _planRepository.GetByIdWithDetailsAsync(TemplateId).Returns(template);
        _eventsGrpcClient.IsEventAdminAsync(EventId, CoachId).Returns(true);

        // Act
        await _planService.CreateEventPlanAsync(
            EventId, new CreateEventPlanDto(null, null, TemplateId), CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.TrainingPlanCreated, CoachId);
        properties["plan_id"].Should().Be(_persisted!.Id);
        properties["event_id"].Should().Be(EventId);
        properties["from_template"].Should().Be(true);
        properties["item_count"].Should().Be(2);
        // An event's plan carries no club of its own; the club is the event's, joined on event_id.
        properties["club_id"].Should().BeNull();
    }

    [Test]
    public async Task CreateEventPlanAsync_WhenTheCallerDoesNotRunTheEvent_CapturesNothing()
    {
        // Arrange
        _eventsGrpcClient.IsEventAdminAsync(EventId, CoachId).Returns(false);

        // Act
        var act = () => _planService.CreateEventPlanAsync(
            EventId, new CreateEventPlanDto("Tuesday session", null, null), CoachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task StartAsync_WhenTheRunSaves_CapturesPracticeRunStartedOnce()
    {
        // Arrange
        StubInstancePlan();
        _runRepository.GetByEventIdWithDetailsAsync(EventId).Returns((TrainingPlanRun?)null);

        // Act
        await _runService.StartAsync(EventId, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PracticeRunStarted, CoachId);
        properties["event_id"].Should().Be(EventId);
        properties["plan_id"].Should().Be(PlanId);
        properties["item_count"].Should().Be(2);
        properties["is_restart"].Should().Be(false);
    }

    [Test]
    public async Task StartAsync_OverAnEarlierRun_CapturesARestart()
    {
        // Arrange — Start Over on a finished session.
        StubInstancePlan();
        var run = RunOnSecondItem();
        run.Status = RunStatus.Completed;
        StubRun(run);

        // Act
        await _runService.StartAsync(EventId, CoachId);

        // Assert
        _analytics.CapturedOnce(AnalyticsEventNames.PracticeRunStarted, CoachId)["is_restart"].Should().Be(true);
    }

    [Test]
    public async Task StartAsync_WhenTheCallerDidNotWriteThePlan_CapturesNothing()
    {
        // Arrange
        StubInstancePlan();

        // Act
        var act = () => _runService.StartAsync(EventId, OtherUserId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task CompleteAsync_WhenTheRunFinishes_CapturesPracticeRunCompletedOnce()
    {
        // Arrange
        StubInstancePlan();
        StubRun(RunOnSecondItem());
        AdvanceTime(TimeSpan.FromMinutes(30));

        // Act
        await _runService.CompleteAsync(EventId, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PracticeRunCompleted, CoachId);
        properties["event_id"].Should().Be(EventId);
        properties["plan_id"].Should().Be(PlanId);
        properties["duration_seconds"].Should().Be(1800);
        properties["items_advanced"].Should().Be(2);
    }

    [Test]
    public async Task CompleteAsync_WhenTheRunIsAlreadyFinished_CapturesNothing()
    {
        // Arrange
        StubInstancePlan();
        var run = RunOnSecondItem();
        run.Status = RunStatus.Completed;
        StubRun(run);

        // Act
        await _runService.CompleteAsync(EventId, CoachId);

        // Assert
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task AdvanceAsync_PastTheLastItem_CapturesPracticeRunCompleted()
    {
        // Arrange
        StubInstancePlan();
        StubRun(RunOnSecondItem());
        AdvanceTime(TimeSpan.FromMinutes(45));

        // Act
        await _runService.AdvanceAsync(EventId, Item2Id, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PracticeRunCompleted, CoachId);
        properties["duration_seconds"].Should().Be(2700);
        properties["items_advanced"].Should().Be(2);
        _analytics.CapturedNone(AnalyticsEventNames.PracticeRunStepChanged);
    }

    [Test]
    public async Task AdvanceAsync_ToTheNextItem_CapturesAStepForwardAndNoCompletion()
    {
        // Arrange — 4 minutes into a 5-minute step.
        StubInstancePlan();
        StubRun(RunOnFirstItem());
        AdvanceTime(TimeSpan.FromMinutes(4));

        // Act
        await _runService.AdvanceAsync(EventId, Item1Id, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PracticeRunStepChanged, CoachId);
        properties["event_id"].Should().Be(EventId);
        properties["plan_id"].Should().Be(PlanId);
        properties["direction"].Should().Be(RunStepDirection.Next);
        properties["from_order"].Should().Be(1);
        properties["to_order"].Should().Be(2);
        properties["elapsed_seconds"].Should().Be(240);
        properties["planned_seconds"].Should().Be(300);
        _analytics.CapturedNone(AnalyticsEventNames.PracticeRunCompleted);
    }

    [Test]
    public async Task AdvanceAsync_AStaleTap_CapturesNothing()
    {
        // Arrange
        StubInstancePlan();
        StubRun(RunOnSecondItem());

        // Act
        await _runService.AdvanceAsync(EventId, Item1Id, CoachId);

        // Assert
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task GoToAsync_ThePreviousItem_CapturesAStepBackWithTheTimeOfTheVisitLeft()
    {
        // Arrange — Next was tapped too early; 20 seconds later the coach goes back.
        StubInstancePlan();
        StubRun(RunOnSecondItem());
        AdvanceTime(TimeSpan.FromSeconds(20));

        // Act
        await _runService.GoToAsync(EventId, Item2Id, Item1Id, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PracticeRunStepChanged, CoachId);
        properties["direction"].Should().Be(RunStepDirection.Previous);
        properties["from_order"].Should().Be(2);
        properties["to_order"].Should().Be(1);
        properties["elapsed_seconds"].Should().Be(20);
        properties["planned_seconds"].Should().Be(600);
    }

    [TestCase(1, 3)]
    [TestCase(3, 1)]
    public async Task GoToAsync_PastTheNeighbouringItem_CapturesAJump(int fromOrder, int toOrder)
    {
        // Arrange — three steps, so a move can skip one.
        StubInstancePlan();
        var items = TwoRunItems();
        items.Add(new() { Id = Guid.NewGuid(), PlanItemId = Guid.NewGuid(), Order = 3, PlannedDurationSeconds = 900 });
        var from = items.Single(i => i.Order == fromOrder);
        var to = items.Single(i => i.Order == toOrder);
        from.StartedAtUtc = Now;
        StubRun(NewRun(items, from.PlanItemId));

        // Act
        await _runService.GoToAsync(EventId, from.PlanItemId, to.PlanItemId, CoachId);

        // Assert
        var properties = _analytics.CapturedOnce(AnalyticsEventNames.PracticeRunStepChanged, CoachId);
        properties["direction"].Should().Be(RunStepDirection.Jump);
        properties["from_order"].Should().Be(fromOrder);
        properties["to_order"].Should().Be(toOrder);
    }

    [Test]
    public async Task GoToAsync_AStaleTap_CapturesNothing()
    {
        // Arrange
        StubInstancePlan();
        StubRun(RunOnFirstItem());

        // Act
        await _runService.GoToAsync(EventId, Item2Id, Item1Id, CoachId);

        // Assert
        _analytics.CapturedNothing();
    }

    private void StubInstancePlan() =>
        _planRepository.Query().Returns(_ => new List<TrainingPlan> { InstancePlan() }.BuildMock());

    private void StubRun(TrainingPlanRun run) =>
        _runRepository.GetByEventIdWithDetailsAsync(EventId).Returns(run);

    private static CreatePlanItemDto WarmUpItem() =>
        new(DrillId: null, SectionId: null, Duration: 15, Notes: null, Kind: ItemKind.Break, Title: "Warm-up");

    private static CreatePlanItemDto BreakItem() =>
        new(DrillId: null, SectionId: null, Duration: 5, Notes: null, Kind: ItemKind.Break, Title: "Water");

    private TrainingPlan Template() => new()
    {
        Id = TemplateId,
        Name = "Tuesday session",
        CreatedByUserId = CoachId,
        PlanType = PlanType.Template,
        Items =
        [
            new PlanItem { TemplateId = TemplateId, Kind = ItemKind.Break, Title = "Warm-up", Order = 1, Duration = 15 },
            new PlanItem { TemplateId = TemplateId, Kind = ItemKind.Break, Title = "Water", Order = 2, Duration = 5 }
        ]
    };

    private static TrainingPlan InstancePlan() => new()
    {
        Id = PlanId,
        Name = "Tuesday session",
        CreatedByUserId = CoachId,
        PlanType = PlanType.Instance,
        EventId = EventId,
        Items =
        [
            new PlanItem { Id = Item1Id, TemplateId = PlanId, Kind = ItemKind.Break, Order = 1, Duration = 5 },
            new PlanItem { Id = Item2Id, TemplateId = PlanId, Kind = ItemKind.Break, Order = 2, Duration = 10 }
        ]
    };

    private List<TrainingPlanRunItem> TwoRunItems() =>
    [
        new() { Id = Guid.NewGuid(), PlanItemId = Item1Id, Order = 1, PlannedDurationSeconds = 300 },
        new() { Id = Guid.NewGuid(), PlanItemId = Item2Id, Order = 2, PlannedDurationSeconds = 600 }
    ];

    private TrainingPlanRun RunOnFirstItem()
    {
        var items = TwoRunItems();
        items[0].StartedAtUtc = Now;
        return NewRun(items, Item1Id);
    }

    private TrainingPlanRun RunOnSecondItem()
    {
        var items = TwoRunItems();
        items[0].StartedAtUtc = Now;
        items[0].CompletedAtUtc = Now;
        items[1].StartedAtUtc = Now;
        return NewRun(items, Item2Id);
    }

    private TrainingPlanRun NewRun(List<TrainingPlanRunItem> items, Guid currentItemId) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = PlanId,
        EventId = EventId,
        StartedByUserId = CoachId,
        Status = RunStatus.Running,
        CurrentItemId = currentItemId,
        CurrentItemStartedAtUtc = Now,
        StartedAtUtc = Now,
        Items = items
    };
}
