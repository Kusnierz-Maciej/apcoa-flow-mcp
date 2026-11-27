using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ApcoaFlowApiClient.Auth;

/// <summary>How a token grant ended. Only an explicit <c>invalid_grant</c> is <see cref="Rejected"/>.</summary>
public enum TokenGrantOutcomeKind
{
    Ok,

    /// <summary>
    /// <c>400 invalid_grant</c>: wrong username/password (password grant) or a dead refresh token (refresh grant).
    /// </summary>
    Rejected,

    /// <summary>Any other status, a transport fault or an unreadable body: an API problem, not bad credentials.</summary>
    ApiError,
}

/// <summary>The result of a grant. <see cref="Detail"/> never contains credentials or tokens.</summary>
public readonly record struct TokenGrantOutcome(TokenGrantOutcomeKind Kind, AccessTokens? Tokens, string? Detail)
{
    public static TokenGrantOutcome Success(AccessTokens tokens) => new(TokenGrantOutcomeKind.Ok, tokens, null);
    public static TokenGrantOutcome Rejected(string? detail) => new(TokenGrantOutcomeKind.Rejected, null, detail);
    public static TokenGrantOutcome Api(string? detail) => new(TokenGrantOutcomeKind.ApiError, null, detail);
}

/// <summary>
/// The two OAuth grants on <c>{auth}/connect/token</c>: <c>password</c> (log in, a flat ~7 s server-side) and
/// <c>refresh_token</c> (~0.2 s, rotates the refresh token). No Turnstile and no <c>f-sig</c> on this path
/// (probed 2026-11-27). Uses the named, rate-limited <see cref="ServiceCollectionExtensions.AuthClientName"/> client.
/// Never throws for the expected failures; returns a <see cref="TokenGrantOutcome"/>.
/// </summary>
public sealed class TokenClient(IHttpClientFactory httpClientFactory, TimeProvider time, ILogger<TokenClient> logger)
{
    public Task<TokenGrantOutcome> PasswordGrantAsync(ApcoaFlowCredentials credentials, CancellationToken ct) =>
        GrantAsync("password", new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = credentials.Username,
            ["password"] = credentials.Password,
            ["client_id"] = ApcoaFlowEndpoints.ClientId,
            ["scope"] = ApcoaFlowEndpoints.Scope,
        }, ct);

    public Task<TokenGrantOutcome> RefreshGrantAsync(string refreshToken, CancellationToken ct) =>
        GrantAsync("refresh_token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ApcoaFlowEndpoints.ClientId,
        }, ct);

    private async Task<TokenGrantOutcome> GrantAsync(string grant, Dictionary<string, string> form, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(ServiceCollectionExtensions.AuthClientName);
        try
        {
            using var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(form), ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
                if (body is null || string.IsNullOrEmpty(body.AccessToken))
                    return TokenGrantOutcome.Api($"{grant}: HTTP {(int)response.StatusCode} without an access token");
                return TokenGrantOutcome.Success(AccessTokens.From(body, time.GetUtcNow()));
            }

            TokenErrorResponse? error = null;
            try { error = await response.Content.ReadFromJsonAsync<TokenErrorResponse>(ct); }
            catch (System.Text.Json.JsonException) { /* not OAuth JSON: an API problem, classified below */ }

            if (response.StatusCode == HttpStatusCode.BadRequest && error?.Error == "invalid_grant")
            {
                logger.LogInformation("APCOA {Grant} grant rejected: invalid_grant ({Description}).", grant, error.ErrorDescription);
                return TokenGrantOutcome.Rejected($"invalid_grant: {error.ErrorDescription}");
            }

            logger.LogWarning("APCOA {Grant} grant failed: HTTP {Status} {Error}.", grant, (int)response.StatusCode, error?.Error);
            return TokenGrantOutcome.Api($"{grant}: HTTP {(int)response.StatusCode} {error?.Error}".TrimEnd());
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "APCOA {Grant} grant failed with a transport error.", grant);
            return TokenGrantOutcome.Api($"{grant}: transport error");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("APCOA {Grant} grant timed out.", grant);
            return TokenGrantOutcome.Api($"{grant}: timeout");
        }
    }
}
