using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ApcoaFlowApiClient.Auth;

/// <summary>
/// Per-account access tokens, shared by every request of that account. A fresh token is returned as is; an expiring
/// one is renewed with the refresh token; when that fails (or there is none) the store logs in again with the
/// password grant. One renewal at a time per account (login takes ~7 s; concurrent callers wait for it).
/// </summary>
public sealed class TokenStore(TokenClient tokenClient, TimeProvider time, ILogger<TokenStore> logger)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed class Entry
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public AccessTokens? Tokens;
    }

    /// <summary>A valid access token for <paramref name="credentials"/>, renewing or logging in when needed.</summary>
    /// <exception cref="ApcoaFlowCredentialsRejectedException">The password grant rejected the credentials.</exception>
    /// <exception cref="ApcoaFlowAuthUnavailableException">The auth server failed for another reason.</exception>
    public async Task<string> GetAccessTokenAsync(ApcoaFlowCredentials credentials, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(credentials.CacheKey, _ => new Entry());
        if (entry.Tokens is { } cached && cached.IsFresh(time.GetUtcNow()))
            return cached.AccessToken;

        await entry.Lock.WaitAsync(ct);
        try
        {
            if (entry.Tokens is { } current && current.IsFresh(time.GetUtcNow()))
                return current.AccessToken; // renewed by the caller we waited for

            entry.Tokens = await RenewAsync(credentials, entry.Tokens, ct);
            return entry.Tokens.AccessToken;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>
    /// Forgets <paramref name="staleAccessToken"/> after the API rejected it, so the next call renews. Does nothing when
    /// another caller already replaced it.
    /// </summary>
    public void Invalidate(ApcoaFlowCredentials credentials, string staleAccessToken)
    {
        if (_entries.TryGetValue(credentials.CacheKey, out var entry) && entry.Tokens is { } tokens &&
            tokens.AccessToken == staleAccessToken)
        {
            // Keep the refresh token: a rejected access token doesn't mean the refresh token is dead.
            entry.Tokens = tokens with { ExpiresAt = DateTimeOffset.MinValue };
        }
    }

    private async Task<AccessTokens> RenewAsync(ApcoaFlowCredentials credentials, AccessTokens? previous, CancellationToken ct)
    {
        if (previous?.RefreshToken is { } refreshToken)
        {
            using var refresh = ApcoaTelemetry.Source.StartActivity("apcoa.token_refresh");
            var refreshed = await tokenClient.RefreshGrantAsync(refreshToken, ct);
            refresh?.SetTag(ApcoaTelemetry.Tags.Outcome, refreshed.Kind.ToString());
            if (refreshed.Kind == TokenGrantOutcomeKind.Ok)
                return refreshed.Tokens!;
            logger.LogInformation("APCOA token refresh failed ({Detail}); logging in again.", refreshed.Detail);
        }

        using var login = ApcoaTelemetry.Source.StartActivity("apcoa.login");
        login?.SetTag(ApcoaTelemetry.Tags.UserEmail, credentials.Username);
        var outcome = await tokenClient.PasswordGrantAsync(credentials, ct);
        login?.SetTag(ApcoaTelemetry.Tags.Outcome, outcome.Kind.ToString());
        if (outcome.Kind != TokenGrantOutcomeKind.Ok)
            login?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, outcome.Kind.ToString());

        return outcome.Kind switch
        {
            TokenGrantOutcomeKind.Ok => outcome.Tokens!,
            TokenGrantOutcomeKind.Rejected => throw new ApcoaFlowCredentialsRejectedException(outcome.Detail),
            _ => throw new ApcoaFlowAuthUnavailableException(outcome.Detail),
        };
    }
}
