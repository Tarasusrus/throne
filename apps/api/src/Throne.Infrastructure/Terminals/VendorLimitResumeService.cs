using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Throne.Application.Terminals;

namespace Throne.Infrastructure.Terminals;

/// <summary>
/// Host loop of the vendor-limit sweep (ADR-0055): a fixed-cadence <see cref="PeriodicTimer"/>
/// around <see cref="VendorLimitResumeSweep.RunOnceAsync"/>. The tick is cheap (one
/// <c>tmux has-session</c> per active pause, nothing when there are none), so a 30 s cadence
/// keeps the resume within a minute of the vendor's reset plus the policy grace. Tick failures
/// are logged and the loop continues.
/// </summary>
internal sealed partial class VendorLimitResumeService(
    VendorLimitResumeSweep sweep,
    ILogger<VendorLimitResumeService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "VendorLimitResumeService tick failed; worker continues.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    private async Task RunTickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await sweep.RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogTickFailed(logger, ex);
        }
    }
}
