using Coaching.Application.DTOs.Templates;
using Coaching.Domain.Enums;
using Coaching.Domain.Models.Templates;
using Coaching.Tests.Unit.Analytics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// Two phones control one run. The write that lands second was made from a run that is no
/// longer there; nothing of it is kept, and its phone is told what the run is now — the answer
/// a stale tap gets.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RunConcurrencyTests : RunServiceTestBase
{
    [Test]
    public async Task AdvanceAsync_WhenAnotherPhonesWriteLandedFirst_ReturnsTheRunAsItIsNow()
    {
        // Arrange — this phone read step 1 running; the other paused it before this write landed.
        StubRun(RunningOn(Step1Id, elapsedSeconds: 60));
        LoseTheRaceTo(PausedOn(Step1Id, elapsedSeconds: 60));

        // Act
        var result = await _sut.AdvanceAsync(EventId, Step1Id, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Paused);
        result.CurrentItemId.Should().Be(Step1Id);
        result.CanControl.Should().BeTrue();
        await _broadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
    }

    [Test]
    public async Task PauseAsync_WhenAnotherPhonesWriteLandedFirst_ReturnsTheRunAsItIsNow()
    {
        // Arrange — the other phone moved the run on to step 2.
        StubRun(RunningOn(Step1Id, elapsedSeconds: 60));
        LoseTheRaceTo(RunningOn(Step2Id, elapsedSeconds: 0));

        // Act
        var result = await _sut.PauseAsync(EventId, CreatorId);

        // Assert
        result.Status.Should().Be(RunStatus.Running);
        result.CurrentItemId.Should().Be(Step2Id);
    }

    [Test]
    public async Task CompleteAsync_WhenAnotherPhonesWriteLandedFirst_RecordsNothing()
    {
        // Arrange
        StubRun(RunningOn(Step3Id, elapsedSeconds: 60));
        LoseTheRaceTo(RunningOn(Step3Id, elapsedSeconds: 60));

        // Act
        await _sut.CompleteAsync(EventId, CreatorId);

        // Assert
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task StartAsync_WhenAnotherPhoneStartedTheRunFirst_ReturnsThatRun()
    {
        // Arrange — both phones saw no run; the other's insert took the plan's one run.
        StubNoRun();
        var theirs = RunningOn(Step1Id, elapsedSeconds: 1);
        theirs.StartedByUserId = OtherUserId;
        _runRepository.SaveChangesAsync().ThrowsAsync(new DbUpdateException(
            "insert failed", new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)));
        _runRepository.GetByEventIdWithDetailsNoTrackingAsync(EventId).Returns(theirs);

        // Act
        var result = await _sut.StartAsync(EventId, CreatorId);

        // Assert
        result.Id.Should().Be(theirs.Id);
        result.StartedByUserId.Should().Be(OtherUserId);
        _analytics.CapturedNothing();
        await _broadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
    }

    [Test]
    public async Task AdvanceAsync_WhenTheSaveFailsForAnyOtherReason_Throws()
    {
        // Arrange — only a lost race is an answer; anything else is still a failure.
        StubRun(RunningOn(Step1Id, elapsedSeconds: 60));
        _runRepository.SaveChangesAsync().ThrowsAsync(new DbUpdateException(
            "insert failed", new PostgresException("no such column", "ERROR", "ERROR", PostgresErrorCodes.UndefinedColumn)));

        // Act
        var act = () => _sut.AdvanceAsync(EventId, Step1Id, CreatorId);

        // Assert
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private void LoseTheRaceTo(TrainingPlanRun current)
    {
        _runRepository.SaveChangesAsync().ThrowsAsync(new DbUpdateConcurrencyException("The run changed since it was read."));
        _runRepository.GetByEventIdWithDetailsNoTrackingAsync(EventId).Returns(current);
    }
}
