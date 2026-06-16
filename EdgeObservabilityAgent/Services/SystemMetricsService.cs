using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using EdgeObservabilityAgent.Models;
using Microsoft.Extensions.Logging;

namespace EdgeObservabilityAgent.Services;

/// <summary>
/// Collects CPU, memory, and Docker container metrics from the local host.
/// </summary>
public sealed class SystemMetricsService
{
    private readonly ILogger<SystemMetricsService> _logger;
    private readonly string _nodeId;

    public SystemMetricsService(ILogger<SystemMetricsService> logger)
    {
        _logger = logger;
        _nodeId = Environment.MachineName;
    }

    public async Task<MetricPayload> CollectAsync(CancellationToken ct = default)
    {
        var cpuUsage = await MeasureCpuUsageAsync(ct);
        var (usageMb, totalMb) = GetMemoryInfo();
        var containers = await GetContainersAsync(ct);

        return new MetricPayload
        {
            NodeId = _nodeId,
            Timestamp = DateTimeOffset.UtcNow,
            CpuUsage = cpuUsage,
            MemoryUsageMb = usageMb,
            MemoryTotalMb = totalMb,
            Containers = containers
        };
    }

    // ---------------------------------------------------------------------------
    // CPU
    // ---------------------------------------------------------------------------

    private static async Task<double> MeasureCpuUsageAsync(CancellationToken ct)
    {
        // Take two snapshots 100 ms apart and compare total processor time.
        // This is a per-process estimate; for a host-level reading a native
        // perf counter would be needed, but those are platform-specific.
        var proc = Process.GetCurrentProcess();

        var t1 = DateTime.UtcNow;
        var cpu1 = proc.TotalProcessorTime;

        await Task.Delay(100, ct);

        proc.Refresh();
        var t2 = DateTime.UtcNow;
        var cpu2 = proc.TotalProcessorTime;

        var wallMs = (t2 - t1).TotalMilliseconds;
        var cpuMs = (cpu2 - cpu1).TotalMilliseconds;
        var cores = Environment.ProcessorCount;

        // Percentage across all logical cores
        var pct = wallMs > 0 ? (cpuMs / (wallMs * cores)) * 100.0 : 0.0;
        return Math.Round(Math.Min(pct, 100.0), 2);
    }

    // ---------------------------------------------------------------------------
    // Memory
    // ---------------------------------------------------------------------------

    private static (double usageMb, double totalMb) GetMemoryInfo()
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();

        // Working set of this process as a proxy for node memory usage.
        var usageMb = Math.Round(proc.WorkingSet64 / 1024.0 / 1024.0, 2);

        // Total physical memory via GC info (available on all platforms).
        var totalMb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0, 2);

        return (usageMb, totalMb);
    }

    // ---------------------------------------------------------------------------
    // Docker
    // ---------------------------------------------------------------------------

    private async Task<List<Models.ContainerStatus>> GetContainersAsync(CancellationToken ct)
    {
        try
        {
            using var client = new DockerClientConfiguration().CreateClient();

            var containers = await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true },
                ct);

            return containers.Select(c => new Models.ContainerStatus
            {
                Id = c.ID[..Math.Min(12, c.ID.Length)],
                Name = c.Names.FirstOrDefault()?.TrimStart('/') ?? string.Empty,
                Status = c.Status,
                Image = c.Image
            }).ToList();
        }
        catch (Exception ex)
        {
            // Docker may not be running - this is expected in many environments.
            _logger.LogDebug("Docker unavailable: {Message}", ex.Message);
            return [];
        }
    }
}
