using Coaching.Application.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace Coaching.BackgroundServices;

/// <summary>
/// Moves on the runs set to auto-advance whose step's time is up, for the phones that are not
/// looking: a locked phone runs no code when its alert fires, so the session goes on here, and
/// every device watching is told. At start, then every interval, it asks which runs are due — one
/// query, and nothing more when none is — and moves each on in a scope of its own. Whatever one
/// sweep meets, the next one still runs.
/// </summary>
public sealed class RunAutoAdvanceService(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    IOptions<RunAutoAdvanceOptions> options,
    ILogger<RunAutoAdvanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval, timeProvider);
        try
        {
            // At once as well as on every tick: a run that fell due while the service was down
            // moves on as soon as it is back.
            do
            {
                await SweepAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass: every run due now moved on, each in its own scope.</summary>
    public async Task SweepAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<Guid> due;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            due = await scope.ServiceProvider.GetRequiredService<IRunService>()
                .GetRunIdsDueToAutoAdvanceAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Auto-advance could not look for due runs; the next sweep will");
            return;
        }

        foreach (var runId in due)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                // A write that lost to a coach's tap stays staged on its context, and must not
                // ride along with the next run's save.
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IRunService>().AutoAdvanceAsync(runId);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Auto-advance failed for run {RunId}; the next sweep will try again", runId);
            }
        }
    }
}
