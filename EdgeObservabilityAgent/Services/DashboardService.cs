using EdgeObservabilityAgent.Models;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

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
        try
        {
            await AnsiConsole.Live(BuildLayout())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async ctx =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        ctx.UpdateTarget(BuildLayout());
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
    // Layout construction
    // ---------------------------------------------------------------------------

    private IRenderable BuildLayout()
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Top"),
                new Layout("Bottom"));

        layout["Top"].Update(BuildMetricsPanel());
        layout["Bottom"].Update(BuildStatusPanel());

        return layout;
    }

    private Panel BuildMetricsPanel()
    {
        var snap = _latest;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        if (snap is null)
        {
            table.AddRow("Status", "[yellow]Waiting for first sample...[/]");
        }
        else
        {
            var cpuColor = snap.CpuUsage > 80 ? "red" : snap.CpuUsage > 50 ? "yellow" : "green";
            var memPct = snap.MemoryTotalMb > 0
                ? snap.MemoryUsageMb / snap.MemoryTotalMb * 100.0
                : 0;
            var memColor = memPct > 80 ? "red" : memPct > 50 ? "yellow" : "green";

            table.AddRow("Node", $"[cyan]{Markup.Escape(snap.NodeId)}[/]");
            table.AddRow("Sampled At", snap.Timestamp.ToLocalTime().ToString("HH:mm:ss"));
            table.AddRow("CPU Usage",
                $"[{cpuColor}]{snap.CpuUsage:F1}%[/]");
            table.AddRow("Memory",
                $"[{memColor}]{snap.MemoryUsageMb:F0} MB[/] / {snap.MemoryTotalMb:F0} MB ([{memColor}]{memPct:F1}%[/])");
            table.AddRow("Docker Containers",
                snap.Containers.Count == 0
                    ? "[grey]none / unavailable[/]"
                    : $"[blue]{snap.Containers.Count}[/] running");
        }

        return new Panel(table)
            .Header("[bold white] Edge Observability Agent [/]")
            .Border(BoxBorder.Double)
            .Expand();
    }

    private Panel BuildStatusPanel()
    {
        var statusColor = _resync.IsConnected ? "green" : "red";
        var statusText = _resync.IsConnected ? "Connected" : "Link Down";

        var lastSync = _resync.LastSyncedAt.HasValue
            ? _resync.LastSyncedAt.Value.ToLocalTime().ToString("HH:mm:ss")
            : "[grey]never[/]";

        var grid = new Grid()
            .AddColumn()
            .AddColumn()
            .AddColumn();

        grid.AddRow(
            $"[bold]Network:[/]  [{statusColor}]{statusText}[/]",
            $"[bold]Buffered:[/]  [yellow]{_resync.PendingCount}[/] rows",
            $"[bold]Last Sync:[/]  {lastSync}");

        return new Panel(grid)
            .Header("[bold white] Status [/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }
}
