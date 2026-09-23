using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Models.Evaluation;
using FluentAssertions;
using NSubstitute;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Evaluation material is for the people who run or coach a club — the criteria its players are
/// assessed against — so reading it asks for standing among the club's staff, plus whoever the
/// material itself names (SPI-6459).
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationAccessTests : UnitTestBase
{
    private IClubsGrpcClient _clubs = null!;
    private EvaluationAccess _sut = null!;

    private readonly Guid _clubId = Guid.NewGuid();
    private readonly Guid _authorId = Guid.NewGuid();
    private readonly Guid _staffId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _clubs = Substitute.For<IClubsGrpcClient>();
        _clubs.IsClubStaffAsync(_staffId, _clubId).Returns(true);
        _sut = new EvaluationAccess(_clubs);
    }

    private EvaluationPlan ClubPlan() => new() { ClubId = _clubId, CreatedByUserId = _authorId };

    [Test]
    public async Task MayReadPlanAsync_ForItsAuthor_ReturnsTrueWithoutAskingTheClub()
    {
        // Act
        var result = await _sut.MayReadPlanAsync(ClubPlan(), _authorId);

        // Assert
        result.Should().BeTrue();
        await _clubs.DidNotReceive().IsClubStaffAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task MayReadPlanAsync_ForStaffOfThePlansClub_ReturnsTrue()
    {
        // Act
        var result = await _sut.MayReadPlanAsync(ClubPlan(), _staffId);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task MayReadPlanAsync_ForSomeoneWithoutStandingInTheClub_ReturnsFalse()
    {
        // Act
        var result = await _sut.MayReadPlanAsync(ClubPlan(), _strangerId);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task MayReadPlanAsync_ForSomeoneElsesPersonalPlan_ReturnsFalseWithoutAskingAnyClub()
    {
        // Arrange
        var personal = new EvaluationPlan { ClubId = null, CreatedByUserId = _authorId };

        // Act
        var result = await _sut.MayReadPlanAsync(personal, _staffId);

        // Assert
        result.Should().BeFalse();
        await _clubs.DidNotReceive().IsClubStaffAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task MayReadClubAsync_AsksWhetherTheReaderIsTheClubsStaff()
    {
        // Act
        var staff = await _sut.MayReadClubAsync(_clubId, _staffId);
        var stranger = await _sut.MayReadClubAsync(_clubId, _strangerId);

        // Assert
        staff.Should().BeTrue();
        stranger.Should().BeFalse();
    }
}
