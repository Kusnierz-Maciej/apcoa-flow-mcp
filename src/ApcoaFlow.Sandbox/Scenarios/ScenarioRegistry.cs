namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>Central list of runnable scenarios, keyed by <see cref="IScenario.Name"/>.</summary>
public static class ScenarioRegistry
{
    public static readonly IReadOnlyList<IScenario> All =
    [
        new LoginScenario(),
        new AccountScenario(),
        new FindParkingsScenario(),
        new QuotesScenario(),
        new ExpiredTokenScenario(),
    ];

    public static IScenario? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
