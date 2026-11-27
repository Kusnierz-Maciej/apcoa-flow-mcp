# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Framework guidelines

This server follows the shared guidelines for our unofficial-API MCP servers in **unofficial-mcp-framework**
(`~/GitPersonal/unofficial-mcp-framework`, https://github.com/Kusnierz-Maciej/unofficial-mcp-framework);
listonic-mcp (`~/GitPersonal/listonic-mcp`) is the reference implementation. Start from its `README.md` index and
open only the topic you need.

- Before changing a cross-cutting pattern (auth/OAuth, tool registration and errors, rate limiting, sandbox, live
  contract tests, telemetry, configuration, CI/release), read the matching guideline.
- When apcoa gets a new or better pattern, update the guidelines too: the guideline file, `status/alignment.md`,
  and a new dated, tagged version in its `CHANGELOG.md` with an "Act on:" line per server.

## Debugging API / MCP issues — use ApcoaFlow.Sandbox

The API client is `src/ApcoaFlowApiClient`; what the API does (hosts, sign-in, endpoints, quirks, test parkings) is
in `docs/API-NOTES.md`.

When diagnosing an API or MCP problem (unexpected status codes, odd response bodies, auth/header issues, retry
behaviour), use the **`src/ApcoaFlow.Sandbox`** project. It drives the real API client (same handler chain as
the MCP) and dumps **every HTTP exchange in full** — request line, all headers (including the bearer token), request
body (including the login form), response status, headers and body — so wire behaviour can be inspected directly.

```sh
dotnet run --project src/ApcoaFlow.Sandbox                   # list scenarios
dotnet run --project src/ApcoaFlow.Sandbox -- account         # run one
```

Each run writes `src/ApcoaFlow.Sandbox/out/<UTC-timestamp>/` containing one `NNNN_METHOD_path.http` file per
exchange, plus `run.log` (notes and the client's own log lines) and `meta.txt`. The run prints the dump directory
path at the end — read the `.http` files there to analyse the issue.

- Add a new probe by implementing `IScenario` (in `Scenarios/`) and registering it in `ScenarioRegistry`. Each
  scenario carries a WHAT/WHY/FOUND doc header; write findings as **dated** lines (`FOUND (2026-11-27): …`) so it's
  clear when the API last behaved that way. All read-only so far: `login` (password + refresh grants, e-mail and phone),
  `account` (profile, vehicles, payment methods, sessions, history), `find-parkings` (search, bounding box, parking
  details, outlines), `quotes` (prices, free window), `expired-token` (~66 min, run in the background).
- **When the web app knows a flow we haven't seen**, drive it in the Chrome DevTools MCP: sign in with
  `tools/chrome/login-inject.sh` (manual sign-in there fails on the bot check; the script's header has the steps),
  turn on `guard.js` (blocks writes), capture the calls, then write a sandbox scenario that replays them through
  the client. The scenario is what we keep.
- The dump handler is attached as the innermost handler on both the BFF and the auth-server clients via the
  optional `configureClient` hook on `AddApcoaFlowApiClient`, so it captures each physical send — including logins,
  refreshes and the auth handler's retry after a refused token.
- Credentials: user-secrets id `apcoa-flow-api-tests` (shared with the live tests)
  or `ApcoaFlow__Email` / `ApcoaFlow__Password` env vars.
- User-secrets: `apcoa-flow-api-tests` (live tests + sandbox) and `apcoa-flow-mcp` (host) hold the **same
  contents** (test account + dev `Security:*` keys). When you change one, set the same value in the other.
- Dumps contain **live tokens and the password**; `out/` is gitignored and must never be committed.

### Safety — sessions can be billable

Keep casual probing **read-only**. Write (start / extend / stop) only on a parking with a **dated PLN 0.00 quote**
for the test car (`docs/API-NOTES.md` "Test parkings": Ostrów Wlkp. III outside Sat 06–15, or a Biedronka inside its
free window), and only with the user's OK.

## Checking the MCP server — MCP Inspector CLI

Use the official MCP Inspector in CLI mode (`npx -y @modelcontextprotocol/inspector --cli …`, needs Node) to
check the server as a client sees it: tool list, schemas, annotations, prompts, real tool calls. Run it after any
change to tools, prompts, descriptions, parameters or auth.

**Quick check: `tools/inspector-check.sh`** (add `--live` to also call `get_vehicles` with the live tests' account).
It starts the host on a free port with throwaway dev settings, runs `tools/list --strict` (must be clean) and checks the
expected tool and prompt names and titles; offline it needs no APCOA account. **PR checks run it offline** (job
"MCP Inspector check", Inspector pinned with `INSPECTOR_VERSION`), so update its expected names when tools or prompts
change. The manual recipe below is for anything else.

Start the HTTP host with dev settings (the key and secret are throwaway dev values):

```sh
Security__AllowedApiKeys__0=dev-key \
Security__TokenSecret=dev-token-secret-at-least-32-chars-long \
dotnet run --project src/ApcoaFlowMcp   # launch profile: http://localhost:5200
```

Authenticate with the header path; build the Basic value from the live tests' user-secrets
(`ApcoaFlow:Email` / `ApcoaFlow:Password`, id `apcoa-flow-api-tests`) without printing it:

```sh
LB=$(dotnet user-secrets list --project src/ApcoaFlowApiClient.Tests | python3 -c 'import sys,base64
d=dict(l.rstrip("\n").split(" = ",1) for l in sys.stdin if " = " in l)
print(base64.b64encode((d["ApcoaFlow:Email"]+":"+d["ApcoaFlow:Password"]).encode()).decode())')
I() { npx -y @modelcontextprotocol/inspector --cli http://localhost:5200/mcp \
        --header "X-Api-Key: dev-key" --header "Authorization: Basic $LB" "$@"; }

I --method tools/list --strict          # schema portability: full report on stderr, exit 6 on errors
I --method tools/call --tool-name get_vehicles
```

- `--strict` must stay clean (empty stderr, exit 0) — `ToolSchemaTests` guards the same in unit tests.
- `--tool-arg k=v` sends numeric-looking values as JSON **numbers**; the server also accepts numbers sent as
  strings (and numbers for string parameters), but use `--tool-args-json` when you need exact types.
- A tool error shows as `"isError": true` with the plain message in `content[0].text` (prefixed by the SDK's
  "An error occurred invoking '<tool>': ") and a `tool_is_error` line. A message with **nothing** after the
  colon means an exception escaped `ToolErrors.GuardAsync` or argument binding — check the server log.
- stdio (`--method` must come **before** the server command; in Development the server reads its own
  user-secrets, id `apcoa-flow-mcp`; otherwise pass `-e ApcoaFlow__Email=… -e ApcoaFlow__Password=…`):
  `npx -y @modelcontextprotocol/inspector --cli --method tools/list -e DOTNET_ENVIRONMENT=Development dotnet run --project src/ApcoaFlowMcp -- --stdio 2>/dev/null`
- **Safety:** same as the sandbox — read tools freely; session-starting tools only on a parking with a dated
  PLN 0.00 quote, with the user's OK.
- For the OAuth sign-in page and other UI, use the Inspector web UI (`npx @modelcontextprotocol/inspector`,
  transport Streamable HTTP) — see README.

## Configuration conventions

When adding or changing any setting:

1. **Default in `appsettings.json`** — every key has a sensible default here.
2. **Dev overrides in `appsettings.Development.json`** — only values that differ from the default.
3. **`appsettings.Production.json`** — only values that differ from the default; omit anything that matches.
4. **`docker/docker-compose.prod.yml`** (clean, generic) and **`docker/docker-compose.synology.yml`** (prod + a `simple` console logging block for `docker logs` on the NAS) — keep a commented reference block with default values so operators can see all knobs in one place. Uncomment to override.
5. **Document in README** — every operator-facing setting should be described.

## Live API tests = the API contract

The live tests (`[Category("LiveApi")]` in `src/ApcoaFlowApiClient.Tests`) run against the real, unofficial
APCOA Flow API with the test account (user-secrets id `apcoa-flow-api-tests`); CI runs them nightly against
the latest release.

- **Every API behaviour the code relies on needs a live test** — status codes and error codes (`403
  Unauthenticated`, `400 invalid_grant`, `404 SessionNotFound`, `400 ParkingSessionInvalidArgument`), payload
  shapes and encodings, and silent successes the tools work around (a second stop is 204). When code starts
  depending on a new behaviour: verify it with the sandbox first, add the test (in `Live/`, e.g.
  `AccountContractTests`), and add a row to "Contracts guarded by live tests" in `docs/API-NOTES.md`. A behaviour
  that can't be tested safely (needs a paid start or a one-time free period) goes under "Trusted, not tested"
  there, with the date and how it was observed.
- **Live tests never run in parallel**: every live test class carries `[NotInParallel(LiveApi.Serial)]`
  (one real account, one rate limit, one running session per plate).
- Every live test class also has `[Before(HookType.Class)]` → `LiveApi.SweepOnceAtStartAsync()` (ends any
  session left running — possibly billable — by a crashed previous run; runs once per test run, whichever class
  starts first) and `[After(HookType.Class)]` → `LiveApi.EndActiveSessionsAsync(…)`. Classes that start
  sessions also end them after every test (`[After(Test)]`).
- Use the account's **first registered vehicle** (`LiveApi.FirstRegisteredCar`); never hard-code a plate or id, and
  don't cache account data between tests (tests must not depend on each other).
- Start sessions **only on a parking with a dated PLN 0.00 quote** for that vehicle, checked right before the start:
  Ostrów Wlkp. III by default, else a `search`-found Biedronka inside its free window (once per 4 h per parking:
  rotate). See "Test parkings" in `docs/API-NOTES.md`.
- **Live tests focus on free options** and never rely on paid behaviour or a grace period (assume none: a paid start
  costs its quote). "Biedronka" is the example query for parkings that come with free time; search for them, never
  hard-code one. Paid checks only by hand, with the user's OK each time.
