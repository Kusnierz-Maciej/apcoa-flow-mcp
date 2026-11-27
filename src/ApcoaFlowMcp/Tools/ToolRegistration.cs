using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tools;

/// <summary>
/// Registers the <see cref="ApcoaFlowTools"/> methods as MCP tools (same as <c>WithTools&lt;ApcoaFlowTools&gt;()</c>:
/// a fresh instance per call from the request's services) but with portable input schemas and lenient
/// argument binding.
/// </summary>
internal static class ToolRegistration
{
    public static IMcpServerBuilder WithApcoaFlowTools(this IMcpServerBuilder builder) =>
        builder.WithTools(CreateTools());

    /// <summary>
    /// Argument binding: the SDK defaults plus leniency for how clients and models actually send values —
    /// numeric parameters (<c>parkingId</c>, <c>sessionId</c>, <c>durationMinutes</c>, <c>confirmedCostPln</c>…)
    /// also accept numbers sent as JSON strings (<c>"616319"</c>), and string parameters accept JSON numbers
    /// (<see cref="LenientStringConverter"/>). Without this, such calls fail before the tool runs with an opaque
    /// error.
    /// </summary>
    internal static readonly JsonSerializerOptions ArgumentOptions = CreateArgumentOptions();

    private static JsonSerializerOptions CreateArgumentOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new LenientStringConverter());
        options.MakeReadOnly();
        return options;
    }

    internal static IEnumerable<McpServerTool> CreateTools() =>
        typeof(ApcoaFlowTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(CreateTool)
            .ToList();

    private static McpServerTool CreateTool(MethodInfo method)
    {
        static ApcoaFlowTools Target(RequestContext<ModelContextProtocol.Protocol.CallToolRequestParams> ctx) =>
            ActivatorUtilities.CreateInstance<ApcoaFlowTools>(ctx.Services!);

        // The advertised schema comes from the standard serializer options (accurate types) + the portability
        // transform; arguments are bound with the lenient options. The lenient options would change the schema
        // (number-from-string handling advertises "string" alternatives, a custom string converter hides the
        // "string" type), so the two are built separately and the schema copied over.
        var schemaSource = McpServerTool.Create(method, Target, new McpServerToolCreateOptions
        {
            SchemaCreateOptions = new AIJsonSchemaCreateOptions { TransformSchemaNode = PortableNullable },
        });
        var tool = McpServerTool.Create(method, Target, new McpServerToolCreateOptions
        {
            SerializerOptions = ArgumentOptions,
        });
        tool.ProtocolTool.InputSchema = schemaSource.ProtocolTool.InputSchema;
        return tool;
    }

    /// <summary>
    /// Rewrites a nullable type array — <c>"type": ["integer", "null"]</c>, legal JSON Schema but refused or
    /// mishandled by clients that map schemas onto a single-<c>type</c> dialect (e.g. Gemini's OpenAPI subset) —
    /// into <c>"anyOf": [{"type": "integer"}, {"type": "null"}]</c>. Other keywords (description, default…)
    /// stay on the outer node.
    /// </summary>
    internal static JsonNode PortableNullable(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        if (schema is not JsonObject obj || obj["type"] is not JsonArray types || types.Count < 2)
            return schema;

        obj.Remove("type");
        var branches = new JsonArray();
        foreach (var type in types)
            branches.Add(new JsonObject { ["type"] = type?.DeepClone() });
        obj["anyOf"] = branches;
        return obj;
    }

    /// <summary>Reads JSON strings normally and JSON numbers as their literal text; writes plain strings.</summary>
    internal sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence
                    ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray()),
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Expected a string, got {reader.TokenType}."),
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
