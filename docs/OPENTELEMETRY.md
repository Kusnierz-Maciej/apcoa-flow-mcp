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
| `ApcoaFlow` (app) | `parking_index.*` (warm / load / build / tile / scheduled_refresh / background_refresh), `apcoa.login` (only on a token-cache miss). **Phase spans** inside the multi-step tools, so each step's backend calls group together: `apcoa.find_nearest`, `apcoa.resolve_lot`, `apcoa.resolve_consumer`, `apcoa.offers`, `apcoa.promotion.validate`, `apcoa.cancel_note`, `apcoa.quote`, `apcoa.price_ladder`, `apcoa.start`, `apcoa.active_lookup` (after a 424) for the `start_*` tools; `apcoa.stop`, `apcoa.reread_completed` for `end_session` |

**Business tags** on the tool span (`apcoa.*`):

| Tag | Set by |
|---|---|
| `parking.id`, `parking.name`, `lot.id`, `tariff.id` | `start_*` (resolved parking); `end_session` (`parking.name`) |
| `plate` | `start_*`, `end_session` |
| `start.mode`, `start.bypass_cap`, `confirmed_cost_pln` | `start_*` |
| `duration_min`, `cost_pln`, `promotion` | `start_*` (quoted/started); `end_session` (`cost_pln`, final) |
| `session.id` | `start_*` (started), `end_session` |
| `distance_m`, `radius_m` | `*_current_location`; `find_parking` (`radius_m`) |
| `name_filter` | `find_parking` |
| `results.count` | every read tool: parkings found, cars, active sessions, history entries |
| `parking.ids` / `plates` / `session.ids` | `find_parking` / `get_registered_cars` / `get_active_sessions` + `get_session_history` (first 20) |
| `client.name`, `client.version` | every tool call: the calling MCP client (from `_meta` `io.modelcontextprotocol/clientInfo`, else the initialize handshake) |
| `client.trace_context` | every tool call: `meta` (client sent `traceparent` in `params._meta`), `http` (HTTP `traceparent` header) or `none` |
| `client.meta_keys` | every tool call: the `_meta` key **names** the client sent (never values) |
| `outcome` | every tool |

Long lists are capped at 20 values (`ApcoaTelemetry.MaxListedValues`; the count tag has the full size). No
passwords, tokens or payloads are recorded. Plates and the account email are personal data — see below.

**Outcomes** — soft failures (the tool returns normally but did not act) set span status **Error**:

| `apcoa.outcome` | Error? |
|---|---|
| `ok` (read tools), `started`, `stopped`, `quote` (paid, awaiting confirmation), `not_free` (free-only tool at a paid parking) | no |
| `cost_cap_exceeded`, `cost_mismatch`, `plate_not_registered`, `no_bookable_lot`, `no_payment_method`, `no_consumer_profile`, `exceeds_free_window`, `no_parking_nearby`, `session_already_active`, `credentials_rejected` | yes |

API failures that end a tool with an error (`ToolErrors` in `Tools/ToolErrors.cs`) also mark the span
**Error** with one of `credentials_rejected`, `session_not_found`, `not_found`, `forbidden`, `rate_limited`, `unavailable`,
`rejected_request`; the model gets a plain-language message instead of the SDK's opaque
"An error occurred invoking …".

A `401`/`419` token rejection adds an `apcoa.token_rejected_retrying` event to the tool span (explains
the duplicate HTTP span that follows).

**One trace per chat turn.** If the client propagates W3C trace context — `traceparent` in `params._meta`
(SEP-414) or the HTTP `traceparent` header — the server continues the client's trace (the MCP SDK and ASP.NET Core
do that already), so all tool calls of one turn land in **one trace**. `ClientContextFilter` (a call-tool request
filter registered in both hosts) records whether that happened as `apcoa.client.trace_context`, plus a log line per
call: `tools/call <tool> from <client> <version>: trace context <meta|http|none>; _meta keys [...]`. Clients that
don't (`none`) get one trace per tool call; group those by `user.email` and time.

**Observed clients** (2026-11-27, listonic-mcp with the same filter):

| Client | Trace context | Result |
|---|---|---|
| Claude Code 2.1.332, default | `none` | one trace per tool call |
| Claude Code 2.1.332 with its own telemetry on (`CLAUDE_CODE_ENABLE_TELEMETRY=1` + an OTLP traces exporter) | `http` (W3C `traceparent` header) | **all tool calls of one prompt share one trace**, each under its own Claude Code span |
| claude.ai | not tested yet (deploy and check `apcoa.client.trace_context`) | per public reports: no trace context, no conversation id |

Claude Code also sends `claudecode/toolUseId` in `_meta`: an id per tool call, not per conversation.

Useful TraceQL:

```
{ span.gen_ai.tool.name = "find_parking" }
{ span.apcoa.lot.id = 41905 }
{ span.apcoa.cost_pln > 20 }
{ span.user.email = "someone@example.com" }
{ status = error }
{ span.apcoa.client.trace_context = "meta" }                      # calls that joined a client trace
{ span.apcoa.client.name = "claude-code" }                        # per client (name as the client reports it)
```

> **Personal data:** traces carry the user's email, license plates and (via URLs) consumer ids.
> That is intended for a self-hosted stack — don't point `Telemetry:OtlpEndpoint` at a shared or
> third-party backend without revisiting this.

### Why URI query-string redaction is off

The framework default is to keep .NET's redaction of query strings in `System.Net.Http` span URLs
(`url.full`). This server deliberately turns it off (`System.Net.Http.DisableUriRedaction`, set in
`Program.cs` only when telemetry is enabled) because APCOA puts the request's meaning in the query string:
the tariff breakdown carries `parkingLotId`, `tariffId`, the plate (`vehicleNumbwe`), check-in/out times and
the promotion; session lists carry `status` and paging; parking calls carry the lot id. With redaction on
every outbound span reads `…/tariffBreakDownV2?*` and a wrong quote can't be traced back to its inputs.

What makes this acceptable: no secrets travel in query strings — credentials go in the login body, the
bearer token in a header, and neither is recorded. What it costs: plates and ids end up in the trace
backend, which is why telemetry is for a self-hosted stack only (see the personal-data note above). If
that ever changes, remove the switch and rely on the business tags instead.

Telemetry off (`Telemetry:Enabled=false`) costs effectively nothing: with no listener,
`StartActivity` returns `null` and every tag call is a null check.
