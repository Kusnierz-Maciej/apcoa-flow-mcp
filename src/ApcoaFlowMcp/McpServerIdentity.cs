using System.Reflection;
using ModelContextProtocol.Protocol;

namespace ApcoaFlowMcp;

/// <summary>
/// The server's MCP <c>serverInfo</c>: name, version, display title and icons. Icons are inlined as
/// <c>data:</c> URIs (embedded from <c>wwwroot</c>) so clients can show them without knowing the
/// server's public URL — it sits behind a proxy and works the same in stdio mode. The artwork is the
/// APCOA mark from apcoa.pl's favicon set.
/// </summary>
internal static class McpServerIdentity
{
    private static readonly Assembly Self = typeof(McpServerIdentity).Assembly;

    public static Implementation Create() => new()
    {
        Name = "ApcoaFlowMcp",
        Title = "APCOA Flow (unofficial)",
        Version = Self.GetName().Version?.ToString(3) ?? "unknown",
        Icons =
        [
            DataUriIcon("icon-192.png", "image/png", "192x192"),
            DataUriIcon("icon-512.png", "image/png", "512x512"),
        ],
    };

    private static Icon DataUriIcon(string file, string mimeType, string size)
    {
        using var stream = Self.GetManifestResourceStream($"ApcoaFlowMcp.Icons.{file}")
            ?? throw new InvalidOperationException($"Embedded icon '{file}' not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new Icon
        {
            Source = $"data:{mimeType};base64,{Convert.ToBase64String(buffer.ToArray())}",
            MimeType = mimeType,
            Sizes = [size],
        };
    }
}
