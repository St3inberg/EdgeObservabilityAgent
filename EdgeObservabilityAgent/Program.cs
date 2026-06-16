using EdgeObservabilityAgent;
using EdgeObservabilityAgent.Services;
using Spectre.Console;

var builder = Host.CreateApplicationBuilder(args);

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// When running in an interactive terminal the Spectre.Console live dashboard
// is the UI — suppress console log output so it doesn't clobber the display.
// In non-interactive contexts (CI, pipes, this tool) use normal console logs.
if (!AnsiConsole.Profile.Capabilities.Interactive)
{
    builder.Logging.AddConsole();
}

// ---------------------------------------------------------------------------
// HTTP client used by ResyncService to reach the HQ API.
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient<ResyncService>(client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "EdgeObservabilityAgent/1.0");
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------

// Transient: a fresh instance per collection tick keeps CPU measurement clean.
builder.Services.AddTransient<SystemMetricsService>();

// Singleton: one shared SQLite repository for the lifetime of the process.
builder.Services.AddSingleton<BufferRepository>();

// ResyncService needs the singleton HttpClient registered above AND the buffer.
// It is also registered as a hosted service so the host manages its lifetime.
builder.Services.AddSingleton<ResyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResyncService>());

// DashboardService is a singleton so the Worker can push metric updates to it.
builder.Services.AddSingleton<DashboardService>();

// Primary worker - collects metrics and drives the dashboard.
builder.Services.AddHostedService<Worker>();

// ---------------------------------------------------------------------------
// Build and run
// ---------------------------------------------------------------------------
var host = builder.Build();

// Initialise the SQLite schema before any hosted service starts,
// so ResyncService can safely query the DB on its first tick.
await host.Services.GetRequiredService<BufferRepository>().InitAsync();

await host.RunAsync();
