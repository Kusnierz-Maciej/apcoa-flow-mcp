# apcoa-flow-mcp

Unofficial MCP server for [APCOA Flow](https://www.apcoa.pl/krotkotrwale-parkowanie/opcje-rezerwacji-i-platnosci/apcoa-flow) parking sessions, built on .NET 10 and ASP.NET Core.

## APCOA Flow API compatibility
![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/Kusnierz-Maciej/apcoa-flow-mcp/verify-api-contract.yml?branch=main&label=APCOA%20api%20compatibility)  
![Dynamic JSON Badge](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.github.com%2Frepos%2FKusnierz-Maciej%2Fapcoa-flow-mcp%2Factions%2Fworkflows%2F429259354%2Fruns%3Fstatus%3Dcompleted%26per_page%3D1&query=%24.workflow_runs%5B0%5D.run_started_at&label=APCOA%20api%20compatibility&cacheSeconds=300)    

## MCP tools

A **parking** is the place; a **session** is parking there.

| Tool | Description |
|---|---|
| `get_vehicles` | The vehicles (license plates) on the account, with the default flagged and the user's description. |
| `find_parkings` | Parkings by name or text (brand like "Biedronka", town, street, postcode, location code) and/or near a position, through APCOA's own search; near a position nearest first, measured to the parking's outline when it has one. With a plate, the first results also say whether they're free for that car right now and for how long. |
| `get_parking_details` | Everything about one parking: location (address, coordinates, outline), status, how sessions work there (app or plate recognition), usual and longest stay, pricing (tariff table and rules), amenities (EV charging, season tickets, pre-booking). |
| `get_price` | The price of a stay starting now for one of your vehicles (free time included), and how long it can be free. |
| `get_active_sessions` | Running sessions with time left. |
| `get_session_history` | Recent sessions with cost. |
| `start_session` | Start a session. **May cost money**: free starts at once; paid returns the price first (and how long the same price would last) and starts on a second call with the exact `confirmedCostPln`. Under a cost limit; paid only with the account's default saved card. |
| `start_free_session` | Start only if free for the vehicle right now (any free time included); never charges. Defaults to the whole free time. |
| `start_expensive_session` | `start_session` without the cost limit (its own tool so it can be permissioned separately). |
| `start_session_here` / `start_free_session_here` | The same at the parking you're at (nearest by outline or pin); lists the candidates when GPS can't tell two apart. |
| `extend_session` | Make a running session longer; same price confirmation and limit. |
| `end_session` | End a running session and report the final cost. |

Tools that act on a parking or session id also take the parking's name (`parkingName`). It isn't sent to APCOA — it
makes the client's permission prompt show *which* parking. Paid sessions are only ever charged to the account's
**default** payment method, and only when that's a saved card (a known limitation: no 3-D Secure, Apple Pay or
payment pages).

**Prompts** — shortcuts you pick yourself (slash commands in Claude Code, the "+" menu in claude.ai); they never run
on their own. The `plate` argument autocompletes from your vehicles and `parking` from the parkings of your recent
sessions:

| Prompt | Does |
|---|---|
| `park_free_here` | Free session at the parking you're at (confirms first; never starts a paid one) |
| `park_free_at` | Free session at a parking you name, e.g. one you used before (confirms first; never paid) |
| `end_parking` | End the running session after confirming, and report the cost |
| `parking_costs` | What parking cost this month (or another period), from the session history |

They are defaults: if you have your own instructions or skills for APCOA parking, those should win (the prompt text
asks the model to prefer them — a hint, not enforced). Turn prompts off with `Prompts__Enabled=false`.

Cost, duration and distance are user-facing in PLN, minutes and metres respectively.

See [`docs/MCP-TOOLS.md`](docs/MCP-TOOLS.md) for the detailed tool reference (inputs + example outputs)
and [`docs/API-NOTES.md`](docs/API-NOTES.md) for notes on the underlying APCOA Flow API.

### Known limitations

- **Payment**: paid sessions use only the account's **default** payment method, and only when it's a saved card
  charged without any extra step. No 3-D Secure, Apple Pay or hosted payment page; set a saved card as the default in the APCOA Flow app.
- **Sign-in** with e-mail + password needs the e-mail to be verified in the APCOA Flow app.
- **Pre-pay** parkings charge at start and don't refund an early stop.
- **Plate-recognition** parkings start and end sessions by themselves; the start tools refuse there.
- Not supported: promo codes, booking ahead, favourites, adding vehicles or payment methods, EV charging,
  permits/passes/season tickets, receipts or invoices.

