using Coaching.Application.Analytics;
using Coaching.Application.DTOs.Templates;
using Coaching.Domain.Models.Templates;
using Coaching.Tests.Unit.Analytics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coaching.Tests.Unit.Services;

/// <summary>
/// The sweep's work on one run, for the phones that are not looking: a locked phone runs no code
/// when a step's time is up, so the server moves the run on, saves it, and tells both rooms. A
/// coach's write that landed in the meantime stands; the next sweep reads the run again.
/// Steps of 5, 10 and 15 minutes.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RunAutoAdvanceSweepTests : RunServiceTestBase
{
    [Test]
    public async Task AutoAdvanceAsync_ADueRun_SavesItOnTheNextStepOnce()
    {
        // Arrange — 20 seconds past step 1's end.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 320);
        StubRunById(run);

        // Act
        await _sut.AutoAdvanceAsync(run.Id);

        // Assert
        run.CurrentItemId.Should().Be(Step2Id);
        run.CurrentItemStartedAtUtc.Should().Be(Now.AddSeconds(-20));
        await _runRepository.Received(1).SaveChangesAsync();
    }

    [Test]
    public async Task AutoAdvanceAsync_ADueRun_TellsBothRoomsWhereItIsNow()
    {
        // Arrange
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 320);
        StubRunById(run);

        // Act
        await _sut.AutoAdvanceAsync(run.Id);

        // Assert — the broadcaster tells controllers and viewers each their own copy
        await _broadcaster.Received(1).BroadcastRunUpdatedAsync(EventId, Arg.Is<RunDto>(d =>
            d.CurrentItemId == Step2Id
            && d.CurrentItemStartedAt == Now.AddSeconds(-20)
            && d.AutoAdvance));
    }

    [Test]
    public async Task AutoAdvanceAsync_ThroughSeveralSteps_CountsEachAsAnAutomaticStepOfTheSession()
    {
        // Arrange — step 1's five minutes and step 2's ten ran out while nothing was sweeping.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 300 + 600 + 30);
        StubRunById(run);

        // Act
        await _sut.AutoAdvanceAsync(run.Id);

        // Assert
        var steps = _analytics.CapturedEach(AnalyticsEventNames.PracticeRunStepChanged);
        steps.Should().HaveCount(2);
        steps.Should().OnlyContain(s => s.UserId == CreatorId, "nobody tapped: the session's starter is who ran it");
        steps.Select(s => s.Properties["direction"]).Should().OnlyContain(d => (string)d! == RunStepDirection.Auto);
        steps.Select(s => (int)s.Properties["from_order"]!).Should().Equal(1, 2);
        steps.Select(s => (int)s.Properties["to_order"]!).Should().Equal(2, 3);
        steps.Select(s => (int)s.Properties["elapsed_seconds"]!).Should().Equal(300, 600);
        steps.Select(s => (int)s.Properties["planned_seconds"]!).Should().Equal(300, 600);
    }

    [Test]
    public async Task AutoAdvanceAsync_WhenACoachsWriteLandedFirst_DropsItsOwnQuietly()
    {
        // Arrange — a coach paused the run between the sweep's read and its write.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 320);
        StubRunById(run);
        _runRepository.SaveChangesAsync().ThrowsAsync(new DbUpdateConcurrencyException("The run changed since it was read."));

        // Act
        var act = () => _sut.AutoAdvanceAsync(run.Id);

        // Assert
        await act.Should().NotThrowAsync();
        await _broadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
        _analytics.CapturedNothing();
    }

    [Test]
    public async Task AutoAdvanceAsync_WhenTheSaveFailsForAnyOtherReason_Throws()
    {
        // Arrange — only a lost race is expected; anything else is for the sweep to report.
        var run = AutoAdvancingOn(Step1Id, elapsedSeconds: 320);
        StubRunById(run);
        _runRepository.SaveChangesAsync().ThrowsAsync(new DbUpdateException("The database went away."));

        // Act
        var act = () => _sut.AutoAdvanceAsync(run.Id);

        // Assert
        await act.Should().ThrowAsync<DbUpdateException>();
        await _broadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
    }

    [Test]
    public async Task AutoAdvanceAsync_ARunNoLongerDue_WritesNothing()
    {
        // Arrange — due when the sweep asked; a coach paused it before the sweep read it.
        var run = PausedOn(Step1Id, elapsedSeconds: 320);
        run.AutoAdvance = true;
        StubRunById(run);

        // Act
        await _sut.AutoAdvanceAsync(run.Id);

        // Assert
        await _runRepository.DidNotReceive().SaveChangesAsync();
        await _broadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
    }

    [Test]
    public async Task AutoAdvanceAsync_ARunThatIsGone_DoesNothing()
    {
        // Arrange — its plan was deleted, and the run with it.
        _runRepository.GetWithDetailsAsync(Arg.Any<Guid>()).Returns((TrainingPlanRun?)null);

        // Act
        await _sut.AutoAdvanceAsync(Guid.NewGuid());

        // Assert
        await _runRepository.DidNotReceive().SaveChangesAsync();
        await _broadcaster.DidNotReceiveWithAnyArgs().BroadcastRunUpdatedAsync(default, default!);
    }

    [Test]
    public async Task GetRunIdsDueToAutoAdvanceAsync_AsksForTheRunsDueNow()
    {
        // Arrange
        var due = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _runRepository.GetIdsDueToAutoAdvanceAsync(Now, Arg.Any<CancellationToken>()).Returns(due);

        // Act
        var result = await _sut.GetRunIdsDueToAutoAdvanceAsync(CancellationToken.None);

        // Assert
        result.Should().Equal(due);
    }
}
