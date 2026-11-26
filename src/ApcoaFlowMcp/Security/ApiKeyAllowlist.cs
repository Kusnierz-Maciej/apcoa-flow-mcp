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

    /// <summary>All configured keys (allowlist + legacy single), empty entries removed.</summary>
    public IEnumerable<string> AllKeys =>
        AllowedApiKeys.Append(ApiKey ?? string.Empty).Where(k => !string.IsNullOrWhiteSpace(k));
}

/// <summary>
/// Validates API keys against the configured allowlist and resolves whether a token's embedded
/// <c>apiKeyHash</c> is still allowlisted (the admin kill-switch: remove a key + restart → its tokens die).
/// All comparisons are constant-time.
/// </summary>
public sealed class ApiKeyAllowlist(IOptions<SecurityOptions> options)
{
    private readonly SecurityOptions _options = options.Value;

    /// <summary>True when the server requires a key at all (any key configured).</summary>
    public bool IsEnabled => _options.AllKeys.Any();

    /// <summary>Constant-time check that a presented raw key is in the allowlist.</summary>
    public bool IsAllowed(string? presentedKey)
    {
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
