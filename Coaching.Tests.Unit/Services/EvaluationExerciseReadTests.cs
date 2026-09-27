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
/// Reading one exercise: the public library's open to anyone, a club's only to its author and its
/// staff. A refused reader is answered as for a missing exercise — asked to sign in when anonymous,
/// told it is not there when signed in — so neither learns that the id is real.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationExerciseReadTests : UnitTestBase
{
    private IEvaluationExerciseRepository _exercises = null!;
    private IEvaluationAccess _access = null!;
    private EvaluationExerciseService _sut = null!;

    private readonly Guid _readerId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _exercises = Substitute.For<IEvaluationExerciseRepository>();
        _access = Substitute.For<IEvaluationAccess>();
        var mapper = Substitute.For<IMapper>();
        mapper.Map<EvaluationExerciseDto>(Arg.Any<EvaluationExercise>())
            .Returns(call => new EvaluationExerciseDto { Id = call.Arg<EvaluationExercise>().Id, Name = call.Arg<EvaluationExercise>().Name });

        _sut = new EvaluationExerciseService(
            _exercises,
            Substitute.For<IRepository<EvaluationMetric>>(),
            Substitute.For<IRepository<MetricSkillWeight>>(),
            _access,
            mapper);
    }

    private EvaluationExercise Stored(bool readable)
    {
        var exercise = new EvaluationExercise { Name = "Serve receive", ClubId = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid() };
        _exercises.GetByIdWithMetricsAsync(exercise.Id).Returns(exercise);
        _access.MayReadExerciseAsync(exercise, Arg.Any<Guid>()).Returns(readable);
        return exercise;
    }

    [Test]
    public async Task GetByIdForUserAsync_WhenTheReaderMayReadIt_ReturnsIt()
    {
        // Arrange
        var exercise = Stored(readable: true);

        // Act
        var result = await _sut.GetByIdForUserAsync(exercise.Id, _readerId);

        // Assert
        result.Id.Should().Be(exercise.Id);
    }

    [Test]
    public async Task GetByIdForUserAsync_SignedInAndRefused_ThrowsTheNotFoundAMissingExerciseGives()
    {
        // Arrange
        var exercise = Stored(readable: false);

        // Act
        var refused = () => _sut.GetByIdForUserAsync(exercise.Id, _readerId);
        var missing = () => _sut.GetByIdForUserAsync(Guid.NewGuid(), _readerId);

        // Assert
        var refusal = (await refused.Should().ThrowAsync<EntityNotFoundException>()).Which;
        var absence = (await missing.Should().ThrowAsync<EntityNotFoundException>()).Which;
        refusal.Message.Should().Be(absence.Message);
    }
}
