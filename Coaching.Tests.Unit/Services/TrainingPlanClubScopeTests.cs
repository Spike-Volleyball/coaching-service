using AutoMapper;
using Coaching.Application.DTOs.Templates;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using MockQueryable;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Services.Analytics;
using NSubstitute;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// SPI-5462: browsing a club's training plan templates must verify the requesting user is a
/// member of that club.
/// </summary>
[TestFixture]
[Category("Unit")]
public class TrainingPlanClubScopeTests
{
    private ITrainingPlanRepository _planRepository = null!;
    private IClubsGrpcClient _clubsClient = null!;
    private IMapper _mapper = null!;
    private TrainingPlanService _sut = null!;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ClubId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _planRepository = Substitute.For<ITrainingPlanRepository>();
        _clubsClient = Substitute.For<IClubsGrpcClient>();
        _mapper = Substitute.For<IMapper>();
        _mapper.Map<List<TrainingPlanDto>>(Arg.Any<IEnumerable<TrainingPlan>>())
            .Returns(call => call.Arg<IEnumerable<TrainingPlan>>()
                .Select(p => new TrainingPlanDto { Id = p.Id, Name = p.Name, ClubId = p.ClubId })
                .ToList());

        var dialValues = Substitute.For<IRepository<PlanItemDialValue>>();
        dialValues.Query().Returns(new List<PlanItemDialValue>().BuildMock());

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
            _clubsClient,
            Substitute.For<IEventsGrpcClient>(),
            Substitute.For<IPlanCoachService>(),
            Substitute.For<IPublishEndpoint>(),
            _mapper,
            Substitute.For<ILogger<TrainingPlanService>>(),
            Substitute.For<IAnalyticsCapture>());
    }

    private static TrainingPlan BuildPublicClubPlan(Guid clubId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Club Plan",
        CreatedByUserId = Guid.NewGuid(),
        ClubId = clubId,
        PlanType = PlanType.Template,
        Visibility = TemplateVisibility.Public
    };

    private void StubPlans(params TrainingPlan[] plans) =>
        _planRepository.Query().Returns(plans.ToList().BuildMock());

    [Test]
    public async Task GetClubPlansAsync_Member_ReturnsClubPlans()
    {
        // Arrange
        var plan = BuildPublicClubPlan(ClubId);
        StubPlans(plan);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(true);

        // Act
        var result = await _sut.GetClubPlansAsync(ClubId, UserId, new PlanFilterRequest());

        // Assert
        result.Items.Should().ContainSingle(p => p.Id == plan.Id);
        result.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task GetClubPlansAsync_Member_ReturnsTheClubsPrivateTemplates()
    {
        // Arrange — SPI-6900: private on a club's template keeps it inside the club, not from it.
        var privatePlan = BuildPublicClubPlan(ClubId);
        privatePlan.Visibility = TemplateVisibility.Private;
        var anotherClubsPlan = BuildPublicClubPlan(Guid.NewGuid());
        anotherClubsPlan.Visibility = TemplateVisibility.Private;
        var eventPlan = BuildPublicClubPlan(ClubId);
        eventPlan.Visibility = TemplateVisibility.Private;
        eventPlan.PlanType = PlanType.Instance;
        StubPlans(privatePlan, anotherClubsPlan, eventPlan);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(true);

        // Act
        var result = await _sut.GetClubPlansAsync(ClubId, UserId, new PlanFilterRequest());

        // Assert
        result.Items.Should().ContainSingle(p => p.Id == privatePlan.Id);
        result.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task GetClubPlansAsync_NonMember_ReturnsEmptyWithoutQueryingPlans()
    {
        // Arrange — a foreign club's plans must not leak to a non-member who merely supplies
        // that club's ID.
        var plan = BuildPublicClubPlan(ClubId);
        StubPlans(plan);
        _clubsClient.IsUserClubMemberAsync(UserId, ClubId).Returns(false);

        // Act
        var result = await _sut.GetClubPlansAsync(ClubId, UserId, new PlanFilterRequest { Page = 1, PageSize = 20 });

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        _planRepository.DidNotReceive().Query();
    }
}
