using System.Collections.Concurrent;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5100");
builder.Logging.SetMinimumLevel(LogLevel.Warning); // keep output clean

var app = builder.Build();

// In-memory store: nodeId → ring of payloads (capped at 500 per node)
var store = new ConcurrentDictionary<string, LinkedList<JsonElement>>();
var storeLock = new object();
const int MaxPerNode = 500;

// ── Health ────────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTimeOffset.UtcNow }));

// ── Ingest ────────────────────────────────────────────────────────────────────
app.MapPost("/ingest", async (HttpRequest request) =>
{
    JsonElement payload;
    try
    {
        payload = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);
    }
    catch
    {
        return Results.BadRequest("Invalid JSON");
    }

    var nodeId = payload.TryGetProperty("nodeId", out var n)
        ? n.GetString() ?? "unknown" : "unknown";

    lock (storeLock)
    {
        var list = store.GetOrAdd(nodeId, _ => new LinkedList<JsonElement>());
        list.AddLast(payload);
        if (list.Count > MaxPerNode)
            list.RemoveFirst();
    }

    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] ingest  node={nodeId}");
    return Results.Ok(new { accepted = true });
});

// ── Latest snapshot per node ──────────────────────────────────────────────────
app.MapGet("/metrics", () =>
{
    var result = new List<object>();
    foreach (var (nodeId, list) in store)
    {
        lock (storeLock)
        {
            if (list.Last is null) continue;
            result.Add(new { nodeId, latest = list.Last.Value, count = list.Count });
        }
    }
    return Results.Ok(result);
});

Console.WriteLine("HQ Collector  http://localhost:5100");
Console.WriteLine("  GET  /health   liveness probe");
Console.WriteLine("  POST /ingest   receive metric payloads");
Console.WriteLine("  GET  /metrics  latest snapshot per node");
Console.WriteLine();

await app.RunAsync();
