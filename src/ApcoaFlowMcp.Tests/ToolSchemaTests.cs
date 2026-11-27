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

    [Test]
    public async Task Tools_Registered_WithTitlesAndAnnotations()
    {
        await Assert.That(Tools).IsNotEmpty();
        await Assert.That(Tools.All(t => t.ProtocolTool.Annotations?.Title is { Length: > 0 })).IsTrue();
        var vehicles = Tools.Single(t => t.ProtocolTool.Name == "get_vehicles").ProtocolTool;
        await Assert.That(vehicles.Annotations!.ReadOnlyHint).IsTrue();
    }

    [Test]
    public async Task NoSchema_UsesTypeArrays()
    {
        var offenders = Tools.SelectMany(t => TypeArrays(t.ProtocolTool.InputSchema, t.ProtocolTool.Name)).ToList();
        await Assert.That(offenders).IsEmpty();
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
        await Assert.That(Bind<int>("\"1800\"")).IsEqualTo(1800);
        await Assert.That(Bind<long>("\"123456789012\"")).IsEqualTo(123456789012L);
        await Assert.That(Bind<decimal?>("\"3.50\"")).IsEqualTo(3.50m);
        await Assert.That(Bind<int>("1800")).IsEqualTo(1800);
    }

    [Test]
    [Arguments("12345", "12345")]
    [Arguments("\"ABC12345\"", "ABC12345")]
    public async Task StringParameters_AcceptJsonNumbers(string json, string expected) =>
        await Assert.That(Bind<string>(json)).IsEqualTo(expected);
}
