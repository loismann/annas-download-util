using Serilog;

namespace AnnasArchive.API.Services;

/// <summary>
/// Enforces locked-showtime cleanup without requiring Mom or Dad to leave a browser
/// open. Request-time advancement remains in DateNightCycleService as a fallback;
/// this worker makes Radarr unmonitoring happen within roughly one minute of the
/// one-hour missed-start or four-hour post-show cutoff.
/// </summary>
public sealed class DateNightLifecycleService(DateNightCycleService cycles) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        var prepareReserve = true;
        do
        {
            try
            {
                await cycles.AdvanceRealShowtimeLifecycleAsync(stoppingToken);
                if (prepareReserve)
                {
                    // Deploy/startup happens well before the next Monday draw in
                    // normal operation. Persist the next unbiased five here so a
                    // login never waits on summary generation.
                    await cycles.PrepareNextDrawAsync(isTest: false, stoppingToken);
                    prepareReserve = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning("[DateNight] Background showtime lifecycle check failed: {Message}", ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
