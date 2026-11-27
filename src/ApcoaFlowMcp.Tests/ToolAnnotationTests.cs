using System.Reflection;
using ApcoaFlowMcp.Tools;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Tool metadata clients use to decide when to ask the user: reads are read-only, starts are writes that
/// aren't idempotent (every call may start/charge a session), the cost-cap bypass and end_session are
/// destructive so clients always confirm them.
/// </summary>
public class ToolAnnotationTests
{
    private static IEnumerable<McpServerToolAttribute> All() =>
        typeof(ApcoaFlowTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)!;

    private static McpServerToolAttribute Tool(string name) => All().Single(a => a.Name == name);

    [Test]
    [Arguments("find_parking")]
    [Arguments("get_registered_cars")]
    [Arguments("get_active_sessions")]
    [Arguments("get_session_history")]
    public async Task Reads_AreReadOnly_AndIdempotent(string name)
    {
        await Assert.That(Tool(name).ReadOnly).IsTrue();
        await Assert.That(Tool(name).Idempotent).IsTrue();
    }

    [Test]
    [Arguments("start_session")]
    [Arguments("start_free_session")]
    [Arguments("start_session_current_location")]
    [Arguments("start_free_session_current_location")]
    public async Task Starts_AreWrites_NotDestructive_NotIdempotent(string name)
    {
        await Assert.That(Tool(name).ReadOnly).IsFalse();
        await Assert.That(Tool(name).Destructive).IsFalse();
        await Assert.That(Tool(name).Idempotent).IsFalse();
    }

    [Test]
    public async Task ExpensiveStart_IsDestructive_SoClientsAlwaysConfirm()
    {
        await Assert.That(Tool("start_expensive_session").ReadOnly).IsFalse();
        await Assert.That(Tool("start_expensive_session").Destructive).IsTrue();
        await Assert.That(Tool("start_expensive_session").Idempotent).IsFalse();
    }

    [Test]
    public async Task EndSession_IsDestructive_AndIdempotent()
    {
        await Assert.That(Tool("end_session").ReadOnly).IsFalse();
        await Assert.That(Tool("end_session").Destructive).IsTrue();
        await Assert.That(Tool("end_session").Idempotent).IsTrue();
    }

    [Test]
    public async Task EveryTool_HasATitle_AndAllTenAreCovered()
    {
        await Assert.That(All().Count()).IsEqualTo(10);
        await Assert.That(All().All(a => !string.IsNullOrWhiteSpace(a.Title))).IsTrue();
    }
}
