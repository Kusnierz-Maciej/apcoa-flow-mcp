using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace ApcoaFlowApiClient.Auth;

/// <summary>
/// API pipeline handler: stamps the app headers and the bearer token, and on an auth failure renews the token once and
/// re-sends. A second auth failure with a fresh token means the account itself is refused
/// (<see cref="ApcoaFlowCredentialsRejectedException"/>).
/// </summary>
public sealed class ApcoaFlowAuthHandler(
    IApcoaFlowCredentialProvider credentials,
    TokenStore tokens,
    TimeProvider time,
    ILogger<ApcoaFlowAuthHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var account = credentials.GetCredentials();
        var token = await tokens.GetAccessTokenAsync(account, ct);
        Stamp(request, token);

        var response = await base.SendAsync(request, ct);
        if (!await IsAuthFailureAsync(response, ct))
            return response;

        logger.LogWarning("APCOA refused the access token (HTTP {Status}); renewing it and retrying once.", (int)response.StatusCode);
        ApcoaTelemetry.Event("apcoa.token_rejected_retrying",
            ("http.response.status_code", (int)response.StatusCode), ("url.path", request.RequestUri?.AbsolutePath));
        response.Dispose();

        tokens.Invalidate(account, token);
        token = await tokens.GetAccessTokenAsync(account, ct);
        var retry = await CloneAsync(request);
        Stamp(retry, token);
        response = await base.SendAsync(retry, ct);

        if (await IsAuthFailureAsync(response, ct))
        {
            logger.LogError("APCOA still refused a fresh access token (HTTP {Status}).", (int)response.StatusCode);
            tokens.Invalidate(account, token);
            ApcoaTelemetry.Fail("credentials_rejected", $"HTTP {(int)response.StatusCode} after renewing the token");
            response.Dispose();
            throw new ApcoaFlowCredentialsRejectedException($"HTTP {(int)response.StatusCode} after renewing the token");
        }
        return response;
    }

    private void Stamp(HttpRequestMessage request, string token)
    {
        foreach (var (name, value) in ApcoaFlowEndpoints.AppHeaders)
            if (!request.Headers.Contains(name))
                request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Remove(ApcoaFlowEndpoints.TimezoneOffsetHeader);
        request.Headers.TryAddWithoutValidation(ApcoaFlowEndpoints.TimezoneOffsetHeader,
            ApcoaFlowEndpoints.TimezoneOffsetMinutes(time.GetUtcNow()));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// No, invalid or expired token: <c>403 {"code":"Unauthenticated"}</c> (not 401; an expired token checked at 65 min,
    /// 2026-11-27). 401 is treated the same, defensively.
    /// </summary>
    internal static async Task<bool> IsAuthFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return true;
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return false;
        await response.Content.LoadIntoBufferAsync(ct);
        var error = await ApcoaFlowApiError.TryReadAsync(response.Content, ct);
        return error?.Code == ApcoaFlowApiError.Codes.Unauthenticated;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        if (original.Content is not null)
        {
            clone.Content = new ByteArrayContent(await original.Content.ReadAsByteArrayAsync());
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in original.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        return clone;
    }
}
