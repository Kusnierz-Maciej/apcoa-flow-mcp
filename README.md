# apcoa-flow-mcp

Unofficial MCP server for [APCOA Flow](https://flow.apcoa.com) parking sessions, built on .NET 10 and ASP.NET Core.

> **Work in progress.** Tool implementations are placeholders — real API endpoints are being wired up from live traffic captures.

## MCP tools

| Tool | Description |
|---|---|
| `get_parking_zones` | All available parking zones with their IDs |
| `get_active_session` | Currently running parking session, or none |
| `start_session` | Start a parking session at a zone for a license plate |
| `end_session` | End the active parking session |
| `get_session_history` | Past parking sessions in reverse chronological order |

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
      - .env
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
    restart: unless-stopped
```

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
2. Enter your server URL: `http://your-host:9012/mcp`
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

## Running tests

The tests run against the live APCOA Flow API to detect whether the API has changed in a way that would break this server. Credentials are required and are never stored in the repo.

```bash
dotnet user-secrets --project src/ApcoaFlowApiClient.Tests set "ApcoaFlow:Email" "your@email.com"
dotnet user-secrets --project src/ApcoaFlowApiClient.Tests set "ApcoaFlow:Password" "yourpassword"

dotnet test src/ApcoaFlowMcp.slnx
```
