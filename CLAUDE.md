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

When diagnosing an API or MCP problem (unexpected status codes, odd response
bodies, auth/header issues, retry behaviour), use the **`src/ApcoaFlow.Sandbox`**
project. It drives the real API client (same handler chain as the app) and dumps
**every HTTP exchange in full** — request line, all headers (including `X-ApiKey`
and the bearer token), request body, response status, headers and body — so wire
behaviour can be inspected directly instead of guessed from summary logs.

```sh
dotnet run --project src/ApcoaFlow.Sandbox                 # list scenarios
dotnet run --project src/ApcoaFlow.Sandbox -- dump-sessions # run one
```

Each run writes `src/ApcoaFlow.Sandbox/out/<UTC-timestamp>/` containing one
`NNNN_METHOD_path.http` file per exchange, plus `run.log` (free-form notes) and
`meta.txt`. The run prints the dump directory path at the end — read the `.http`
files there to analyse the issue.

- Add a new probe by implementing `IScenario` (in `Scenarios/`) and registering it
  in `ScenarioRegistry`. Each scenario carries a WHAT/WHY/FOUND doc header; write
  findings as **dated** lines (`FOUND (2026-11-27): …`) so it's clear when the API
  last behaved that way. Read-only: `dump-sessions` (account snapshot),
  `investigate-carrefour`, `investigate-aldi` (lots, coupon, tariff breakdowns),
  `dump-tariff` (raw `/tariff` grid). `login-probe` touches no data but makes one
  wrong-password login (APCOA locks after 5 per 2 minutes — run sparingly).
  `probe-silent-successes` and `start-free-aldi` write, but can't charge (free-only
  starts, ended immediately; the former only on 616319).
  `capture-carrefour-paid-start` is **BILLABLE** — real paid start+stop.
- The dump handler is attached as the innermost handler on both the API and the
  login clients via the optional `configureClient` hook on `AddApcoaFlowApiClient`,
  so it captures each physical send — including the login itself and the auth
  handler's 419/401 retry re-send.
- Credentials: user-secrets id `apcoa-flow-api-tests` (shared with the live tests)
  or `ApcoaFlow__Email` / `ApcoaFlow__Password` env vars.
- Dumps contain **live tokens**; `out/` is gitignored and must never be committed.

### Safety — sessions are billable

Keep casual probing **read-only**. Starting / extending / stopping a session is
billable and the test account is limited. Parking **616319** (lot 41905, tariff
108014) is the only target with unlimited free start/stop — use it for any write
test. The seeded `dump-sessions` scenario is strictly read-only.

## Checking the MCP server — MCP Inspector CLI

Use the official MCP Inspector in CLI mode (`npx -y @modelcontextprotocol/inspector --cli …`, needs Node) to
check the server as a client sees it: tool list, schemas, annotations, prompts, real tool calls. Run it after any
change to tools, prompts, descriptions, parameters or auth.

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
I --method tools/call --tool-name get_registered_cars
I --method tools/call --tool-name find_parking --tool-arg latitude=50.4433 longitude=18.863 radiusMeters=500
I --method tools/call --tool-name end_session --tool-args-json '{"sessionId":2000000000}'   # error path
I --method prompts/list
I --method prompts/get --prompt-name park_free_at --prompt-args parking=Carrefour
```

- `--strict` must stay clean (empty stderr, exit 0) — `ToolSchemaTests` guards the same in unit tests.
- `--tool-arg k=v` sends numeric-looking values as JSON **numbers**; the server also accepts numbers sent as
  strings (and numbers for string parameters), but use `--tool-args-json` when you need exact types.
- A tool error shows as `"isError": true` with the plain message in `content[0].text` (prefixed by the SDK's
  "An error occurred invoking '<tool>': ") and a `tool_is_error` line. A message with **nothing** after the
  colon means an exception escaped `ToolErrors.GuardAsync` or argument binding — check the server log.
- The CLI has no `completion/complete`; test completions with a raw JSON-RPC POST to `/mcp` (same headers,
  `Accept: application/json, text/event-stream`), e.g. params
  `{"ref":{"type":"ref/prompt","name":"park_free_at"},"argument":{"name":"plate","value":"S"}}`.
- stdio (`--method` must come **before** the server command; in Development the server reads its own
  user-secrets, id `apcoa-flow-mcp`; otherwise pass `-e ApcoaFlow__Email=… -e ApcoaFlow__Password=…`):
  `npx -y @modelcontextprotocol/inspector --cli --method tools/list -e DOTNET_ENVIRONMENT=Development dotnet run --project src/ApcoaFlowMcp -- --stdio 2>/dev/null`
- **Safety:** same as the sandbox — read tools freely; `start_*` / `end_session` only on parking **616319**,
  and never `start_session` / `start_expensive_session` anywhere else (billable).
- For the OAuth sign-in page and other UI, use the Inspector web UI (`npx @modelcontextprotocol/inspector`,
  transport Streamable HTTP) — see README.

## Configuration conventions

When adding or changing any setting:

1. **Default in `appsettings.json`** — every key has a sensible default here.
2. **Dev overrides in `appsettings.Development.json`** — only values that differ from the default.
3. **`appsettings.Production.json`** — only values that differ from the default; omit anything that matches.
4. **`docker/docker-compose.prod.yml`** (clean, generic) and **`docker/docker-compose.synology.yml`** (prod + Synology ACL `user:` fix and a `simple` console logging block) — keep a commented reference block with default values so operators can see all knobs in one place. Uncomment to override.
5. **Document in README** — every operator-facing setting should be described.

## Live API tests = the API contract

The live tests (`[Category("LiveApi")]` in `src/ApcoaFlowApiClient.Tests`) run against the real, unofficial
APCOA Flow API with the test account (user-secrets id `apcoa-flow-api-tests`); CI runs them nightly against
the latest release.

- **Every API behaviour the code relies on needs a live test** — status codes (419 vs 401, 424, 422,
  login rejected as 200 + `failedLogin`), payload shapes and encodings, and silent successes the tools work
  around. When code starts depending on a new behaviour: verify it with the sandbox first, add the test
  (usually in `LiveApiContractTests`), and add a row to "Contracts guarded by live tests" in
  `docs/API-NOTES.md`. A behaviour that can't be tested safely (needs a paid start or a one-use coupon)
  goes under "Trusted, not tested" there, with the date and how it was observed.
- **Live tests never run in parallel**: every live test class carries `[NotInParallel(LiveApi.Serial)]`
  (one real account, one rate limit, a 403 lockout if hammered, one active session per account).
- Every live test class also has `[Before(HookType.Class)]` → `LiveApi.SweepOnceAtStartAsync()` (ends any
  session left ACTIVE — billable — by a crashed previous run; runs once per test run, whichever class
  starts first) and `[After(HookType.Class)]` → `LiveApi.EndActiveSessionsAsync(…)`. Classes that start
  sessions also end them after every test (`[After(Test)]`).
- Start sessions **only on parking `LiveApi.FreeParkingId` (616319)** — unlimited free start/stop.
