using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Models.Evaluation;
using FluentAssertions;
using NSubstitute;
using Shared.Exceptions;
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
    private IEvaluationGroupRepository _groups = null!;
    private EvaluationAccess _sut = null!;

    private readonly Guid _clubId = Guid.NewGuid();
    private readonly Guid _authorId = Guid.NewGuid();
    private readonly Guid _staffId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();
    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _evaluatorId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _clubs = Substitute.For<IClubsGrpcClient>();
        _clubs.IsClubStaffAsync(_staffId, _clubId).Returns(true);
        _groups = Substitute.For<IEvaluationGroupRepository>();
        _sut = new EvaluationAccess(_clubs, _groups);
    }

    private EvaluationPlan ClubPlan() => new() { ClubId = _clubId, CreatedByUserId = _authorId };

    private EvaluationSession Session()
    {
        var session = new EvaluationSession { ClubId = _clubId, CoachUserId = _coachId, Title = "Autumn trials" };
        _groups.IsEvaluatorAsync(session.Id, _evaluatorId).Returns(true);
        return session;
    }

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

    [Test]
    public async Task EnsureMayReadSessionAsync_ForItsCoach_ReturnsItWithoutAskingFurther()
    {
        // Arrange
        var session = Session();

        // Act
        var result = await _sut.EnsureMayReadSessionAsync(session, _coachId);

        // Assert
        result.Should().BeSameAs(session);
        await _groups.DidNotReceive().IsEvaluatorAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
        await _clubs.DidNotReceive().IsClubStaffAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task EnsureMayReadSessionAsync_ForAnEvaluatorOnOneOfItsGroups_ReturnsIt()
    {
        // Arrange — an evaluator scores from the run screen, which reads the session and its scores.
        var session = Session();

        // Act
        var result = await _sut.EnsureMayReadSessionAsync(session, _evaluatorId);

        // Assert
        result.Should().BeSameAs(session);
    }

    [Test]
    public async Task EnsureMayReadSessionAsync_ForStaffOfItsClub_ReturnsIt()
    {
        // Arrange
        var session = Session();

        // Act
        var result = await _sut.EnsureMayReadSessionAsync(session, _staffId);

        // Assert
        result.Should().BeSameAs(session);
    }

    [Test]
    public async Task EnsureMayReadSessionAsync_ForAStranger_ThrowsTheNotFoundAMissingSessionGives()
    {
        // Arrange
        var session = Session();

        // Act
        var refused = () => _sut.EnsureMayReadSessionAsync(session, _strangerId);
        var missing = () => _sut.EnsureMayReadSessionAsync(null, _strangerId);

        // Assert
        var refusal = (await refused.Should().ThrowAsync<EntityNotFoundException>()).Which;
        var absence = (await missing.Should().ThrowAsync<EntityNotFoundException>()).Which;
        refusal.Message.Should().Be(absence.Message);
    }

    [Test]
    public async Task EnsureMayReadSessionAsync_ForADeletedSession_ThrowsNotFoundEvenForItsCoach()
    {
        // Arrange
        var session = Session();
        session.IsDeleted = true;

        // Act
        var act = () => _sut.EnsureMayReadSessionAsync(session, _coachId);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException>();
    }

    [Test]
    public async Task MayReadExerciseAsync_OutsideAnyClub_ReturnsTrueEvenAnonymously()
    {
        // Arrange — an exercise with no club is the public library's.
        var exercise = new EvaluationExercise { Name = "Serve receive", ClubId = null, CreatedByUserId = _authorId };

        // Act
        var anonymous = await _sut.MayReadExerciseAsync(exercise, null);
        var stranger = await _sut.MayReadExerciseAsync(exercise, _strangerId);

        // Assert
        anonymous.Should().BeTrue();
        stranger.Should().BeTrue();
    }

    [TestCase("author", true)]
    [TestCase("staff", true)]
    [TestCase("stranger", false)]
    [TestCase("anonymous", false)]
    public async Task MayReadExerciseAsync_OfAClub_ReturnsWhetherTheReaderWroteItOrRunsTheClub(string reader, bool expected)
    {
        // Arrange
        var exercise = new EvaluationExercise { Name = "Serve receive", ClubId = _clubId, CreatedByUserId = _authorId };
        Guid? readerId = reader switch
        {
            "author" => _authorId,
            "staff" => _staffId,
            "stranger" => _strangerId,
            _ => null,
        };

        // Act
        var result = await _sut.MayReadExerciseAsync(exercise, readerId);

        // Assert
        result.Should().Be(expected);
    }
}
