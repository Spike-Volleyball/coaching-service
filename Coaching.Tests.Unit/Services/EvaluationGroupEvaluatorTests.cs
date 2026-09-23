using AutoMapper;
using Coaching.Application.DTOs.Evaluation;
using Coaching.Application.Interfaces.Repositories;
using Coaching.Application.Interfaces.Services;
using Coaching.Application.Services;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Evaluation;
using FluentAssertions;
using NSubstitute;
using Shared.DataAccess.Repositories.Interfaces;
using Shared.Exceptions;
using Shared.Testing.Base;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Who may evaluate a group: someone who may evaluate in the session's club — the permission
/// giving feedback there asks — rather than any user id a client sends. An evaluator can be taken
/// off a group while the session is a draft; once it starts, every group needs one.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EvaluationGroupEvaluatorTests : UnitTestBase
{
    private IEvaluationSessionRepository _sessions = null!;
    private IEvaluationGroupRepository _groups = null!;
    private IEvaluationAccess _access = null!;
    private EvaluationGroupService _sut = null!;

    private readonly Guid _clubId = Guid.NewGuid();
    private readonly Guid _coachId = Guid.NewGuid();
    private readonly Guid _eligibleId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();

    [SetUp]
    public override void SetUp()
    {
        base.SetUp();
        _sessions = Substitute.For<IEvaluationSessionRepository>();
        _groups = Substitute.For<IEvaluationGroupRepository>();
        _access = Substitute.For<IEvaluationAccess>();
        _access.MayEvaluateInClubAsync(_clubId, _eligibleId).Returns(true);
        var mapper = Substitute.For<IMapper>();
        mapper.Map<EvaluationGroupDto>(Arg.Any<EvaluationGroup>()).Returns(call => new EvaluationGroupDto { Id = call.Arg<EvaluationGroup>().Id });

        _sut = new EvaluationGroupService(
            _sessions,
            _groups,
            Substitute.For<IEvaluationParticipantRepository>(),
            Substitute.For<IRepository<EvaluationGroupPlayer>>(),
            _access,
            mapper);
    }

    private (EvaluationSession Session, EvaluationGroup Group) SessionWithGroup(
        EvaluationSessionStatus status = EvaluationSessionStatus.Draft, Guid? evaluatorId = null)
    {
        var session = new EvaluationSession { ClubId = _clubId, CoachUserId = _coachId, Title = "Autumn trials", Status = status };
        var group = new EvaluationGroup { SessionId = session.Id, Name = "Court one", EvaluatorUserId = evaluatorId };
        _sessions.GetByIdWithParticipantsAsync(session.Id).Returns(session);
        _groups.GetByIdWithPlayersAsync(group.Id).Returns(group);
        return (session, group);
    }

    [Test]
    public async Task UpdateGroupAsync_AssigningSomeoneWhoMayEvaluateInTheClub_StoresThem()
    {
        // Arrange
        var (session, group) = SessionWithGroup();

        // Act
        await _sut.UpdateGroupAsync(session.Id, group.Id, new UpdateGroupDto { EvaluatorUserId = _eligibleId }, _coachId);

        // Assert
        group.EvaluatorUserId.Should().Be(_eligibleId);
        await _groups.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task UpdateGroupAsync_AssigningSomeoneWhoMayNotEvaluateInTheClub_ThrowsNamingItAndChangesNothing()
    {
        // Arrange
        var (session, group) = SessionWithGroup(evaluatorId: _eligibleId);

        // Act
        var act = () => _sut.UpdateGroupAsync(session.Id, group.Id, new UpdateGroupDto { EvaluatorUserId = _outsiderId }, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("evaluatorUserId");
        group.EvaluatorUserId.Should().Be(_eligibleId);
        await _groups.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task UpdateGroupAsync_EchoingTheCurrentEvaluator_DoesNotAskAgain()
    {
        // Arrange — renaming a group sends its evaluator back; that is not a new assignment.
        var (session, group) = SessionWithGroup(evaluatorId: _outsiderId);

        // Act
        await _sut.UpdateGroupAsync(session.Id, group.Id, new UpdateGroupDto { Name = "Court A", EvaluatorUserId = _outsiderId }, _coachId);

        // Assert
        group.Name.Should().Be("Court A");
        await _access.DidNotReceive().MayEvaluateInClubAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task UpdateGroupAsync_ClearingTheEvaluatorOfADraft_ClearsIt()
    {
        // Arrange
        var (session, group) = SessionWithGroup(evaluatorId: _eligibleId);

        // Act
        await _sut.UpdateGroupAsync(session.Id, group.Id, new UpdateGroupDto { ClearEvaluator = true }, _coachId);

        // Assert
        group.EvaluatorUserId.Should().BeNull();
        await _groups.Received(1).SaveChangesAsync();
    }

    [TestCase(EvaluationSessionStatus.Running)]
    [TestCase(EvaluationSessionStatus.Paused)]
    public async Task UpdateGroupAsync_ClearingTheEvaluatorOnceStarted_ThrowsAndKeepsThem(EvaluationSessionStatus status)
    {
        // Arrange
        var (session, group) = SessionWithGroup(status, evaluatorId: _eligibleId);

        // Act
        var act = () => _sut.UpdateGroupAsync(session.Id, group.Id, new UpdateGroupDto { ClearEvaluator = true }, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("clearEvaluator");
        group.EvaluatorUserId.Should().Be(_eligibleId);
    }

    [Test]
    public async Task UpdateGroupAsync_ClearingAndAssigningAtOnce_ThrowsAndChangesNothing()
    {
        // Arrange
        var (session, group) = SessionWithGroup(evaluatorId: _eligibleId);

        // Act
        var act = () => _sut.UpdateGroupAsync(session.Id, group.Id,
            new UpdateGroupDto { ClearEvaluator = true, EvaluatorUserId = _eligibleId }, _coachId);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
        group.EvaluatorUserId.Should().Be(_eligibleId);
        await _groups.DidNotReceive().SaveChangesAsync();
    }

    [Test]
    public async Task CreateGroupAsync_WithAnEvaluatorWhoMayNotEvaluateInTheClub_ThrowsNamingItAndCreatesNothing()
    {
        // Arrange
        var (session, _) = SessionWithGroup();

        // Act
        var act = () => _sut.CreateGroupAsync(session.Id, new CreateGroupDto { Name = "Court two", EvaluatorUserId = _outsiderId }, _coachId);

        // Assert
        var error = (await act.Should().ThrowAsync<ValidationException>()).Which;
        error.FieldErrors.Select(e => e.Field).Should().Equal("evaluatorUserId");
        _groups.DidNotReceive().Add(Arg.Any<EvaluationGroup>());
    }
}
