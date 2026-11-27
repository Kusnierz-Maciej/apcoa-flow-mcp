namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>Central list of runnable scenarios, keyed by <see cref="IScenario.Name"/>.</summary>
public static class ScenarioRegistry
{
    public static readonly IReadOnlyList<IScenario> All =
    [
        new DumpSessionsScenario(),
        new InvestigateCarrefourScenario(),
        new InvestigateAldiScenario(),
        new StartFreeAldiScenario(),
        new CaptureCarrefourPaidStartScenario(),
        new DumpTariffScenario(),
        new LoginProbeScenario(),
    ];

    public static IScenario? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
