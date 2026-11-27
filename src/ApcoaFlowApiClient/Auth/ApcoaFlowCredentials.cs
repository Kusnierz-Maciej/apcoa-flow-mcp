using System.Security.Cryptography;
using System.Text;

namespace ApcoaFlowApiClient.Auth;

/// <summary>
/// The APCOA Flow account the client acts for. <see cref="Username"/> is the account e-mail (the MCP's choice) or the
/// phone number in E.164 (<c>+48…</c>); <c>/connect/token</c> accepts both.
/// </summary>
public sealed record ApcoaFlowCredentials(string Username, string Password)
{
    /// <summary>
    /// Cache key: SHA-256 of username + NUL + password, so plaintext passwords are never dictionary keys. The NUL
    /// keeps ("a:b","c") and ("a","b:c") distinct.
    /// </summary>
    internal string CacheKey =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Username}\0{Password}")));

    /// <summary>Never print the password.</summary>
    public override string ToString() => $"ApcoaFlowCredentials {{ Username = {Username} }}";
}

/// <summary>Supplies the credentials for the current request scope (per MCP user in the host).</summary>
public interface IApcoaFlowCredentialProvider
{
    ApcoaFlowCredentials GetCredentials();
}
