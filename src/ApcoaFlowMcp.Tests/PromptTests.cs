using ApcoaFlowMcp.Prompts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tests;

public class PromptTests
{
    private static List<McpServerPrompt> Registered(bool? enabled)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled is not null) settings[PromptRegistration.EnabledKey] = enabled.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithApcoaFlowPrompts(config);
        return services.BuildServiceProvider().GetServices<McpServerPrompt>().ToList();
    }

    [Test]
    public async Task EnabledByDefault_FourPrompts_WithTitles()
    {
        var prompts = Registered(enabled: null);

        await Assert.That(prompts.Select(p => p.ProtocolPrompt.Name).Order())
            .IsEquivalentTo(new[] { "end_parking", "park_free_at", "park_free_here", "parking_costs" });
        await Assert.That(prompts.All(p => !string.IsNullOrWhiteSpace(p.ProtocolPrompt.Title))).IsTrue();
        var parkAt = prompts.Single(p => p.ProtocolPrompt.Name == "park_free_at").ProtocolPrompt;
        await Assert.That(parkAt.Arguments!.Single(a => a.Name == "parking").Required).IsTrue();
        await Assert.That(parkAt.Arguments!.Single(a => a.Name == "plate").Required).IsNotEqualTo(true);
    }

    [Test]
    public async Task Disabled_RegistersNothing() =>
        await Assert.That(Registered(enabled: false)).IsEmpty();

    [Test]
    public async Task Texts_DeferToUserPreferences()
    {
        foreach (var text in new[]
                 {
                     ApcoaFlowPrompts.ParkFreeHere(), ApcoaFlowPrompts.ParkFreeAt("Carrefour"),
                     ApcoaFlowPrompts.EndParking(), ApcoaFlowPrompts.ParkingCosts(),
                 })
            await Assert.That(text).EndsWith(ApcoaFlowPrompts.Defer);
    }

    [Test]
    public async Task Starts_AreFreeOnly_AndConfirmed()
    {
        foreach (var text in new[] { ApcoaFlowPrompts.ParkFreeHere("SK 12345"), ApcoaFlowPrompts.ParkFreeAt("Carrefour") })
        {
            await Assert.That(text).Contains("start_free_session");
            await Assert.That(text).Contains("ask me to confirm before starting");
            await Assert.That(text).Contains("don't start a paid session");
            await Assert.That(text).DoesNotContain("start_session ");
        }
        await Assert.That(ApcoaFlowPrompts.ParkFreeHere("SK 12345")).Contains("\"SK 12345\"");
        await Assert.That(ApcoaFlowPrompts.ParkFreeHere()).Contains("[default]");
    }

    [Test]
    public async Task EndParking_ConfirmsFirst_Costs_IsReadOnly_AndFlagsPartialHistory()
    {
        await Assert.That(ApcoaFlowPrompts.EndParking()).Contains("Only after I confirm");
        await Assert.That(ApcoaFlowPrompts.ParkingCosts()).Contains("this month");
        await Assert.That(ApcoaFlowPrompts.ParkingCosts("last week")).Contains("last week");
        await Assert.That(ApcoaFlowPrompts.ParkingCosts()).Contains("read-only");
        await Assert.That(ApcoaFlowPrompts.ParkingCosts()).Contains("may be incomplete");
    }

    [Test]
    public async Task Completion_PrefixFirst_ThenContains_CaseInsensitive_Distinct_SkipsBlanks()
    {
        var result = PromptRegistration.Complete(
            ["Hala Tarnowskie Góry", "Carrefour Tarnowskie Góry", "carrefour tarnowskie góry", "Aldi Carrefourowa", null, " "], "carr");

        await Assert.That(result.Completion.Values)
            .IsEquivalentTo(new[] { "Carrefour Tarnowskie Góry", "Aldi Carrefourowa" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(result.Completion.Total).IsEqualTo(2);
        await Assert.That(result.Completion.HasMore).IsFalse();
    }

    [Test]
    public async Task Completion_EmptyText_ReturnsAll() =>
        await Assert.That(PromptRegistration.Complete(["SK 1", "SK 2"], "").Completion.Values.Count).IsEqualTo(2);
}
