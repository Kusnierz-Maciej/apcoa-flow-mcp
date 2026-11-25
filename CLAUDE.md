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
  in `ScenarioRegistry`.
- The dump handler is attached as the innermost pipeline handler via the optional
  `configureClient` hook on `AddApcoaFlowApiClient`, so it captures each physical
  send — including the auth handler's 419/401 retry re-send.
- Dumps contain **live tokens**; `out/` is gitignored and must never be committed.

### Safety — sessions are billable

Keep casual probing **read-only**. Starting / extending / stopping a session is
billable and the test account is limited. Parking **616319** (lot 41905, tariff
108014) is the only target with unlimited free start/stop — use it for any write
test. The seeded `dump-sessions` scenario is strictly read-only.
