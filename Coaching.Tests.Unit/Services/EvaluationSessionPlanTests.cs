using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using FluentAssertions;
using NSubstitute;
using Shared.Exceptions;
using Shared.Services.Analytics;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Which plan a session runs. Starting a session builds a score for every player and every exercise
/// of its plan, so the plan is chosen before the start and fixed after it; and a session may only
/// use a plan its coach may use — their own, or one of the session's club they may read.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationSessionPlanTests : UnitTestBase
{
    private IEvaluationSessionRepository _sessions = null!;
    private IEvaluationPlanRepository _plans = null!;
    private IEvaluationAccess _access = null!;
    private EvaluationSessionService _sut = null!;

    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _clubId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _sessions = Substitute.For<IEvaluationSessionRepository>();
        _plans = Substitute.For<IEvaluationPlanRepository>();
        _access = Substitute.For<IEvaluationAccess>();
        _access.MayEvaluateInClubAsync(_clubId, _coachId).Returns(true);
        var mapper = Substitute.For<IMapper>();
        mapper.Map<EvaluationSessionDto>(Arg.Any<EvaluationSession>())
            .Returns(call => new EvaluationSessionDto { Id = call.Arg<EvaluationSession>().Id });
        _sessions.GetByIdWithParticipantsAsync(Arg.Any<Guid>()).Returns(call => new EvaluationSession { Id = call.Arg<Guid>() });

        _sut = new EvaluationSessionService(
            _sessions,
            Substitute.For<IEvaluationParticipantRepository>(),
            _plans,
            Substitute.For<IAnalyticsCapture>(),
            _access,
            mapper);
    }

    private EvaluationSession Session(EvaluationSessionStatus status = EvaluationSessionStatus.Draft, Guid? planId = null)
    {
        var session = new EvaluationSession
        {
            ClubId = _clubId,
            CoachUserId = _coachId,
            Title = "Autumn trials",
            Status = status,
            EvaluationPlanId = planId,
        };
        _sessions.GetByIdAsync(session.Id).Returns(session);
        return session;
    }

    private EvaluationPlan Plan(Guid? clubId, Guid createdBy, bool readable = false)
    {
        var plan = new EvaluationPlan { ClubId = clubId, CreatedByUserId = createdBy, Name = "Serve and pass" };
        _plans.GetByIdAsync(plan.Id).Returns(plan);
        _access.MayReadPlanAsync(plan, _coachId).Returns(readable);
        return plan;
    }

    [Test]
    public async Task UpdateAsync_WithTheCoachsOwnPlan_AttachesIt()
    {
        // Arrange
        var session = Session();
        var plan = Plan(clubId: null, createdBy: _coachId);

        // Act
        await _sut.UpdateAsync(session.Id, new UpdateEvaluationSessionDto { EvaluationPlanId = plan.Id }, _coachId);

        // Assert
        session.EvaluationPlanId.Should().Be(plan.Id);
        await _sessions.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_WithAPlanOfTheSessionsClubTheCoachMayRead_AttachesIt()
    {
        // Arrange
        var session = Session();
        var plan = Plan(clubId: _clubId, createdBy: Guid.NewGuid(), readable: true);

        // Act
        await _sut.UpdateAsync(session.Id, new UpdateEvaluationSessionDto { EvaluationPlanId = plan.Id }, _coachId);

        // Assert
        session.EvaluationPlanId.Should().Be(plan.Id);
    }

    [Test]
    public async Task UpdateAsync_WithAPlanTheSessionMayNotUse_AnswersAsForAMissingPlanAndChangesNothing()
    {
        // Arrange — each one refused the same way, so none of them confirms the plan is real.
        var session = Session();
        var unreadable = Plan(clubId: _clubId, createdBy: Guid.NewGuid(), readable: false);
        var otherClubs = Plan(clubId: Guid.NewGuid(), createdBy: Guid.NewGuid(), readable: true);
        var deleted = Plan(clubId: null, createdBy: _coachId);
        deleted.IsDeleted = true;
        var missingId = Guid.NewGuid();

        // Act & Assert
        var missing = (await ((Func<Task>)(() => _sut.UpdateAsync(
            session.Id, new UpdateEvaluationSessionDto { EvaluationPlanId = missingId }, _coachId)))
            .Should().ThrowAsync<EntityNotFoundException>()).Which;

        foreach (var plan in new[] { unreadable, otherClubs, deleted })
        {
            var act = () => _sut.UpdateAsync(session.Id, new UpdateEvaluationSessionDto { EvaluationPlanId = plan.Id }, _coachId);
            var refusal = (await act.Should().ThrowAsync<EntityNotFoundException>()).Which;
            refusal.Message.Should().Be(missing.Message);
        }

        session.EvaluationPlanId.Should().BeNull();
        await _sessions.DidNotReceive().SaveChangesAsync();
    }

    [TestCase(EvaluationSessionStatus.Running)]
    [TestCase(EvaluationSessionStatus.Paused)]
    [TestCase(EvaluationSessionStatus.Completed)]
    [TestCase(EvaluationSessionStatus.InProgress)]
    public async Task UpdateAsync_ChangingThePlanOnceStarted_ThrowsNamingItAndChangesNothing(EvaluationSessionStatus status)
    {
        // Arrange
        var current = Plan(clubId: null, createdBy: _coachId);
        var session = Session(status, current.Id);
        var next = Plan(clubId: null, createdBy: _coachId);

        // Act
        var act = () => _sut.UpdateAsync(session.Id, new UpdateEvaluationSessionDto { EvaluationPlanId = next.Id }, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("evaluationPlanId");
        session.EvaluationPlanId.Should().Be(current.Id);
        await _sessions.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_EchoingTheCurrentPlanOnceStarted_IsNotAChange()
    {
        // Arrange
        var current = Plan(clubId: null, createdBy: _coachId);
        var session = Session(EvaluationSessionStatus.Running, current.Id);

        // Act
        await _sut.UpdateAsync(session.Id, new UpdateEvaluationSessionDto { Title = "Autumn trials, day two", EvaluationPlanId = current.Id }, _coachId);

        // Assert
        session.Title.Should().Be("Autumn trials, day two");
        session.EvaluationPlanId.Should().Be(current.Id);
    }

    [Test]
    public async Task UpdateAsync_WithADifferentStatus_ThrowsNamingItAndChangesNothing()
    {
        // Arrange — walking a started session back to Draft was a way around the plan rule above,
        // and moving one forward here skipped building the scores Start builds.
        var session = Session(EvaluationSessionStatus.Running);

        // Act
        var act = () => _sut.UpdateAsync(session.Id, new UpdateEvaluationSessionDto { Status = EvaluationSessionStatus.Draft }, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("status");
        session.Status.Should().Be(EvaluationSessionStatus.Running);
        await _sessions.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdateAsync_EchoingTheCurrentStatus_IsNotAChange()
    {
        // Arrange
        var session = Session(EvaluationSessionStatus.Draft);

        // Act
        await _sut.UpdateAsync(session.Id, new UpdateEvaluationSessionDto { Title = "Renamed", Status = EvaluationSessionStatus.Draft }, _coachId);

        // Assert
        session.Title.Should().Be("Renamed");
        await _sessions.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task CreateAsync_WhenTheCoachMayNotEvaluateInTheClub_ThrowsForbiddenAndCreatesNothing()
    {
        // Arrange
        _access.MayEvaluateInClubAsync(_clubId, _coachId).Returns(false);

        // Act
        var act = () => _sut.CreateAsync(new CreateEvaluationSessionDto { ClubId = _clubId, Title = "Autumn trials" }, _coachId);

        // Assert
        await act.Should().ThrowAsync<ForbiddenException>();
        _sessions.DidNotReceive().Add(Arg.Any<EvaluationSession>());
        await _sessions.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task CreateAsync_WhenTheCoachMayEvaluateInTheClub_CreatesADraft()
    {
        // Act
        await _sut.CreateAsync(new CreateEvaluationSessionDto { ClubId = _clubId, Title = "Autumn trials" }, _coachId);

        // Assert
        _sessions.Received(1).Add(Arg.Is<EvaluationSession>(s =>
            s.ClubId == _clubId && s.CoachUserId == _coachId && s.Status == EvaluationSessionStatus.Draft));
        await _sessions.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task CreateAsync_WithAPlanTheSessionMayNotUse_AnswersAsForAMissingPlanAndCreatesNothing()
    {
        // Arrange
        var plan = Plan(clubId: Guid.NewGuid(), createdBy: Guid.NewGuid(), readable: true);

        // Act
        var act = () => _sut.CreateAsync(new CreateEvaluationSessionDto
        {
            ClubId = _clubId,
            EvaluationPlanId = plan.Id,
            Title = "Autumn trials",
        }, _coachId);

        // Assert
        await act.Should().ThrowAsync<EntityNotFoundException>();
        _sessions.DidNotReceive().Add(Arg.Any<EvaluationSession>());
    }
}
