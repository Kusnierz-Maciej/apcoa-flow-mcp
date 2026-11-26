# apcoa-flow-mcp

Unofficial MCP server for [APCOA Flow](https://www.apcoa.pl/krotkotrwale-parkowanie/opcje-rezerwacji-i-platnosci/apcoa-flow) parking sessions, built on .NET 10 and ASP.NET Core.

## APCOA Flow API compatibility
![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/Kusnierz-Maciej/apcoa-flow-mcp/verify-api-contract.yml?branch=main&label=APCOA%20api%20compatibility)  
![Dynamic JSON Badge](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.github.com%2Frepos%2FKusnierz-Maciej%2Fapcoa-flow-mcp%2Factions%2Fworkflows%2F429259354%2Fruns%3Fstatus%3Dcompleted%26per_page%3D1&query=%24.workflow_runs%5B0%5D.run_started_at&label=APCOA%20api%20compatibility&cacheSeconds=300)    

## MCP tools

| Tool | Description |
|---|---|
| `find_parking` | Parkings near a coordinate (lat/lon, radius in metres), optionally filtered by name (e.g. "Carrefour"); nearest first, distances in metres. Returns parking IDs for the start tools. |
| `get_registered_cars` | License plates registered on the account, with the default flagged. |
| `get_active_sessions` | Currently running session(s), or a message if none. |
| `get_session_history` | Recent completed sessions, most recent first, with cost. |
| `start_session` | Start a session at a parking (by id) for a plate. **May cost money.** Free starts immediately. If paid, it does **not** start on the first call: it returns the **shortest available duration and its price**, the other price options, and a note on whether stopping early refunds — then you confirm by calling again with both `durationMinutes` (the option you chose) and the exact `confirmedCostPln`. Resolves lot/tariff automatically, auto-applies a free-time promotion **only when it validates as usable** (filling the free window), and when a coupon is consumed reports when it can be used again. Enforces a max-cost cap. |
| `start_free_session` | Start a session at a parking only if it is actually **free** (auto-applies a validated free-time promotion). Never charges. If no free tier is available it starts nothing and instead reports the price options and suggests `start_session`. |
| `start_session_current_location` | Like `start_session` (may cost money), but at the closest parking to a given GPS location within a radius (metres, default 300). |
| `start_free_session_current_location` | Free-only variant of the above — closest parking by location, started only if free. |
| `start_expensive_session` | Same as `start_session` but bypasses the max-cost cap (separate tool so it can be permissioned independently). |
| `end_session` | End an active session by id; returns the final cost. |

Cost, duration and distance are user-facing in PLN, minutes and metres respectively.

See [`docs/MCP-TOOLS.md`](docs/MCP-TOOLS.md) for the detailed tool reference (inputs + example outputs)
and [`docs/API-NOTES.md`](docs/API-NOTES.md) for notes on the underlying APCOA Flow API.

## Running the MCP server

### Transports & authentication

The server runs in one of **two transport modes**, and HTTP mode offers **three authentication methods**:

| Transport | How it's used | Auth methods | APCOA credentials come from |
|---|---|---|---|
| **HTTP** (default) | Remote server; clients connect to `…/mcp` over the network. Serve over **HTTPS** in production. | **1. OAuth 2.1** — client runs the login flow; **2. `X-Api-Key` + `Authorization: Basic`** headers | Per user: the OAuth login screen, or the `Basic` header |
| **stdio** (`--stdio`) | Client spawns the server as a local subprocess; MCP over stdin/stdout. | **3. none** — local process, no gate | A single account from `ApcoaFlow__Email` / `ApcoaFlow__Password` |

The three auth methods:

1. **OAuth 2.1** *(HTTP)* — the standard flow for remote clients, and the **only** option for clients
   that can't send custom headers (e.g. ChatGPT, some claude.ai setups). The client discovers the
   endpoints and opens a login screen where the user enters an **API key** (from
   `Security__AllowedApiKeys`) plus their **own APCOA email + password**. Credentials are validated
   against APCOA at login and then stored only inside an encrypted, client-held session token — never on
   the server. Requires `Security__TokenSecret`. Removing a key from `Security__AllowedApiKeys` and
   restarting ends every session created with it.
2. **`X-Api-Key` + `Authorization: Basic`** *(HTTP)* — for clients that support custom headers. The
   `X-Api-Key` must match a configured key; the `Basic` header carries `base64(email:password)`.
3. **stdio config account** *(stdio only)* — no network, no gate; the single APCOA account is read from
   `ApcoaFlow__Email` / `ApcoaFlow__Password`. OAuth and `X-Api-Key` do not apply in stdio mode.

> HTTP mode has **no default APCOA account** — every HTTP client supplies its own credentials via
> method 1 or 2.
>
> **Disabling the API-key gate:** set `Security__RequireMcpApiKey=false` for a trusted/private
> deployment. The `X-Api-Key` check is dropped and the OAuth login screen no longer asks for an API
> key — APCOA email + password are still required. Default is `true`; while it is on, at least one
> `Security__AllowedApiKeys__N` must be configured or every request is rejected.

### Docker (production / NAS)

The image is published to GHCR on every release — no build or clone required.

**1. Create a `docker-compose.yml`:**

Two starting points live in [`src/ApcoaFlowMcp/docker/`](src/ApcoaFlowMcp/docker/):

- [`docker-compose.prod.yml`](src/ApcoaFlowMcp/docker/docker-compose.prod.yml) — clean, generic
  production setup. Start here for any host.
- [`docker-compose.synology.yml`](src/ApcoaFlowMcp/docker/docker-compose.synology.yml) — the above
  plus Synology fixes: runs as an ACL-trusted host user so the bind mount is writable, and sets a
  human-readable `simple` console logging block.

Copy whichever fits as your `docker-compose.yml`. All settings have sensible defaults built in;
override any of them with environment variables in the compose file (`Section__Key` convention).
Each compose file lists the non-secret keys commented out, so you can see every knob in one place.

> The bind-mounted directory caches the parking dataset (downloaded and decrypted from APCOA's public S3 bucket) so the server warm-starts after a restart instead of re-downloading. It is optional — if the directory isn't writable (or you omit the mount) the server still works, serving the dataset from memory and rebuilding from S3 on every start. See [Parking data](#parking-data).

**2. Create a `.env` next to it:**

```
# Required: API key(s) that gate access. Any long random string works — a GUID is a good choice.
# Generate one with:  uuidgen   (macOS/Linux)   or   [guid]::NewGuid()  (PowerShell)
# Add more keys for more users; removing one and restarting ends every session created with it.
Security__AllowedApiKeys__0=00000000-0000-0000-0000-000000000000

# Required for OAuth: a long, STABLE random secret that encrypts session tokens.
# Changing it invalidates all issued tokens (everyone must re-authorize).
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
API key + APCOA email/password. The session is refreshed silently, so the user does not have to log in
again unless their API key is removed or their APCOA password changes.

**Header auth (for clients that support custom headers):**

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
   - **No sign-in** + **Request headers** `X-Api-Key` and `Authorization: Basic <base64(email:password)>`
     if you prefer the header style.
4. Click **Add**

The server must be publicly reachable from the internet and served over **HTTPS** (the session tokens
carry encrypted credentials). If it's on a local NAS, expose it via a tunnel such as
[Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/) or ngrok — both terminate TLS for you.

### Local development

Run the HTTP host:

```bash
dotnet run --project src/ApcoaFlowMcp
# Server starts at http://localhost:5200 (loopback only)
```

For local work the simplest path is the **header** style — pass an
`Authorization: Basic base64(email:password)` header (and, if you set `Security__AllowedApiKeys`, a
matching `X-Api-Key`). To exercise the **OAuth** flow locally, set an API key and a token secret first:

```bash
dotnet user-secrets --project src/ApcoaFlowMcp set "Security:AllowedApiKeys:0" "dev-key"
dotnet user-secrets --project src/ApcoaFlowMcp set "Security:TokenSecret" "dev-token-secret"
```

then point the [MCP Inspector](https://github.com/modelcontextprotocol/inspector) (or Claude Code) at
`http://localhost:5200/mcp` and complete the login screen with `dev-key` + your APCOA email/password.

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

## Parking data

`find_parking` searches an offline dataset, because APCOA has no live search API — the app downloads static, encrypted parking tiles from a public S3 bucket and searches them on-device. This server does the same: on first use it downloads and decrypts every country tile, builds an in-memory spatial index (~1 MB), and persists a compact snapshot to disk.

The snapshot lives at `ParkingData__StorePath`. Bind-mount a host directory there (writable by the user the container runs as) so the index warm-starts after a restart instead of re-downloading from S3. If the path isn't writable the server falls back to serving from memory and rebuilding on each start.

Freshness and indexing are tunable via `ParkingData__*` environment variables:

- **ParkingData__SoftRefreshAge** — past this, a search returns current data immediately and refreshes in the background.
- **ParkingData__HardRefreshAge** — past this, the next search blocks on a rebuild before answering.
- **ParkingData__BackgroundRefreshInterval** — how often the web host proactively rebuilds (not used in stdio mode).
- **ParkingData__GeohashPrecision** — spatial bucket size; 6 ≈ 1.2 × 0.6 km cells.
- **ParkingData__CompanyId** — APCOA tenant used to fetch the dataset decryption key.

## Session settings

- **ApcoaFlow__MaxSessionCostPln** — the most a single `start_session` may cost (PLN). A quote above this is refused; use `start_expensive_session` to bypass.
- **ApcoaFlow__CurrentLocationRadiusMeters** — default search radius for `start_session_current_location` (the tool may override per call).

## Running tests

Tests use TUnit (run via Microsoft.Testing.Platform) and come in three kinds:

- **Unit** (`ApcoaFlowMcp.Tests`) — the MCP session orchestration (`start_session` free/paid logic,
  promo validation, already-active handling) against a fully mocked API. No credentials, no network.
- **Offline** (`ApcoaFlowApiClient.Tests`, no `LiveApi` category) — the spatial index, the on-disk
  snapshot store, and the tariff/promotion/cost logic. No credentials, no network.
- **Live** (`[Category("LiveApi")]`) — exercise the real APCOA Flow API to catch upstream changes.
  These need credentials and are opt-in; they never run unless you filter them in.

Run every non-live test (unit + offline — fast, no account needed):

```bash
dotnet test --solution src/ApcoaFlowMcp.slnx --treenode-filter '/**[Category!=LiveApi]'
```

Run just the MCP unit tests:

```bash
dotnet test --project src/ApcoaFlowMcp.Tests
```

Credentials for the live tests (never stored in the repo):

```bash
dotnet user-secrets --project src/ApcoaFlowApiClient.Tests set "ApcoaFlow:Email" "your@email.com"
dotnet user-secrets --project src/ApcoaFlowApiClient.Tests set "ApcoaFlow:Password" "yourpassword"
```

Run the live tests (needs credentials):

```bash
dotnet test --project src/ApcoaFlowApiClient.Tests --treenode-filter '/**[Category=LiveApi]'
```

Run a single live test class by name, e.g. the full session lifecycle:

```bash
dotnet test --project src/ApcoaFlowApiClient.Tests --treenode-filter '/*/*/LiveApiSessionTests/*'
```

> **Safety:** `LiveApiSessionTests` starts a real parking session. It only ever commits a session it has quoted as **free** (it searches for a parking whose start and extension both cost 0, re-checks the price as a money-guard before starting, and ends the session afterwards). An `[After(Test)]` hook also sweeps and ends any session left active, so a run can never leave a billable session open.

