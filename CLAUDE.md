# CLAUDE.md

Guidance for Claude Code when working in this repository.

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
  in `ScenarioRegistry`. Existing scenarios each carry a WHAT/WHY/FOUND doc header:
  `dump-sessions` (read-only account snapshot), `investigate-carrefour` (read-only:
  lots, coupon, tariff breakdowns), `dump-tariff` (read-only: raw `/tariff` grid),
  `capture-carrefour-paid-start` (**BILLABLE** — real paid start+stop).
- The dump handler is attached as the innermost pipeline handler via the optional
  `configureClient` hook on `AddApcoaFlowApiClient`, so it captures each physical
  send — including the auth handler's 419/401 retry re-send.
- Dumps contain **live tokens**; `out/` is gitignored and must never be committed.

### Safety — sessions are billable

Keep casual probing **read-only**. Starting / extending / stopping a session is
billable and the test account is limited. Parking **616319** (lot 41905, tariff
108014) is the only target with unlimited free start/stop — use it for any write
test. The seeded `dump-sessions` scenario is strictly read-only.

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
