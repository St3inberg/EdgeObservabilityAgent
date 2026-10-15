# EdgeObservabilityAgent

EdgeObservabilityAgent is a .NET 9 solution for collecting edge-node telemetry, buffering it locally during outages, and forwarding it to a lightweight HQ collector when connectivity is available.

## Solution layout

- `EdgeObservabilityAgent/`  
  Worker service that:
  - collects CPU, memory, and Docker container metrics every 5 seconds
  - stores metrics in a local SQLite buffer (`edge_buffer.db`)
  - attempts periodic resync to HQ (`http://localhost:5100`)
  - renders a live terminal dashboard (interactive terminals only)

- `HqCollector/`  
  Minimal ASP.NET Core API that:
  - exposes `GET /health` for connectivity checks
  - accepts telemetry via `POST /ingest`
  - exposes latest per-node snapshots via `GET /metrics`

## Prerequisites

- .NET SDK 9.0+
- Docker (optional, for container telemetry)

## Build

From repository root:

```bash
dotnet restore EdgeObservabilityAgent.sln
dotnet build EdgeObservabilityAgent.sln
```

## Run locally

Open two terminals.

### 1) Start HQ collector

```bash
dotnet run --project HqCollector/HqCollector.csproj
```

Collector listens on `http://localhost:5100`.

### 2) Start edge agent

```bash
dotnet run --project EdgeObservabilityAgent/EdgeObservabilityAgent.csproj
```

The agent will:
- buffer payloads in SQLite
- detect HQ availability via `/health`
- flush buffered rows to `/ingest` when connected

## API quick reference (HQ collector)

- `GET /health` → liveness check
- `POST /ingest` → accepts JSON metric payload
- `GET /metrics` → returns latest payload and retained count per node

## Data model

Primary payload fields include:

- `nodeId`
- `timestamp`
- `cpuUsage`
- `memoryUsageMb`
- `memoryTotalMb`
- `containers[]` (`id`, `name`, `status`, `image`)

See `EdgeObservabilityAgent/Protos/metrics.proto` for schema details.

## Notes

- Resync polling runs every 2 seconds in batches of up to 50 records.
- Dashboard output is automatically suppressed in non-interactive environments.
