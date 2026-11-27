using System.Text.Json;
using ApcoaFlowMcp.Tools;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Tool input schemas must be portable: no <c>"type": [..]</c> arrays anywhere (some MCP clients map schemas
/// onto a single-type dialect and reject or drop them). Registration must keep names and annotations.
/// </summary>
public class ToolSchemaTests
{
    private static readonly List<ModelContextProtocol.Server.McpServerTool> Tools = ToolRegistration.CreateTools().ToList();

    private static JsonElement Property(string tool, string property) =>
        Tools.Single(t => t.ProtocolTool.Name == tool).ProtocolTool.InputSchema
            .GetProperty("properties").GetProperty(property);

    [Test]
    public async Task AllTenTools_Registered_WithAnnotations()
    {
        await Assert.That(Tools.Count).IsEqualTo(10);
        await Assert.That(Tools.All(t => t.ProtocolTool.Annotations?.Title is { Length: > 0 })).IsTrue();
        var find = Tools.Single(t => t.ProtocolTool.Name == "find_parking").ProtocolTool;
        await Assert.That(find.Annotations!.ReadOnlyHint).IsTrue();
        var expensive = Tools.Single(t => t.ProtocolTool.Name == "start_expensive_session").ProtocolTool;
        await Assert.That(expensive.Annotations!.DestructiveHint).IsTrue();
    }

    [Test]
    public async Task NoSchema_UsesTypeArrays()
    {
        var offenders = Tools.SelectMany(t => TypeArrays(t.ProtocolTool.InputSchema, t.ProtocolTool.Name)).ToList();
        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    [Arguments("start_session", "durationMinutes", "integer")]
    [Arguments("start_session", "confirmedCostPln", "number")]
    [Arguments("start_session_current_location", "radiusMeters", "number")]
    [Arguments("find_parking", "nameContains", "string")]
    public async Task NullableFields_BecomeAnyOf_KeepingTypeAndDescription(string tool, string property, string type)
    {
        var p = Property(tool, property);
        var types = p.GetProperty("anyOf").EnumerateArray().Select(b => b.GetProperty("type").GetString()!).ToList();

        await Assert.That(types).IsEquivalentTo(new[] { type, "null" });
        await Assert.That(p.GetProperty("description").GetString()).IsNotNull();
    }

    [Test]
    public async Task NumericIds_StayNumbers_InSchema()
    {
        await Assert.That(Property("start_session", "parkingId").GetProperty("type").GetString()).IsEqualTo("integer");
        await Assert.That(Property("end_session", "sessionId").GetProperty("type").GetString()).IsEqualTo("integer");
    }

    private static IEnumerable<string> TypeArrays(JsonElement node, string path)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in node.EnumerateObject())
                {
                    if (p.Name == "type" && p.Value.ValueKind == JsonValueKind.Array)
                        yield return path;
                    foreach (var x in TypeArrays(p.Value, $"{path}.{p.Name}")) yield return x;
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var e in node.EnumerateArray())
                    foreach (var x in TypeArrays(e, $"{path}[{i++}]")) yield return x;
                break;
        }
    }
}

public class ToolArgumentBindingTests
{
    private static T Bind<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ToolRegistration.ArgumentOptions)!;

    [Test]
    public async Task NumericParameters_AcceptNumbersSentAsStrings()
    {
        await Assert.That(Bind<int>("\"616319\"")).IsEqualTo(616319);
        await Assert.That(Bind<long>("\"123456789012\"")).IsEqualTo(123456789012L);
        await Assert.That(Bind<decimal?>("\"3.50\"")).IsEqualTo(3.50m);
        await Assert.That(Bind<int>("616319")).IsEqualTo(616319);
    }

    [Test]
    [Arguments("12345", "12345")]
    [Arguments("\"SK 12345\"", "SK 12345")]
    public async Task StringParameters_AcceptJsonNumbers(string json, string expected) =>
        await Assert.That(Bind<string>(json)).IsEqualTo(expected);
}
