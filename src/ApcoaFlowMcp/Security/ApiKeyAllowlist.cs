using System.Security.Cryptography;
using System.Text;
using ApcoaFlowMcp.Security.OAuth;
using Microsoft.Extensions.Options;

namespace ApcoaFlowMcp.Security;

/// <summary>Binds <c>Security:AllowedApiKeys</c> (+ legacy single <c>Security:ApiKey</c>) and <c>Security:TokenSecret</c>.</summary>
public sealed class SecurityOptions
{
    public string[] AllowedApiKeys { get; set; } = [];

    /// <summary>Legacy single key — treated as an additional allowed key for the header path.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Secret seeding token encryption. Must be set and stable in production.</summary>
    public string? TokenSecret { get; set; }

    /// <summary>
    /// Whether callers must present an allowlisted MCP server API key. Default <c>true</c>. Set to
    /// <c>false</c> for trusted/private deployments to drop the API-key gate entirely (APCOA
    /// credentials are still required — the server has no default account).
    /// </summary>
    public bool RequireMcpApiKey { get; set; } = true;

    /// <summary>All configured keys (allowlist + legacy single), empty entries removed.</summary>
    public IEnumerable<string> AllKeys =>
        AllowedApiKeys.Append(ApiKey ?? string.Empty).Where(k => !string.IsNullOrWhiteSpace(k));
}

/// <summary>
/// Validates API keys against the configured allowlist and resolves whether a token's embedded
/// <c>apiKeyHash</c> is still allowlisted (the admin kill-switch: remove a key + restart → its tokens die).
/// All comparisons are constant-time. When <see cref="SecurityOptions.RequireMcpApiKey"/> is <c>false</c>,
/// every check passes — the API-key layer is disabled.
/// </summary>
public sealed class ApiKeyAllowlist(IOptions<SecurityOptions> options)
{
    private readonly SecurityOptions _options = options.Value;

    /// <summary>Whether an MCP server API key is required at all.</summary>
    public bool RequireApiKey => _options.RequireMcpApiKey;

    /// <summary>True when the server requires a key and has at least one configured.</summary>
    public bool IsEnabled => _options.RequireMcpApiKey && _options.AllKeys.Any();

    /// <summary>Constant-time check that a presented raw key is in the allowlist.</summary>
    public bool IsAllowed(string? presentedKey)
    {
        if (!_options.RequireMcpApiKey)
            return true;

        if (string.IsNullOrWhiteSpace(presentedKey))
            return false;

        var presented = Encoding.UTF8.GetBytes(presentedKey);
        var match = false;
        foreach (var key in _options.AllKeys)
            match |= CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(key));
        return match;
    }

    /// <summary>True when a token's embedded key hash still corresponds to an allowlisted key.</summary>
    public bool IsHashAllowed(string? apiKeyHash)
    {
        if (!_options.RequireMcpApiKey)
            return true;

        if (string.IsNullOrWhiteSpace(apiKeyHash))
            return false;

        var presented = Encoding.UTF8.GetBytes(apiKeyHash);
        var match = false;
        foreach (var key in _options.AllKeys)
            match |= CryptographicOperations.FixedTimeEquals(
                presented, Encoding.UTF8.GetBytes(CredentialTokenService.HashApiKey(key)));
        return match;
    }
}
