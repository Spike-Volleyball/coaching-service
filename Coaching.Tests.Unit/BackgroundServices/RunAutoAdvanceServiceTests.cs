using Coaching.Application.Interfaces.Services;
using Coaching.BackgroundServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Coaching.Tests.Unit.BackgroundServices;

/// <summary>
/// The auto-advance sweep's loop: at start and then once an interval, one question — which runs
/// are due — and each due run moved on in a scope of its own; nothing it meets stops the next
/// sweep. The clock is a FakeTimeProvider rather than UnitTestBase's frozen one, because the
/// service's timer has to fire when a test moves time on.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RunAutoAdvanceServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private FakeTimeProvider _time = null!;
    private IRunService _runs = null!;
    private SemaphoreSlim _asked = null!;
    private int _scopes;
    private ServiceProvider _provider = null!;
    private RunAutoAdvanceService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero));
        _asked = new SemaphoreSlim(0);
        _scopes = 0;

        _runs = Substitute.For<IRunService>();
        DueRuns();

        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            Interlocked.Increment(ref _scopes);
            return _runs;
        });
        _provider = services.BuildServiceProvider();

        _sut = new RunAutoAdvanceService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _time,
            Options.Create(new RunAutoAdvanceOptions { IntervalSeconds = Interval.TotalSeconds }),
            NullLogger<RunAutoAdvanceService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        _sut.Dispose();
        await _provider.DisposeAsync();
        _asked.Dispose();
    }

    [Test]
    public async Task Start_LooksForDueRunsAtOnce()
    {
        // Act
        await _sut.StartAsync(CancellationToken.None);

        // Assert — a run that fell due while the service was down moves on as soon as it is back
        (await _asked.WaitAsync(Patience)).Should().BeTrue();
    }

    [Test]
    public async Task EachInterval_LooksOnceMoreAndNoSooner()
    {
        // Arrange
        await _sut.StartAsync(CancellationToken.None);
        await AskedAsync();

        // Act
        _time.Advance(Interval - TimeSpan.FromMilliseconds(1));

        // Assert
        _asked.CurrentCount.Should().Be(0, "an idle service asks once an interval and no more");
        _time.Advance(TimeSpan.FromMilliseconds(1));
        (await _asked.WaitAsync(Patience)).Should().BeTrue();
    }

    [Test]
    public async Task ASweep_WithNothingDue_OnlyAsks()
    {
        // Act
        await _sut.StartAsync(CancellationToken.None);
        await AskedAsync();

        // Assert
        await _runs.DidNotReceiveWithAnyArgs().AutoAdvanceAsync(default);
        _scopes.Should().Be(1);
    }

    [Test]
    public async Task ASweep_MovesEachDueRunOnInAScopeOfItsOwn()
    {
        // Arrange
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        DueRuns(first, second);
        var secondMovedOn = SignalWhenMovedOn(second);

        // Act
        await _sut.StartAsync(CancellationToken.None);

        // Assert
        (await secondMovedOn.WaitAsync(Patience)).Should().BeTrue();
        await _runs.Received(1).AutoAdvanceAsync(first);
        _scopes.Should().Be(3, "one to ask which runs are due, and one for each of them");
    }

    [Test]
    public async Task ASweep_WhenOneRunFails_StillMovesTheRestOn()
    {
        // Arrange
        var (failing, next) = (Guid.NewGuid(), Guid.NewGuid());
        DueRuns(failing, next);
        _runs.AutoAdvanceAsync(failing).ThrowsAsync(new InvalidOperationException("The database went away."));
        var nextMovedOn = SignalWhenMovedOn(next);

        // Act
        await _sut.StartAsync(CancellationToken.None);

        // Assert
        (await nextMovedOn.WaitAsync(Patience)).Should().BeTrue();
    }

    [Test]
    public async Task ASweepThatCannotAsk_IsTriedAgainNextInterval()
    {
        // Arrange — the database is away for the first sweep.
        _runs.GetRunIdsDueToAutoAdvanceAsync(default).ReturnsForAnyArgs(
            _ =>
            {
                _asked.Release();
                throw new InvalidOperationException("The database went away.");
            },
            _ =>
            {
                _asked.Release();
                return Task.FromResult<IReadOnlyList<Guid>>([]);
            });
        await _sut.StartAsync(CancellationToken.None);
        await AskedAsync();

        // Act
        _time.Advance(Interval);

        // Assert
        (await _asked.WaitAsync(Patience)).Should().BeTrue();
        _sut.ExecuteTask!.IsCompleted.Should().BeFalse("the sweeps go on");
    }

    [Test]
    public async Task Stop_EndsTheSweeps()
    {
        // Arrange
        await _sut.StartAsync(CancellationToken.None);
        await AskedAsync();

        // Act
        await _sut.StopAsync(CancellationToken.None);
        _time.Advance(Interval);

        // Assert
        _sut.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        _asked.CurrentCount.Should().Be(0);
    }

    private void DueRuns(params Guid[] runIds) =>
        _runs.GetRunIdsDueToAutoAdvanceAsync(default).ReturnsForAnyArgs(_ =>
        {
            _asked.Release();
            return Task.FromResult<IReadOnlyList<Guid>>(runIds);
        });

    private async Task AskedAsync() => (await _asked.WaitAsync(Patience)).Should().BeTrue();

    private SemaphoreSlim SignalWhenMovedOn(Guid runId)
    {
        var movedOn = new SemaphoreSlim(0);
        _runs.AutoAdvanceAsync(runId).Returns(_ =>
        {
            movedOn.Release();
            return Task.CompletedTask;
        });
        return movedOn;
    }
}
