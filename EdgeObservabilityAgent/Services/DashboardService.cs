using EdgeObservabilityAgent.Models;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace EdgeObservabilityAgent.Services;

/// <summary>
/// Renders a live terminal dashboard using Spectre.Console.
/// Refreshes every second showing CPU, memory, Docker containers,
/// network status, pending buffer rows, and last sync time.
/// </summary>
public sealed class DashboardService
{
    private readonly ResyncService _resync;
    private readonly ILogger<DashboardService> _logger;

    // Latest snapshot written by the Worker, read here for display.
    private volatile MetricPayload? _latest;

    public DashboardService(ResyncService resync, ILogger<DashboardService> logger)
    {
        _resync = resync;
        _logger = logger;
    }

    /// <summary>Called by Worker to push a fresh metric snapshot.</summary>
    public void UpdateMetrics(MetricPayload payload) => _latest = payload;

    /// <summary>
    /// Runs the live Spectre.Console dashboard until the token is cancelled.
    /// This method blocks the calling thread (run via Task.Run).
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        // Spectre.Console's live display requires an interactive TTY.
        // Fall back to no-op if stdout is redirected (e.g. piped or CI).
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            _logger.LogInformation("Non-interactive terminal detected — live dashboard suppressed.");
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await AnsiConsole.Live(BuildTable())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async ctx =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        ctx.UpdateTarget(BuildTable());
                        ctx.Refresh();
                        await Task.Delay(1_000, ct);
                    }
                });
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard render error.");
        }
    }

    // ---------------------------------------------------------------------------
    // Table construction
    // ---------------------------------------------------------------------------

    private Table BuildTable()
    {
        var snap = _latest;
        var statusColor = _resync.IsConnected ? "green" : "red";
        var statusText  = _resync.IsConnected ? "Connected" : "Link Down";
        var lastSync    = _resync.LastSyncedAt.HasValue
            ? _resync.LastSyncedAt.Value.ToLocalTime().ToString("HH:mm:ss")
            : "never";

        var table = new Table()
            .Title("[bold white] Edge Observability Agent [/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[grey]Metric[/]").Width(22))
            .AddColumn("[grey]Value[/]");

        // ── Metrics ─────────────────────────────────────────────────────────
        if (snap is null)
        {
            table.AddRow("[yellow]Status[/]", "[yellow]Waiting for first sample…[/]");
        }
        else
        {
            var cpuColor = snap.CpuUsage > 80 ? "red" : snap.CpuUsage > 50 ? "yellow" : "green";
            var memPct   = snap.MemoryTotalMb > 0
                ? snap.MemoryUsageMb / snap.MemoryTotalMb * 100.0 : 0;
            var memColor = memPct > 80 ? "red" : memPct > 50 ? "yellow" : "green";

            table.AddRow("Node",       $"[cyan]{Markup.Escape(snap.NodeId)}[/]");
            table.AddRow("Sampled at", snap.Timestamp.ToLocalTime().ToString("HH:mm:ss"));
            table.AddRow("CPU",        $"[{cpuColor}]{snap.CpuUsage:F1} %[/]");
            table.AddRow("Memory",
                $"[{memColor}]{snap.MemoryUsageMb:F0} MB / {snap.MemoryTotalMb:F0} MB  ({memPct:F1} %)[/]");
            table.AddRow("Containers",
                snap.Containers.Count == 0
                    ? "[grey]none / Docker unavailable[/]"
                    : $"[blue]{snap.Containers.Count}[/] running");
        }

        // ── Status ───────────────────────────────────────────────────────────
        table.AddEmptyRow();
        table.AddRow("Network",   $"[{statusColor}]{statusText}[/]");
        table.AddRow("Buffered",  $"[yellow]{_resync.PendingCount}[/] rows");
        table.AddRow("Last sync", lastSync);

        return table;
    }
}
