using ApcoaFlow.Sandbox;
using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlow.Sandbox.Scenarios;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// ApcoaFlow.Sandbox — a private, local-only harness for driving the APCOA Flow API / MCP with
// FULL request/response dumping, for diagnosis. Not shipped, not a test project.
//
// Usage:
//   dotnet run --project src/ApcoaFlow.Sandbox                 → list scenarios
//   dotnet run --project src/ApcoaFlow.Sandbox -- dump-sessions → run a scenario
//
// Every run writes to src/ApcoaFlow.Sandbox/out/<UTC-timestamp>/ : one NNNN_*.http file per HTTP
// exchange (full headers + body, request and response) plus run.log and meta.txt.
//
// SAFETY: starting / extending / stopping sessions is BILLABLE and the test account is limited.
// Keep casual probing read-only. Parking 616319 (lot 41905, tariff 108014) is the only target
// with unlimited free start/stop — use it for any write test.
// ─────────────────────────────────────────────────────────────────────────────────────────────

var scenarioName = args.FirstOrDefault();

if (string.IsNullOrWhiteSpace(scenarioName))
{
    Console.WriteLine("Available scenarios:");
    foreach (var s in ScenarioRegistry.All)
        Console.WriteLine($"  {s.Name,-20} {s.Description}");
    Console.WriteLine();
    Console.WriteLine("Run one with:  dotnet run --project src/ApcoaFlow.Sandbox -- <name>");
    return 0;
}

var scenario = ScenarioRegistry.Find(scenarioName);
if (scenario is null)
{
    Console.Error.WriteLine($"Unknown scenario '{scenarioName}'. Known: {string.Join(", ", ScenarioRegistry.All.Select(s => s.Name))}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var run = new DumpRun(scenario.Name);
await using var services = SandboxHost.Build(run);

run.Log($"scenario '{scenario.Name}' starting…");
try
{
    await scenario.RunAsync(services, run, cts.Token);
    run.Log($"scenario '{scenario.Name}' finished OK.");
}
catch (Exception ex)
{
    run.Log($"scenario '{scenario.Name}' FAILED: {ex}");
    Console.Error.WriteLine($"\nScenario failed — see the dump for the wire traffic. {ex.Message}");
    Console.WriteLine($"\nDump directory: {run.Directory}");
    return 1;
}

Console.WriteLine($"\nDump directory: {run.Directory}");
return 0;
