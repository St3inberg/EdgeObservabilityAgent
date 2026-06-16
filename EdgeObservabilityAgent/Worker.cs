using EdgeObservabilityAgent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EdgeObservabilityAgent;

/// <summary>
/// Primary hosted worker.
/// - Initialises the SQLite buffer on startup.
/// - Collects metrics every 5 seconds and persists them locally.
/// - Kicks off the ResyncService and DashboardService as parallel tasks.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly SystemMetricsService _metrics;
    private readonly BufferRepository _buffer;
    private readonly ResyncService _resync;
    private readonly DashboardService _dashboard;
    private readonly ILogger<Worker> _logger;

    private static readonly TimeSpan CollectInterval = TimeSpan.FromSeconds(5);

    public Worker(
        SystemMetricsService metrics,
        BufferRepository buffer,
        ResyncService resync,
        DashboardService dashboard,
        ILogger<Worker> logger)
    {
        _metrics = metrics;
        _buffer = buffer;
        _resync = resync;
        _dashboard = dashboard;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------
    // BackgroundService
    // ---------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker started. Collecting metrics every {Interval}s.", CollectInterval.TotalSeconds);

        // Run the live dashboard in its own task so it doesn't block collection.
        var dashboardTask = Task.Run(() => _dashboard.RunAsync(stoppingToken), stoppingToken);

        // ResyncService is registered as a hosted service and started by the host,
        // but we wait for its task here so any unhandled exception surfaces.
        // The actual execution is managed by IHostedService; we just observe it.

        // Metric collection loop.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var payload = await _metrics.CollectAsync(stoppingToken);
                await _buffer.SaveAsync(payload, stoppingToken);
                _dashboard.UpdateMetrics(payload);

                _logger.LogDebug(
                    "Collected: CPU={Cpu:F1}% Mem={Mem:F0}MB Containers={C}",
                    payload.CpuUsage, payload.MemoryUsageMb, payload.Containers.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during metric collection.");
            }

            await Task.Delay(CollectInterval, stoppingToken);
        }

        // Await the dashboard task to surface any render errors on shutdown.
        try { await dashboardTask; }
        catch (OperationCanceledException) { /* expected on shutdown */ }

        _logger.LogInformation("Worker stopped.");
    }
}
