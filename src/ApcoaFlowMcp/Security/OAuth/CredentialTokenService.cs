using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace ApcoaFlowMcp.Security.OAuth;

/// <summary>The kind of token a payload belongs to — bound into the payload so a code can't be used as
/// an access token, a refresh token can't be replayed at <c>/mcp</c>, etc.</summary>
public enum TokenKind
{
    Code,
    Access,
    Refresh,
}

/// <summary>
/// The decrypted contents of an OAuth token. Carries the APCOA credentials, a salted hash of the API key
/// (for the allowlist kill-switch), the token kind, and PKCE/redirect binding (codes only).
/// </summary>
public sealed record TokenPayload(
    TokenKind Kind,
    string Email,
    string Password,
    string ApiKeyHash,
    string? CodeChallenge,
    string? RedirectUri);

/// <summary>
/// Encrypts/decrypts opaque OAuth tokens with ASP.NET Core Data Protection. Tokens are not readable JWTs:
/// the whole payload (incl. the APCOA password) is encrypted and authenticated, with an expiry enforced by
/// the protector. The protection key derives from <c>Security:TokenSecret</c> (configured in
/// <see cref="ServiceCollectionExtensions"/>), so tokens survive restarts and are portable across replicas.
/// </summary>
public sealed class CredentialTokenService(IDataProtectionProvider provider)
{
    // Separate protector purposes per kind add defence in depth: a blob protected as one kind cannot be
    // unprotected as another even before the in-payload Kind check.
    private IDataProtector ProtectorFor(TokenKind kind) =>
        provider.CreateProtector($"ApcoaFlowMcp.OAuth.{kind}");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Protects a payload into an opaque token string valid for <paramref name="ttl"/>.</summary>
    public string Protect(TokenPayload payload, TimeSpan ttl)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var protector = ProtectorFor(payload.Kind).ToTimeLimitedDataProtector();
        return protector.Protect(Convert.ToBase64String(json), ttl);
    }

    /// <summary>
    /// Unprotects a token and returns its payload, or null if the token is tampered, expired, of the
    /// wrong kind, or otherwise invalid. Never throws for an invalid token.
    /// </summary>
    public TokenPayload? TryUnprotect(string? token, TokenKind expectedKind)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var protector = ProtectorFor(expectedKind).ToTimeLimitedDataProtector();
            var base64 = protector.Unprotect(token);
            var payload = JsonSerializer.Deserialize<TokenPayload>(
                Convert.FromBase64String(base64), Json);

            // Defence in depth: the payload's own Kind must match what we expected to unprotect.
            return payload is { } p && p.Kind == expectedKind ? p : null;
        }
        catch (CryptographicException)
        {
            return null; // tampered or expired
        }
        catch (FormatException)
        {
            return null; // not a valid token string
        }
        catch (JsonException)
        {
            return null; // payload shape changed / corrupt
        }
    }

    /// <summary>A salted, stable hash of an API key for embedding in tokens (never the raw key).</summary>
    public static string HashApiKey(string apiKey)
    {
        // Static salt tying the hash to this application; the key itself is the secret, this just avoids
        // storing it verbatim inside tokens.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("ApcoaFlowMcp.ApiKey\u0000" + apiKey));
        return Convert.ToHexString(bytes);
    }
}
