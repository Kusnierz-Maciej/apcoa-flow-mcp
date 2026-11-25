# apcoa-flow-mcp

Unofficial MCP server for [APCOA Flow](https://www.apcoa.pl/krotkotrwale-parkowanie/opcje-rezerwacji-i-platnosci/apcoa-flow) parking sessions, built on .NET 10 and ASP.NET Core.

## MCP tools

| Tool | Description |
|---|---|
| `find_parking` | Parkings near a coordinate (lat/lon, radius in metres), optionally filtered by name (e.g. "Carrefour"); nearest first, distances in metres. Returns parking IDs for the start tools. |
| `get_registered_cars` | License plates registered on the account, with the default flagged. |
| `get_active_sessions` | Currently running session(s), or a message if none. |
| `get_session_history` | Recent completed sessions, most recent first, with cost. |
| `start_session` | Start a session at a parking (by id) for a plate. **May cost money.** Free starts immediately. If paid, it does **not** start on the first call: it returns the **shortest available duration and its price**, the other price options, and a note on whether stopping early refunds — then you confirm by calling again with both `durationMinutes` (the option you chose) and the exact `confirmedCostPln`. Resolves lot/tariff automatically, auto-applies a free-time promotion **only when it validates as usable** (filling the free window), and enforces a max-cost cap. |
| `start_free_session` | Start a session at a parking only if it is actually **free** (auto-applies a validated free-time promotion). Never charges. If no free tier is available it starts nothing and instead reports the price options and suggests `start_session`. |
| `start_session_current_location` | Like `start_session` (may cost money), but at the closest parking to a given GPS location within a radius (metres, default 300). |
| `start_free_session_current_location` | Free-only variant of the above — closest parking by location, started only if free. |
| `start_expensive_session` | Same as `start_session` but bypasses the max-cost cap (separate tool so it can be permissioned independently). |
| `end_session` | End an active session by id; returns the final cost. |

Cost, duration and distance are user-facing in PLN, minutes and metres respectively.

See [`docs/MCP-TOOLS.md`](docs/MCP-TOOLS.md) for the detailed tool reference (inputs + example outputs)
and [`docs/API-NOTES.md`](docs/API-NOTES.md) for notes on the underlying APCOA Flow API.

## Running the MCP server

### Docker (production / NAS)

The image is published to GHCR on every release — no build or clone required.

**1. Create a `docker-compose.yml`:**

```yaml
services:
  apcoa-flow-mcp:
    container_name: apcoa-flow-mcp
    image: ghcr.io/kusnierz-maciej/apcoa-flow-mcp:latest   # or pin a version, e.g. :v1.0.0
    ports:
      - "9012:8080"
    env_file:
      # Secrets (API key + optional default credentials). See .env below.
      - .env
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      # Non-secret settings — all optional, defaults shown. Override via the Section__Key convention.
      - ApcoaFlow__MaxSessionCostPln=100
      - ApcoaFlow__CurrentLocationRadiusMeters=300
      - ParkingData__CompanyId=9
      - ParkingData__GeohashPrecision=6
      - ParkingData__SoftRefreshAge=12:00:00
      - ParkingData__HardRefreshAge=48:00:00
      - ParkingData__BackgroundRefreshInterval=24:00:00
      - ParkingData__StorePath=/data/parking
    volumes:
      # Bind mount to a host directory the container can write to (warm-starts the parking cache).
      # It must be writable by the user the container runs as. On a Synology NAS, run as an
      # ACL-trusted host user (see the `user:` line) rather than chowning — Synology ACLs override
      # chmod/chown, so `chown -R 1654` does not grant write.
      - ./data/parking:/data/parking
    # Run as a host user the share's ACL allows to write. On Synology use ContainerManager —
    # find its id with `id ContainerManager` (e.g. 106808:106808). `user: "0:0"` (root) also works.
    user: "106808:106808"
    restart: unless-stopped
```

> The bind-mounted directory caches the parking dataset (downloaded and decrypted from APCOA's public S3 bucket) so the server warm-starts after a restart instead of re-downloading. It is optional — if the directory isn't writable (or you omit the mount) the server still works, serving the dataset from memory and rebuilding from S3 on every start. See [Parking data](#parking-data).

**2. Create a `.env` next to it:**

```
# Required: gates access to the server. Any long random string works — a GUID is a good choice.
# Generate one with:  uuidgen   (macOS/Linux)   or   [guid]::NewGuid()  (PowerShell)
Security__ApiKey=00000000-0000-0000-0000-000000000000

# Optional: a default APCOA Flow account, used only when a client does not send its
# own Authorization: Basic header. Omit both lines if every client passes credentials.
ApcoaFlow__Email=your@email.com
ApcoaFlow__Password=yourpassword
```

> If your password contains `$`, escape each one as `$$` (Docker Compose variable interpolation). Example: `p@$w0rd!` → `p@$$w0rd!`

**3. Start it:**

```bash
docker compose up -d
# Exposed on host port 9012
```

**4. Connect Claude Code:**

```json
{
  "mcpServers": {
    "apcoa-flow": {
      "type": "http",
      "url": "http://your-host:9012/mcp",
      "headers": {
        "X-Api-Key": "your-api-key-from-.env",
        "Authorization": "Basic <base64(email:password)>"
      }
    }
  }
}
```

`X-Api-Key` must match `Security__ApiKey` in your `.env` file.

`Authorization: Basic` carries APCOA Flow credentials encoded as `base64(email:password)`. If omitted, the server falls back to `ApcoaFlow__Email`/`ApcoaFlow__Password` from `.env`. This means multiple users can share one server instance, each passing their own credentials — no server restart required.

### Connecting from claude.ai

1. Go to **claude.ai** → Settings → **Connectors** → **Add custom connector**
2. Enter your server URL, **including the `/mcp` suffix**: `http://your-host:9012/mcp`
3. Under **Authentication**, select **No sign-in**
4. Under **Request headers**, add:
   - `X-Api-Key`: `your-api-key`
   - `Authorization`: `Basic <base64(email:password)>`
5. Click **Add**

The server must be publicly reachable from the internet. If it's on a local NAS, expose it via a tunnel such as [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/) or ngrok.

### Local development

```bash
dotnet user-secrets --project src/ApcoaFlowMcp set "ApcoaFlow:Email" "your@email.com"
dotnet user-secrets --project src/ApcoaFlowMcp set "ApcoaFlow:Password" "yourpassword"

dotnet run --project src/ApcoaFlowMcp
# Server starts at http://localhost:5200
```

No API key is required in development — `Security:ApiKey` is unset so the middleware lets all requests through.

Connect Claude Code by adding to your MCP config (`.mcp.json` in the project root or `~/.claude/mcp.json`):

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

For MCP clients that launch the server as a subprocess, run it with `--stdio`. The server then talks MCP over stdin/stdout instead of HTTP, and logs go to stderr. There is no HTTP endpoint, so `Security__ApiKey` is not used. The APCOA Flow account is read from environment variables:

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

The snapshot lives at `ParkingData:StorePath` (default `/data/parking`). Bind-mount a host directory there (writable by the user the container runs as — see the compose example above) so the index warm-starts after a restart instead of re-downloading from S3. If the path isn't writable the server falls back to serving from memory and rebuilding on each start.

> **Synology NAS:** shares enforce POSIX ACLs that override `chmod`/`chown`, so a `chown -R 1654` on the bind-mount directory does **not** make it writable (the `everyone` ACL entry caps non-listed users at read-only). Instead, run the container as a host user the share's ACL already trusts — on Synology that's the built-in `ContainerManager` account. Get its id with `id ContainerManager` and set it in compose, e.g. `user: "106808:106808"`. `user: "0:0"` (root) also works.

Freshness is tunable via the `ParkingData` configuration section (defaults shown):

```json
{
  "ParkingData": {
    "StorePath": "/data/parking",
    "SoftRefreshAge": "12:00:00",
    "HardRefreshAge": "48:00:00",
    "BackgroundRefreshInterval": "24:00:00",
    "GeohashPrecision": 6,
    "CompanyId": 9
  }
}
```

- **SoftRefreshAge** — past this, a search returns current data immediately and refreshes in the background.
- **HardRefreshAge** — past this, the next search blocks on a rebuild before answering.
- **BackgroundRefreshInterval** — how often the web host proactively rebuilds (not used in stdio mode).
- **GeohashPrecision** — spatial bucket size; 6 ≈ 1.2 × 0.6 km cells.
- **CompanyId** — APCOA tenant used to fetch the dataset decryption key.

Override any of these with environment variables, e.g. `ParkingData__HardRefreshAge=96:00:00`.

## Session settings

Session behaviour is tunable via the `ApcoaFlow` configuration section (defaults shown):

```json
{
  "ApcoaFlow": {
    "MaxSessionCostPln": 100,
    "CurrentLocationRadiusMeters": 300
  }
}
```

- **MaxSessionCostPln** — the most a single `start_session` may cost (PLN). A quote above this is refused; use `start_expensive_session` to bypass.
- **CurrentLocationRadiusMeters** — default search radius for `start_session_current_location` (the tool may override per call).

Override via environment variables, e.g. `ApcoaFlow__MaxSessionCostPln=50`. Credentials (`ApcoaFlow__Email`/`ApcoaFlow__Password`) live in the same section but belong in `.env` / user-secrets, not here.

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