## Running the MCP server

### Transports & authentication

The server runs in one of **two transport modes**, and HTTP mode offers **three authentication methods**:

| Transport | How it's used | Auth methods | APCOA credentials come from |
|---|---|---|---|
| **HTTP** (default) | Remote server; clients connect to `…/mcp` over the network. Serve over **HTTPS** in production. | **1. OAuth 2.1** (recommended) — client runs the login flow; **2. `X-Api-Key` + `Authorization: Basic`** headers (legacy) | Per user: the OAuth login screen, or the `Basic` header |
| **stdio** (`--stdio`) | Client spawns the server as a local subprocess; MCP over stdin/stdout. | **3. none** — local process, no gate | A single account from `ApcoaFlow__Email` / `ApcoaFlow__Password` |

The three auth methods:

1. **OAuth 2.1** *(HTTP)* — the standard flow for remote clients, and the **only** option for clients
   that can't send custom headers (e.g. ChatGPT, some claude.ai setups). The client discovers the
   endpoints and opens a login screen where the user enters an **API key** (from
   `Security__AllowedApiKeys`) plus their **own APCOA email + password** (the e-mail must be verified in the
   APCOA Flow app). Credentials are validated
   against APCOA at login and then stored only inside an encrypted, client-held session token — never on
   the server. Requires `Security__TokenSecret` (min 32 chars) — the key that secures those tokens;
   keep it stable, and note that **changing it invalidates every issued token**. Removing a key from
   `Security__AllowedApiKeys` and restarting likewise ends every session created with it. Sessions
   refresh silently; token lifetimes are settings (defaults: code 1 minute, access token 1 hour,
   refresh token 365 days — the refresh lifetime is how long a client may stay idle before signing in
   again): `OAuth__AuthorizationCodeLifetime` (≤ 10 minutes), `OAuth__AccessTokenLifetime`,
   `OAuth__RefreshTokenLifetime` (≥ the access lifetime).
2. **`X-Api-Key` + `Authorization: Basic`** *(HTTP, legacy)* — for clients that support custom headers. The
   `X-Api-Key` must match a configured key; the `Basic` header carries `base64(email:password)`. Still
   supported, but prefer OAuth: with headers your APCOA password sits in the client's config in plain text.
3. **stdio config account** *(stdio only)* — no network, no gate; the single APCOA account is read from
   `ApcoaFlow__Email` / `ApcoaFlow__Password`. OAuth and `X-Api-Key` do not apply in stdio mode.

> HTTP mode has **no default APCOA account** — every HTTP client supplies its own credentials via
> method 1 or 2.
>
> **Disabling the API-key gate:** set `Security__RequireMcpApiKey=false` for a trusted/private
> deployment. The `X-Api-Key` check is dropped and the OAuth login screen no longer asks for an API
> key — APCOA email + password are still required. Default is `true`; while it is on, at least one
> `Security__AllowedApiKeys__N` must be configured or the HTTP server refuses to start.
>
> **Startup validation:** every setting is checked when the server starts. On invalid configuration
> it exits with code 1 and lists every problem on stderr.

### Docker (production / NAS)

The image is published to GHCR on every release — no build or clone required.

