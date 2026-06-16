using System.Net.Http.Json;
using EdgeObservabilityAgent.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EdgeObservabilityAgent.Services;

/// <summary>
/// Background service that periodically checks connectivity to the HQ API
/// and flushes buffered metrics when the link is up.
/// </summary>
public sealed class ResyncService : BackgroundService
{
    private readonly BufferRepository _buffer;
    private readonly HttpClient _http;
    private readonly ILogger<ResyncService> _logger;

    private const string HealthUrl = "http://localhost:5100/health";
    private const string IngestUrl = "http://localhost:5100/ingest";
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(1);

    // ---------------------------------------------------------------------------
    // Public observable state (read by DashboardService)
    // ---------------------------------------------------------------------------

    public bool IsConnected { get; private set; }
    public int PendingCount { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }

    public ResyncService(
        BufferRepository buffer,
        HttpClient http,
        ILogger<ResyncService> logger)
    {
        _buffer = buffer;
        _http = http;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------
    // BackgroundService
    // ---------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ResyncService started. Polling every {Interval}s.", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ResyncService tick error.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    // ---------------------------------------------------------------------------
    // Core tick
    // ---------------------------------------------------------------------------

    private async Task TickAsync(CancellationToken ct)
    {
        PendingCount = await _buffer.GetPendingCountAsync(ct);

        if (!await CheckConnectivityAsync(ct))
        {
            IsConnected = false;
            _logger.LogDebug("Link down - buffering");
            return;
        }

        IsConnected = true;

        if (PendingCount == 0)
            return;

        var rows = await _buffer.GetPendingAsync(BatchSize, ct);
        if (rows.Count == 0)
            return;

        var syncedIds = new List<long>(rows.Count);

        foreach (var (id, payload) in rows)
        {
            try
            {
                var response = await _http.PostAsJsonAsync(IngestUrl, payload, ct);
                if (response.IsSuccessStatusCode)
                    syncedIds.Add(id);
                else
                    _logger.LogWarning("Ingest returned {Status} for row {Id}.", response.StatusCode, id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to POST row {Id} to HQ.", id);
            }
        }

        if (syncedIds.Count > 0)
        {
            await _buffer.MarkSyncedAsync(syncedIds, ct);
            PendingCount = await _buffer.GetPendingCountAsync(ct);
            LastSyncedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Synced {Count} rows to HQ.", syncedIds.Count);
        }
    }

    // ---------------------------------------------------------------------------
    // Connectivity probe
    // ---------------------------------------------------------------------------

    private async Task<bool> CheckConnectivityAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HttpTimeout);

            var response = await _http.GetAsync(HealthUrl, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
