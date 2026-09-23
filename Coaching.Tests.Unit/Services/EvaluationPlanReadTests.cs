using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Models.Evaluation;
using FluentAssertions;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Reading evaluation plans (SPI-6459): a plan the reader may not see answers exactly as one that
/// is not there, and a club's list shows someone without standing what a club with no plans shows.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationPlanReadTests : UnitTestBase
{
    private IEvaluationPlanRepository _plans = null!;
    private IEvaluationAccess _access = null!;
    private IMapper _mapper = null!;
    private EvaluationPlanService _sut = null!;

    private readonly Guid _readerId = Guid.NewGuid();
    private readonly Guid _clubId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _plans = Substitute.For<IEvaluationPlanRepository>();
        _access = Substitute.For<IEvaluationAccess>();
        _mapper = Substitute.For<IMapper>();
        _mapper.Map<EvaluationPlanDto>(Arg.Any<EvaluationPlan>())
            .Returns(call => new EvaluationPlanDto { Id = call.Arg<EvaluationPlan>().Id });

        _sut = new EvaluationPlanService(
            _plans,
            Substitute.For<IRepository<EvaluationPlanItem>>(),
            Substitute.For<IEvaluationExerciseRepository>(),
            _access,
            _mapper);
    }

    [Test]
    public async Task GetByIdForUserAsync_WhenTheReaderMayReadIt_ReturnsThePlan()
    {
        // Arrange
        var plan = new EvaluationPlan { ClubId = _clubId, CreatedByUserId = Guid.NewGuid() };
        _plans.GetByIdWithItemsAsync(plan.Id).Returns(plan);
        _access.MayReadPlanAsync(plan, _readerId).Returns(true);

        // Act
        var result = await _sut.GetByIdForUserAsync(plan.Id, _readerId);

        // Assert
        result.Id.Should().Be(plan.Id);
    }

    [Test]
    public async Task GetByIdForUserAsync_WhenTheReaderMayNotReadIt_ThrowsTheSameNotFoundAsAMissingPlan()
    {
        // Arrange
        var plan = new EvaluationPlan { ClubId = _clubId, CreatedByUserId = Guid.NewGuid() };
        _plans.GetByIdWithItemsAsync(plan.Id).Returns(plan);
        _access.MayReadPlanAsync(plan, _readerId).Returns(false);
        var missingId = Guid.NewGuid();
        _plans.GetByIdWithItemsAsync(missingId).Returns((EvaluationPlan?)null);

        // Act
        var refused = () => _sut.GetByIdForUserAsync(plan.Id, _readerId);
        var missing = () => _sut.GetByIdForUserAsync(missingId, _readerId);

        // Assert
        var refusal = (await refused.Should().ThrowAsync<EntityNotFoundException>()).Which;
        var absence = (await missing.Should().ThrowAsync<EntityNotFoundException>()).Which;
        refusal.Message.Should().Be(absence.Message);
        refusal.ErrorCode.Should().Be(absence.ErrorCode);
    }

    [Test]
    public async Task GetByClubIdAsync_ForSomeoneWithoutStanding_ReturnsNothingWithoutReadingThePlans()
    {
        // Arrange
        _access.MayReadClubAsync(_clubId, _readerId).Returns(false);

        // Act
        var result = await _sut.GetByClubIdAsync(_clubId, _readerId);

        // Assert
        result.Should().BeEmpty();
        await _plans.DidNotReceive().GetByClubIdAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task GetByClubIdAsync_ForTheClubsStaff_ReturnsItsPlans()
    {
        // Arrange
        _access.MayReadClubAsync(_clubId, _readerId).Returns(true);
        var plans = new List<EvaluationPlan> { new() { ClubId = _clubId }, new() { ClubId = _clubId } };
        _plans.GetByClubIdAsync(_clubId).Returns(plans);
        _mapper.Map<List<EvaluationPlanDto>>(plans)
            .Returns(plans.Select(p => new EvaluationPlanDto { Id = p.Id }).ToList());

        // Act
        var result = await _sut.GetByClubIdAsync(_clubId, _readerId);

        // Assert
        result.Select(p => p.Id).Should().Equal(plans.Select(p => p.Id));
    }
}
