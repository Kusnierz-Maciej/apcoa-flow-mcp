using ApcoaFlow.Sandbox.Dumping;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// A named thing to run against the live API/MCP with full request/response dumping.
/// Add new probes by implementing this and registering them in <see cref="ScenarioRegistry"/>.
/// </summary>
public interface IScenario
{
    /// <summary>CLI name, e.g. <c>dump-sessions</c>.</summary>
    string Name { get; }

    /// <summary>One-line description shown when listing scenarios.</summary>
    string Description { get; }

    Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct);
}
