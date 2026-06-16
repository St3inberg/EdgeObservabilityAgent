namespace EdgeObservabilityAgent.Models;

/// <summary>
/// Plain C# model mirroring the proto MetricPayload structure.
/// Used for in-process work before/after proto (de)serialisation.
/// </summary>
public sealed class MetricPayload
{
    public string NodeId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public double CpuUsage { get; set; }
    public double MemoryUsageMb { get; set; }
    public double MemoryTotalMb { get; set; }
    public List<ContainerStatus> Containers { get; set; } = [];
}

/// <summary>
/// Plain C# model mirroring the proto ContainerStatus structure.
/// </summary>
public sealed class ContainerStatus
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
}