[![Docker images](https://img.shields.io/badge/Docker_images-latest-blue?logo=docker)](https://github.com/Kusnierz-Maciej/apcoa-flow-mcp/pkgs/container/apcoa-flow-mcp?tag=latest)

    docker pull ghcr.io/kusnierz-maciej/apcoa-flow-mcp:latest

Each release is also tagged with its version (e.g. `v2.0.0`) — see the
[releases](https://github.com/Kusnierz-Maciej/apcoa-flow-mcp/releases) page.

**1. Create a `docker-compose.yml`:**

Two starting points live in [`src/ApcoaFlowMcp/docker/`](src/ApcoaFlowMcp/docker/):

- [`docker-compose.prod.yml`](src/ApcoaFlowMcp/docker/docker-compose.prod.yml) — clean, generic
  production setup. Start here for any host.
- [`docker-compose.synology.yml`](src/ApcoaFlowMcp/docker/docker-compose.synology.yml) — the above
  plus a human-readable `simple` console logging block for `docker logs` on the NAS.

Copy whichever fits as your `docker-compose.yml`. All settings have sensible defaults built in;
override any of them with environment variables in the compose file (`Section__Key` convention).
Each compose file lists the non-secret keys commented out, so you can see every knob in one place.

> The server keeps no data on disk: no volume is needed.

**2. Create a `.env` next to it:**

```
# Required: API key(s) that gate access. Any long random string works — a GUID is a good choice.
# Generate one with:  uuidgen   (macOS/Linux)   or   [guid]::NewGuid()  (PowerShell)
# Add more keys for more users; removing one and restarting ends every session created with it.
Security__AllowedApiKeys__0=00000000-0000-0000-0000-000000000000

# Required for OAuth: a long (min 32 chars), STABLE random secret — the key that secures session
# tokens. Changing it invalidates all issued tokens (everyone must re-authorize). The server
# refuses to start if it is unset, too short, or left at this placeholder.
Security__TokenSecret=change-me-to-a-long-random-secret
```

> If a value contains `$`, escape each one as `$$` (Docker Compose variable interpolation). Example: `p@$w0rd!` → `p@$$w0rd!`

**3. Start it:**

```bash
docker compose up -d
# Exposed on host port 9012 — serve it over HTTPS (see below)
```

**4. Connect Claude Code:**

The server supports two connection styles — pick whichever your client supports.

**OAuth (recommended; required by clients that don't allow custom headers):**

```json
{
  "mcpServers": {
    "apcoa-flow": {
      "type": "http",
      "url": "https://your-host:9012/mcp"
    }
  }
}
```

The client discovers the OAuth endpoints automatically and opens a **login screen** for the user's
API key + APCOA email/password. If something is wrong, the page says so under the field concerned (the API
key, or email + password) and keeps everything typed — nothing is cleared or sent back by the server. The
session is refreshed silently, so the user does not have to log in
again unless their API key is removed or their APCOA password changes. If the password changes, tool
calls return a plain "reconnect this connector" error — disconnect and connect it again.

**Header auth (legacy — prefer OAuth; the password ends up in the client config):**

```json
{
  "mcpServers": {
    "apcoa-flow": {
      "type": "http",
      "url": "https://your-host:9012/mcp",
      "headers": {
        "X-Api-Key": "your-api-key-from-.env",
        "Authorization": "Basic <base64(email:password)>"
      }
    }
  }
}
```

Multiple users can share one server instance, each passing their own credentials — no server restart required.

### Connecting from claude.ai

1. Go to **claude.ai** → Settings → **Connectors** → **Add custom connector**
2. Enter your server URL, **including the `/mcp` suffix**: `https://your-host:9012/mcp`
3. Under **Authentication**, choose:
   - **OAuth** (recommended) — you'll be taken to the login screen to enter your API key + APCOA
     email/password; or
   - (legacy) **No sign-in** + **Request headers** `X-Api-Key` and `Authorization: Basic <base64(email:password)>`
     — works, but stores your APCOA password in the connector settings.
4. Click **Add**

The server must be publicly reachable from the internet and served over **HTTPS** (the session tokens
carry encrypted credentials). If it's on a local NAS, expose it via a tunnel such as
[Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/) or ngrok — both terminate TLS for you.

> **Behind a reverse proxy / tunnel:** the server honors `X-Forwarded-Proto` and `X-Forwarded-Host`,
> so the OAuth discovery metadata it publishes (issuer, authorize/token/register endpoints) uses the
> public `https://` scheme and host rather than the container's internal `http://…:8080`. Your proxy
> must forward those headers (Cloudflare Tunnel, ngrok, nginx/Traefik/Caddy all do by default). If the
> metadata comes back with `http://` URLs, OAuth client registration from claude.ai will fail — check
> that the proxy forwards `X-Forwarded-Proto`.

### Local development

Run the HTTP host:

```bash
dotnet run --project src/ApcoaFlowMcp
# Server starts at http://localhost:5200 (loopback only)
```

The HTTP host needs a token secret (min 32 chars) and, unless `Security:RequireMcpApiKey` is `false`,
at least one API key — set them once via user-secrets:

```bash
dotnet user-secrets --project src/ApcoaFlowMcp set "Security:AllowedApiKeys:0" "dev-key"
dotnet user-secrets --project src/ApcoaFlowMcp set "Security:TokenSecret" "dev-token-secret-at-least-32-chars-long"
```

Test it with the official [MCP Inspector](https://github.com/modelcontextprotocol/inspector)
(`npx @modelcontextprotocol/inspector`, needs Node): transport **Streamable HTTP**, URL
`http://localhost:5200/mcp`, **Connect** — the sign-in page opens; use `dev-key` + your APCOA account. Claude
Code works the same way, or send the legacy headers (`X-Api-Key: dev-key` +
`Authorization: Basic base64(email:password)`). For stdio:
`npx @modelcontextprotocol/inspector -e ApcoaFlow__Email=… -e ApcoaFlow__Password=… dotnet run --project src/ApcoaFlowMcp -- --stdio`.
The Inspector's CLI mode (scripted `tools/list --strict`, tool calls) is described in `CLAUDE.md`.

To see exactly what goes over the wire, use the diagnostic sandbox — see `CLAUDE.md`
(`dotnet run --project src/ApcoaFlow.Sandbox`). Write tests only on a parking with a dated PLN 0.00 quote for
the test car (see "Test parkings" in [`docs/API-NOTES.md`](docs/API-NOTES.md)); any other start can cost money.

Claude Code config (`.mcp.json` in the project root or `~/.claude/mcp.json`):

```json
{
  "mcpServers": {
    "apcoa-flow": {
      "type": "http",
      "url": "http://localhost:5200/mcp"
    }
  }
}
```

## Stdio mode

For MCP clients that launch the server as a subprocess, run it with `--stdio`. The server then talks MCP over stdin/stdout instead of HTTP, and logs go to stderr. There is no HTTP endpoint, so the API key is not used. The APCOA Flow account is read from environment variables:

```bash
ApcoaFlow__Email=your@email.com ApcoaFlow__Password=yourpassword \
  dotnet run --project src/ApcoaFlowMcp -- --stdio
```

Example client configuration:

```json
{
  "mcpServers": {
    "apcoa-flow": {
      "command": "dotnet",
      "args": ["run", "--project", "src/ApcoaFlowMcp", "--", "--stdio"],
      "env": {
        "ApcoaFlow__Email": "your@email.com",
        "ApcoaFlow__Password": "yourpassword"
      }
    }
  }
}
```

## Settings

Set in the compose file (or `.env` for secrets) using the `Section__Key` convention. Details below the table.

| Setting | Default | |
|---|---|---|
| `Security__AllowedApiKeys__N` | — | MCP API keys (secret). At least one while keys are required. |
| `Security__TokenSecret` | — | Required (secret). ≥ 32 chars, stable; secures OAuth tokens. |
| `Security__RequireMcpApiKey` | `true` | `false` drops the API-key gate (APCOA email + password still required). |
| `OAuth__AuthorizationCodeLifetime` / `__AccessTokenLifetime` / `__RefreshTokenLifetime` | `00:01:00` / `01:00:00` / `365.00:00:00` | Code: how long the client has to swap the one-time code it gets after a successful sign-in for tokens (done automatically within a second; the sign-in form itself has no time limit). Access: lifetime of the token sent on every call (renewed silently). Refresh: how long a client may stay idle before signing in again. |
| `ApcoaFlow__MaxSessionCostPln` | `100` | Most a session may cost (a start, or the session's cost so far plus an extension); `start_expensive_session` bypasses it for starts. |
| `ApcoaFlow__CurrentLocationRadiusMeters` | `300` | Default search radius for the current-location tools. |
| `ApcoaFlow__Email` / `ApcoaFlow__Password` | — | stdio mode only (required there). |
| `Prompts__Enabled` | `true` | `false` removes the MCP prompts (and their completions). |
| `RateLimit__TokenLimit` / `RateLimit__ReplenishmentPeriod` | `10` / `00:00:01` | Client-side limit on APCOA calls: a burst, then one call per period (extra calls wait). Loosen at your own risk. |
| `Telemetry__Enabled` / `Telemetry__OtlpEndpoint` | `false` / `http://localhost:4317` | OpenTelemetry export of traces + logs — see [`docs/OPENTELEMETRY.md`](docs/OPENTELEMETRY.md). |
| `Logging__LogLevel__Default` | `Information` | App logs: one line per tool call, sign-in, APCOA login and token refresh. |
| `Logging__LogLevel__Microsoft.AspNetCore` / `…Microsoft.AspNetCore.Hosting.Diagnostics` | `Warning` / `Information` | Framework noise (routing, result writing) off; one start + one finished line (status, duration) per HTTP request kept. |
| `Logging__LogLevel__System.Net.Http.HttpClient` | `Information` | Each outbound APCOA call with status and duration (4 lines per call; query strings are shown as `?*`). Set `Warning` to quiet it. |
| `Logging__LogLevel__ModelContextProtocol` | `Information` | Each MCP method handled (timing, client name) and every tool result (`IsError`). |
| `Logging__Console__…` | `simple`, single-line, scopes on | Console format; the Synology compose sets it explicitly for `docker logs`. |

## Session settings

- **ApcoaFlow__MaxSessionCostPln** — the most a single session may cost (PLN) unless the user explicitly accepts
  more.
- **ApcoaFlow__CurrentLocationRadiusMeters** — default search radius for the current-location tools (a tool may
  override it per call).

## Prompts

- **Prompts__Enabled** — `true` (default) advertises the MCP prompts above and their argument completions;
  `false` removes them.

## Rate limit

Every call to APCOA (login and token refresh included) goes through one client-side token bucket, so the server
never hammers the backend. Calls over the limit wait rather than fail.

- **RateLimit__TokenLimit** — burst size: calls allowed back to back (default `10`, 1–1000).
- **RateLimit__ReplenishmentPeriod** — one more call is allowed per period once the burst is used
  (default `00:00:01`, i.e. 1 call/s; must be > 0 and ≤ 1 minute). Loosen at your own risk.

## Running tests

Tests use TUnit (run via Microsoft.Testing.Platform) and come in three kinds:

- **Unit** (`ApcoaFlowMcp.Tests`) — tools against a mocked API, tool errors, annotations and schemas, telemetry
  tags, the startup settings validators, and the whole OAuth flow against an in-memory server. No credentials, no
  network.
- **Offline** (`ApcoaFlowApiClient.Tests`, no `LiveApi` category) — the API client against a fake network: sign-in,
  token refresh and re-login, the retry after a refused token, request and response formats. No credentials, no
  network.
- **Live** (`[Category("LiveApi")]`) — exercise the real APCOA Flow API to catch upstream changes. These need
  credentials and are opt-in; they never run unless you filter them in.

Run every non-live test (unit + offline — fast, no account needed):

```bash
dotnet test --solution src/ApcoaFlowMcp.slnx --treenode-filter '/**[Category!=LiveApi]'
```

Credentials for the live tests (never stored in the repo):

```bash
dotnet user-secrets --project src/ApcoaFlowApiClient.Tests set "ApcoaFlow:Email" "your@email.com"
dotnet user-secrets --project src/ApcoaFlowApiClient.Tests set "ApcoaFlow:Password" "yourpassword"
```

The tests and the diagnostic sandbox (`src/ApcoaFlow.Sandbox`) share the user-secrets id `apcoa-flow-api-tests`;
the server's own secrets live under `apcoa-flow-mcp`. Keep the same credentials in both (repeat the commands above
with `--project src/ApcoaFlowMcp`). Environment variables override user-secrets (handy for CI or a one-off run):

```bash
ApcoaFlow__Email=your@email.com ApcoaFlow__Password=yourpassword \
  dotnet test --project src/ApcoaFlowApiClient.Tests --treenode-filter '/**[Category=LiveApi]'
```

**Live tests** are the contract check for the unofficial APCOA Flow API: every API behaviour the server relies on
has one (listed in [`docs/API-NOTES.md`](docs/API-NOTES.md#contracts-guarded-by-live-tests)). They run nightly in
CI against the latest release, and an issue is opened (or updated) when they fail. They:

- run **one at a time** (never in parallel) — one real account, one rate limit;
- focus on **free options**: read-only checks of search, free time in quotes and parking details, plus one free
  session cycle per run on a parking whose dated quote is PLN 0.00 (the reusable test parking, else a parking found
  with the query "Biedronka"; skipped when none is free). Nothing paid, and nothing relies on a grace period;
- use the account's first registered vehicle (never a hard-coded plate);
- end any running session once when the run starts (so a crashed run is cleaned up by the next one) and after each
  test class;
- make one wrong-password login per run.

Pull requests run only the unit + offline tests — including the whole OAuth sign-in flow against an in-memory
server with a mocked APCOA (`OAuthFlowTests`) — plus `tools/inspector-check.sh` in its offline mode: the official MCP
Inspector against the real host (`tools/list --strict`, the expected tools, prompts and titles; Inspector version
pinned in the workflow).
