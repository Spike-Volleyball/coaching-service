using Coaching.Application.DTOs.Drills;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Feedback;
using Coaching.Domain.Models.Templates;
using FluentAssertions;
using MockQueryable;
using NSubstitute;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// SPI-6690: a coach's private drill in an event's plan, or attached to a player's feedback, 404ed
/// for the co-coaches and players who met it there. The event or feedback the reader names opens
/// the drill when it really holds it and the reader may read it, by the rule its own GET uses.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DrillReadGrantsTests : UnitTestBase
{
    private static readonly Guid ReaderId = Guid.NewGuid();
    private static readonly Guid CoachId = Guid.NewGuid();
    private static readonly Guid DrillId = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();

    private List<TrainingPlan> _plans = null!;
    private List<Feedback> _feedback = null!;
    private IEventsGrpcClient _events = null!;
    private DrillReadGrants _sut = null!;

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _plans = [];
        _feedback = [];

        var plans = Substitute.For<ITrainingPlanRepository>();
        plans.QueryNoTracking().Returns(_ => _plans.BuildMock());
        var feedback = Substitute.For<IFeedbackRepository>();
        feedback.QueryNoTracking().Returns(_ => _feedback.BuildMock());
        _events = Substitute.For<IEventsGrpcClient>();

        _sut = new DrillReadGrants(plans, feedback, _events);
    }

    [Test]
    public async Task GrantsAsync_DrillInTheEventsPlan_OpensForAParticipant()
    {
        // Arrange
        _plans.Add(EventPlan(DrillId));
        _events.IsEventParticipantAsync(EventId, ReaderId).Returns((true, true));

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(EventId, null));

        // Assert
        granted.Should().BeTrue();
    }

    [Test]
    public async Task GrantsAsync_DrillInTheEventsPlan_OpensForAnEventAdminOffTheRoster()
    {
        // Arrange — a co-host or club coach running the session is not a participant.
        _plans.Add(EventPlan(DrillId));
        _events.IsEventParticipantAsync(EventId, ReaderId).Returns((false, true));
        _events.IsEventAdminAsync(EventId, ReaderId).Returns(true);

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(EventId, null));

        // Assert
        granted.Should().BeTrue();
    }

    [Test]
    public async Task GrantsAsync_DrillInTheEventsPlan_StaysShutForSomeoneNotOnTheEvent()
    {
        // Arrange
        _plans.Add(EventPlan(DrillId));
        _events.IsEventParticipantAsync(EventId, ReaderId).Returns((false, true));
        _events.IsEventAdminAsync(EventId, ReaderId).Returns(false);

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(EventId, null));

        // Assert
        granted.Should().BeFalse();
    }

    [Test]
    public async Task GrantsAsync_DrillInAStationOfTheEventsPlan_Opens()
    {
        // Arrange
        _plans.Add(EventPlanWithStation(DrillId));
        _events.IsEventParticipantAsync(EventId, ReaderId).Returns((true, true));

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(EventId, null));

        // Assert
        granted.Should().BeTrue();
    }

    [Test]
    public async Task GrantsAsync_DrillNotInThatEventsPlan_StaysShutWithoutAskingAboutTheEvent()
    {
        // Arrange — naming an event one is at must not open every drill in the library.
        _plans.Add(EventPlan(Guid.NewGuid()));
        _events.IsEventParticipantAsync(EventId, ReaderId).Returns((true, true));

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(EventId, null));

        // Assert
        granted.Should().BeFalse();
        await _events.DidNotReceiveWithAnyArgs().IsEventParticipantAsync(default, default);
    }

    [Test]
    public async Task GrantsAsync_DrillInAnotherEventsPlan_StaysShut()
    {
        // Arrange
        _plans.Add(EventPlan(DrillId, eventId: Guid.NewGuid()));
        _events.IsEventParticipantAsync(EventId, ReaderId).Returns((true, true));

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(EventId, null));

        // Assert
        granted.Should().BeFalse();
    }

    [Test]
    public async Task GrantsAsync_DrillInADeletedPlan_StaysShut()
    {
        // Arrange
        var plan = EventPlan(DrillId);
        plan.IsDeleted = true;
        _plans.Add(plan);
        _events.IsEventParticipantAsync(EventId, ReaderId).Returns((true, true));

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(EventId, null));

        // Assert
        granted.Should().BeFalse();
    }

    [Test]
    public async Task GrantsAsync_DrillOnFeedback_OpensForItsCoach()
    {
        // Arrange
        var feedback = FeedbackWith(DrillId, recipient: Guid.NewGuid(), shared: false);
        _feedback.Add(feedback);

        // Act
        var granted = await _sut.GrantsAsync(DrillId, CoachId, new DrillReadContext(null, feedback.Id));

        // Assert
        granted.Should().BeTrue();
    }

    [Test]
    public async Task GrantsAsync_DrillOnSharedFeedback_OpensForItsPlayer()
    {
        // Arrange
        var feedback = FeedbackWith(DrillId, recipient: ReaderId, shared: true);
        _feedback.Add(feedback);

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(null, feedback.Id));

        // Assert
        granted.Should().BeTrue();
    }

    [Test]
    public async Task GrantsAsync_DrillOnFeedbackNotYetShared_StaysShutForItsPlayer()
    {
        // Arrange
        var feedback = FeedbackWith(DrillId, recipient: ReaderId, shared: false);
        _feedback.Add(feedback);

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(null, feedback.Id));

        // Assert
        granted.Should().BeFalse();
    }

    [Test]
    public async Task GrantsAsync_DrillOnSomeoneElsesFeedback_StaysShut()
    {
        // Arrange
        var feedback = FeedbackWith(DrillId, recipient: Guid.NewGuid(), shared: true);
        _feedback.Add(feedback);

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(null, feedback.Id));

        // Assert
        granted.Should().BeFalse();
    }

    [Test]
    public async Task GrantsAsync_DrillNotOnThatFeedback_StaysShut()
    {
        // Arrange
        var feedback = FeedbackWith(Guid.NewGuid(), recipient: ReaderId, shared: true);
        _feedback.Add(feedback);

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(null, feedback.Id));

        // Assert
        granted.Should().BeFalse();
    }

    public enum Removed
    {
        Link,
        Point,
        Feedback
    }

    [TestCase(Removed.Link)]
    [TestCase(Removed.Point)]
    [TestCase(Removed.Feedback)]
    public async Task GrantsAsync_DrillTakenOffTheFeedback_StaysShut(Removed removed)
    {
        // Arrange — removal only marks rows deleted, so a detached drill's link is still there.
        var feedback = FeedbackWith(DrillId, recipient: ReaderId, shared: true);
        var point = feedback.ImprovementPoints.Single();
        switch (removed)
        {
            case Removed.Link: point.AttachedDrills.Single().IsDeleted = true; break;
            case Removed.Point: point.IsDeleted = true; break;
            case Removed.Feedback: feedback.IsDeleted = true; break;
        }
        _feedback.Add(feedback);

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(null, feedback.Id));

        // Assert
        granted.Should().BeFalse();
    }

    [Test]
    public async Task GrantsAsync_WithNoContext_StaysShut()
    {
        // Arrange
        _plans.Add(EventPlan(DrillId));
        _feedback.Add(FeedbackWith(DrillId, recipient: ReaderId, shared: true));

        // Act
        var granted = await _sut.GrantsAsync(DrillId, ReaderId, new DrillReadContext(null, null));

        // Assert
        granted.Should().BeFalse();
    }

    private static TrainingPlan EventPlan(Guid drillId, Guid? eventId = null)
    {
        var plan = InstancePlan(eventId ?? EventId);
        plan.Items.Add(new PlanItem { TemplateId = plan.Id, Kind = ItemKind.Break, Title = "Water", Order = 0 });
        plan.Items.Add(new PlanItem { TemplateId = plan.Id, Kind = ItemKind.Drill, DrillId = drillId, Order = 1 });
        return plan;
    }

    private static TrainingPlan EventPlanWithStation(Guid drillId)
    {
        var plan = InstancePlan(EventId);
        var stations = new PlanItem { TemplateId = plan.Id, Kind = ItemKind.Stations, Title = "Stations", Order = 0 };
        var station = new PlanStation { PlanItemId = stations.Id, Name = "Setters", Order = 0 };
        station.Items.Add(new PlanStationItem { StationId = station.Id, Kind = ItemKind.Drill, DrillId = drillId, Order = 0 });
        stations.Stations.Add(station);
        plan.Items.Add(stations);
        return plan;
    }

    private static TrainingPlan InstancePlan(Guid eventId) => new()
    {
        Name = "Practice",
        CreatedByUserId = CoachId,
        PlanType = PlanType.Instance,
        EventId = eventId
    };

    private static Feedback FeedbackWith(Guid drillId, Guid recipient, bool shared)
    {
        var feedback = new Feedback { CoachUserId = CoachId, RecipientUserId = recipient, SharedWithPlayer = shared };
        var point = new ImprovementPoint { FeedbackId = feedback.Id, Description = "Platform", Order = 0 };
        point.AttachedDrills.Add(new ImprovementPointDrill { ImprovementPointId = point.Id, DrillId = drillId });
        feedback.ImprovementPoints.Add(point);
        return feedback;
    }
}
