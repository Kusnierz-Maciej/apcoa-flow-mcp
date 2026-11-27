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

This sends logs and traces to the Alloy container in the logging stack, which forwards
logs to Loki and traces to Tempo.

Each trace is built from four span sources:

| Source | Spans |
|---|---|
| ASP.NET Core instrumentation | incoming request (root), e.g. `POST /mcp/` — tagged `user.email`; unhandled exceptions recorded. Static icons and `/.well-known/*` are not traced. |
| `Experimental.ModelContextProtocol` (MCP SDK) | one per JSON-RPC request, e.g. `tools/call find_parking` — tagged `gen_ai.tool.name`, `mcp.method.name`, `mcp.session.id`; tool exceptions set `error.type` |
| `System.Net.Http` (.NET built-in) | each outbound APCOA API / S3 call, **with query string** (redaction disabled) |
| `ApcoaFlow` (app) | `parking_index.*` (warm / load / build / tile / scheduled_refresh / background_refresh), `apcoa.login` |

**Business tags** on the tool span (`apcoa.*`): `parking.id`, `parking.name`, `lot.id`, `tariff.id`,
`plate`, `start.mode`, `start.bypass_cap`, `duration_min`, `cost_pln`, `confirmed_cost_pln`,
`promotion`, `session.id`, `distance_m`, and `outcome`.

**Outcomes** — soft failures (the tool returns normally but did not act) set span status **Error**:

| `apcoa.outcome` | Error? |
|---|---|
| `started`, `stopped`, `quote` (paid, awaiting confirmation), `not_free` (free-only tool at a paid parking) | no |
| `cost_cap_exceeded`, `cost_mismatch`, `plate_not_registered`, `no_bookable_lot`, `no_payment_method`, `no_consumer_profile`, `exceeds_free_window`, `no_parking_nearby`, `session_already_active`, `credentials_rejected` | yes |

API failures that end a tool with an error (`ToolErrors` in `Tools/ToolErrors.cs`) also mark the span
**Error** with one of `credentials_rejected`, `not_found`, `forbidden`, `rate_limited`, `unavailable`,
`rejected_request`; the model gets a plain-language message instead of the SDK's opaque
"An error occurred invoking …".

A `401`/`419` token rejection adds an `apcoa.token_rejected_retrying` event to the tool span (explains
the duplicate HTTP span that follows).

Useful TraceQL:

```
{ span.gen_ai.tool.name = "find_parking" }
{ span.apcoa.lot.id = 41905 }
{ span.apcoa.cost_pln > 20 }
{ span.user.email = "someone@example.com" }
{ status = error }
```

> **Personal data:** traces carry the user's email, license plates and (via URLs) consumer ids.
> That is intended for a self-hosted stack — don't point `Telemetry:OtlpEndpoint` at a shared or
> third-party backend without revisiting this.

Telemetry off (`Telemetry:Enabled=false`) costs effectively nothing: with no listener,
`StartActivity` returns `null` and every tag call is a null check.
