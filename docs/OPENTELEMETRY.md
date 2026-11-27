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

**Logs per tool call** (console and OTLP alike): every tool writes **one line after the call** with ids, counts
and the outcome (the same value as the `apcoa.outcome` span tag), e.g.

```
get_vehicles: 1 vehicle(s) [ABC12345]
```

By default (README settings table) the log also has one start + one finished line per HTTP request
(`Microsoft.AspNetCore.Hosting.Diagnostics`), each APCOA call with status and duration
(`System.Net.Http.HttpClient`, query strings shown as `?*`) and each MCP method with its timing and tool result
(`ModelContextProtocol`). Telemetry export itself stays off unless `Telemetry__Enabled=true`.

## Production

Telemetry is off by default. To enable, set env vars in `docker-compose.prod.yml`:

```yaml
- Telemetry__Enabled=true
- Telemetry__OtlpEndpoint=http://alloy:4317
```

This sends logs and traces to the Alloy container in the logging stack, which forwards
logs to Loki and traces to Tempo.

The container must be able to resolve the collector's name: when Alloy runs in its own compose project, join that
project's shared network. `docker-compose.synology.yml` has this ready, commented out — uncomment both blocks:

```yaml
    networks:
      - default
      - observability          # created by the observability stack
...
networks:
  observability:
    external: true
```

With OTLP export on, don't also ship the container's stdout to Loki (e.g. a `logging=enabled` label for a
Docker log scraper) — the same log lines would arrive twice.

Each trace is built from four span sources:

| Source | Spans |
|---|---|
| ASP.NET Core instrumentation | incoming request (root) — tagged `user.email`; unhandled exceptions recorded. Static icons and `/.well-known/*` are not traced. `/mcp` requests are named after their MCP message, e.g. `POST /mcp [tools/call get_vehicles]`, `POST /mcp [tools/list]`, `POST /mcp [notifications/initialized]` — from the `Mcp-Method` / `Mcp-Name` headers when the client sends them (2026-07-28 Streamable HTTP), else from the parsed JSON-RPC message (`McpSpanNames`; an OTel processor re-applies the name after the instrumentation resets it to `{method} {route}`). The SSE `GET` and session `DELETE` keep `GET /mcp/` / `DELETE /mcp/`. |
| `Experimental.ModelContextProtocol` (MCP SDK) | one per JSON-RPC request, e.g. `tools/call get_vehicles` — tagged `gen_ai.tool.name`, `mcp.method.name`, `mcp.session.id`; tool exceptions set `error.type` |
| `System.Net.Http` (.NET built-in) | each outbound APCOA call (API and sign-in server), **with query string** (redaction disabled) |
| `ApcoaFlow` (app) | `apcoa.login` (password grant, only when there's no usable token) and `apcoa.token_refresh`, each tagged `apcoa.outcome`; `apcoa.login` also `user.email`. Phase spans for the multi-step tools are added with those tools. |

**Business tags** on the tool span (`apcoa.*`):

| Tag | Set by |
|---|---|
| `results.count` | every read tool: parkings, vehicles, sessions found |
| `license_plates` / `parking.ids` / `session.ids` | `get_vehicles` / `find_parkings` / `get_active_sessions` + `get_session_history` (first 20) |
| `license_plate`, `parking.id`, `parking.name` | `get_price`, `get_parking_details`, the start tools |
| `duration_min`, `cost_pln`, `confirmed_cost_pln` | `get_price`, the start tools, `extend_session`; `end_session` (`cost_pln`, final) |
| `session.id` | the start tools (started), `extend_session`, `end_session` |
| `radius_m`, `distance_m` | `start_*_here` |
| `client.name`, `client.version` | every tool call: the calling MCP client (from `_meta` `io.modelcontextprotocol/clientInfo`, else the initialize handshake) |
| `client.trace_context` | every tool call: `meta` (client sent `traceparent` in `params._meta`), `http` (HTTP `traceparent` header) or `none` |
| `client.meta_keys` | every tool call: the `_meta` key **names** the client sent (never values) |
| `outcome` | every tool |

Long lists are capped at 20 values (`ApcoaTelemetry.MaxListedValues`; the count tag has the full size). No
passwords, tokens or payloads are recorded. Plates and the account email are personal data — see below.

**Outcomes** — soft failures (the tool returns normally but did not act) set span status **Error**:

| `apcoa.outcome` | Error? |
|---|---|
| `ok` (read tools), `started`, `extended`, `stopped`, `quote` (paid, awaiting the user's confirmation), `not_free` (free-only tool at a paid parking) | no |
| `not_startable`, `plate_not_registered`, `session_already_active`, `no_payment_method`, `default_not_a_card`, `cost_cap_exceeded`, `cost_mismatch`, `exceeds_max_duration`, `bad_duration`, `payment_action_required`, `session_not_found`, `not_extendable`, `not_stoppable`, `no_parking_nearby`, `ambiguous_location`, `missing_input`, `credentials_rejected` | yes |

API failures that end a tool with an error (`ToolErrors` in `Tools/ToolErrors.cs`) also mark the span
**Error** with one of `credentials_rejected`, `session_not_found`, `session_refused`, `not_found`, `forbidden`,
`rate_limited`, `unavailable`, `rejected_request`; the model gets a plain-language message instead of the SDK's opaque
"An error occurred invoking …".

A refused token (`403 Unauthenticated`, or 401) adds an `apcoa.token_rejected_retrying` event to the tool span
(explains the duplicate HTTP span that follows: the same request re-sent with a renewed token).

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

Over HTTP the server runs **stateless** (the SDK's default session mode), so there is no initialize handshake to
fall back on per request: `client.name` / `client.version` are set only when the client sends `_meta`
`io.modelcontextprotocol/clientInfo` (2026-07-28 stateless protocol) — older clients show none (log line says
`from unknown`; verified 2026-11-27 with the Inspector CLI and the Aspire dashboard). In stdio mode they come from
the handshake.

Useful TraceQL:

```
{ span.gen_ai.tool.name = "get_vehicles" }
{ span.user.email = "someone@example.com" }
{ status = error }
{ span.apcoa.client.trace_context = "meta" }                      # calls that joined a client trace
{ span.apcoa.client.name = "claude-code" }                        # per client (name as the client reports it)
```

> **Personal data:** traces carry the user's email, license plates and (via URLs) space, vehicle and session ids.
> That is intended for a self-hosted stack — don't point `Telemetry:OtlpEndpoint` at a shared or
> third-party backend without revisiting this.

### Why URI query-string redaction is off

The framework default is to keep .NET's redaction of query strings in `System.Net.Http` span URLs
(`url.full`). This server deliberately turns it off (`System.Net.Http.DisableUriRedaction`, set in
`Program.cs` only when telemetry is enabled) because APCOA puts the request's meaning in the query string:
a price quote carries `SpaceId`, `LicensePlateId`, `Duration` and `StartDate`; search carries the text and the
position; the map query carries the box. With redaction on every outbound span reads `…/prices?*` and a wrong quote
can't be traced back to its inputs.

What makes this acceptable: no secrets travel in query strings — credentials go in the sign-in form body, the
bearer token in a header, and neither is recorded. What it costs: plates and ids end up in the trace
backend, which is why telemetry is for a self-hosted stack only (see the personal-data note above). If
that ever changes, remove the switch and rely on the business tags instead.

Telemetry off (`Telemetry:Enabled=false`) costs effectively nothing: with no listener,
`StartActivity` returns `null` and every tag call is a null check.
