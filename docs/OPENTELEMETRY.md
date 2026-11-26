# OpenTelemetry

The app exports traces and logs via OTLP when `Telemetry:Enabled` is `true`.
Telemetry is **off by default** in all environments — enable it explicitly when needed.

## Local testing with Aspire Dashboard

Microsoft's Aspire Dashboard is the best local OTLP viewer for .NET — it shows
structured logs with scopes, traces with per-span timings, and correlates both.
No .NET Aspire project required; it runs standalone via the Aspire CLI.

Install once:

```sh
brew install --cask microsoft/aspire/aspire
```

Start the dashboard:

```sh
aspire dashboard run
```

Enable telemetry in `appsettings.Development.json` (already points at `localhost:4317`):

```json
"Telemetry": {
  "Enabled": true,
  "OtlpEndpoint": "http://localhost:4317"
}
```

Then run the app:

```sh
dotnet run --project src/ApcoaFlowMcp
```

Open **http://localhost:18888** → Structured Logs or Traces → select service `apcoa-flow-mcp`.

Each MCP request shows up as a trace. All log lines within the request carry the
same `TraceId` and `RequestId`, so you can filter a single request's logs in one click.

## Production

Telemetry is off by default. To enable, set env vars in `docker-compose.prod.yml`:

```yaml
- Telemetry__Enabled=true
- Telemetry__OtlpEndpoint=http://alloy:4317
```

This sends logs to the Alloy container in the logging stack, which forwards them to Loki.
Traces are discarded (no Tempo). To retain traces, add a Tempo container and wire
`otelcol.exporter.otlp` in `config.alloy`.
