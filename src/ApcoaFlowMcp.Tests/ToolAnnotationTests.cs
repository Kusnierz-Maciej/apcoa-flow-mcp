using System.Reflection;
using ApcoaFlowMcp.Tools;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Tool metadata clients use to decide when to ask the user: reads are read-only and idempotent; every tool has a
/// title. (Write tools get their own checks when they're added.)
/// </summary>
public class ToolAnnotationTests
{
    private static IEnumerable<McpServerToolAttribute> All() =>
        typeof(ApcoaFlowTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)!;

    private static McpServerToolAttribute Tool(string name) => All().Single(a => a.Name == name);

    [Test]
    [Arguments("get_vehicles")]
    [Arguments("find_parkings")]
    [Arguments("get_parking_details")]
    [Arguments("get_price")]
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
    [Arguments("start_session_here")]
    [Arguments("start_free_session_here")]
    [Arguments("extend_session")]
    public async Task Starts_and_extends_are_writes_not_destructive_not_idempotent(string name)
    {
        await Assert.That(Tool(name).ReadOnly).IsFalse();
        await Assert.That(Tool(name).Destructive).IsFalse();
        await Assert.That(Tool(name).Idempotent).IsFalse();
    }

    [Test]
    public async Task ExpensiveStart_is_destructive_so_clients_always_confirm()
    {
        await Assert.That(Tool("start_expensive_session").Destructive).IsTrue();
        await Assert.That(Tool("start_expensive_session").Idempotent).IsFalse();
    }

    [Test]
    public async Task EndSession_is_destructive_and_idempotent()
    {
        await Assert.That(Tool("end_session").Destructive).IsTrue();
        await Assert.That(Tool("end_session").Idempotent).IsTrue();
    }

    [Test]
    public async Task EveryTool_HasATitle() =>
        await Assert.That(All().All(a => !string.IsNullOrWhiteSpace(a.Title))).IsTrue();
}
