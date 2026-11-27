using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace ApcoaFlowApiClient;

public class ApcoaFlowAuthHandler : DelegatingHandler
{
    /// <summary>
    /// Sentinel request header: interface methods marked with it hit PUBLIC endpoints that reject a
    /// bearer token with HTTP 419. The handler strips this header and omits Authorization for them.
    /// </summary>
    public const string NoAuthHeader = "X-Apcoa-NoAuth";

    private readonly IApcoaFlowCredentialProvider _credentials;
    private readonly ApcoaFlowTokenStore _tokens;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApcoaFlowAuthHandler>? _logger;

    public ApcoaFlowAuthHandler(
        IApcoaFlowCredentialProvider credentials,
        ApcoaFlowTokenStore tokens,
        IHttpClientFactory httpClientFactory,
        ILogger<ApcoaFlowAuthHandler>? logger = null)
    {
        _credentials = credentials;
        _tokens = tokens;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApplyAppHeaders(request);

        // Public endpoints (e.g. parking detail / parkingLots) must NOT carry a bearer token —
        // the backend returns 419 if they do. They are opted out via the NoAuth sentinel header.
        if (request.Headers.Contains(NoAuthHeader))
        {
            request.Headers.Remove(NoAuthHeader);
            return await base.SendAsync(request, cancellationToken);
        }

        var credentials = _credentials.GetCredentials();

        var token = await _tokens.GetOrLoginAsync(credentials, ct => LoginAsync(credentials, ct), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);

        if (IsAuthFailure(response.StatusCode))
        {
            _logger?.LogWarning("APCOA returned HTTP {Status}; invalidating the cached token and re-logging in once.",
                (int)response.StatusCode);
            // Explains the duplicate HTTP span that follows (same request re-sent with a fresh token).
            ApcoaTelemetry.Event("apcoa.token_rejected_retrying",
                ("http.response.status_code", (int)response.StatusCode),
                ("url.path", request.RequestUri?.AbsolutePath));

            _tokens.Invalidate(credentials, token);

            token = await _tokens.GetOrLoginAsync(credentials, ct => LoginAsync(credentials, ct), cancellationToken);

            var retryRequest = await CloneRequestAsync(request);
            ApplyAppHeaders(retryRequest);
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await base.SendAsync(retryRequest, cancellationToken);

            // If it STILL fails auth after a fresh login, the credentials themselves are no longer
            // valid (e.g. the user changed their APCOA password). Surface that distinctly so the MCP
            // layer can force re-authentication, rather than returning an opaque 401 body.
            if (IsAuthFailure(response.StatusCode))
            {
                _logger?.LogError("APCOA still returned HTTP {Status} after a fresh login — credentials rejected.",
                    (int)response.StatusCode);
                _tokens.Invalidate(credentials, token);
                ApcoaTelemetry.Fail("credentials_rejected", $"HTTP {(int)response.StatusCode} after re-login");
                throw new ApcoaFlowCredentialsRejectedException($"HTTP {(int)response.StatusCode} after re-login");
            }
        }

        return response;
    }

    // HTTP 419 is APCOA's "Invalid or Expired Token"; 401 is standard unauthorized.
    private static bool IsAuthFailure(HttpStatusCode status) =>
        status == HttpStatusCode.Unauthorized || (int)status == 419;

    /// <summary>Stamps the static app headers the backend requires, without overwriting any already set.</summary>
    private static void ApplyAppHeaders(HttpRequestMessage request)
    {
        foreach (var (name, value) in ApcoaFlowClientOptions.AppHeaders)
        {
            if (!request.Headers.Contains(name))
                request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private async Task<string> LoginAsync(ApcoaFlowClientOptions credentials, CancellationToken cancellationToken)
    {
        // Only runs on a token-cache miss, so the span marks when (and how often) we really log in.
        using var activity = ApcoaTelemetry.Source.StartActivity("apcoa.login");
        activity?.SetTag(ApcoaTelemetry.Tags.UserEmail, credentials.Email);
        var outcome = await ApcoaFlowLogin.TryLoginAsync(_httpClientFactory, credentials.Email, credentials.Password, _logger, cancellationToken);
        activity?.SetTag(ApcoaTelemetry.Tags.Outcome, outcome.Kind.ToString());
        if (outcome.Kind != LoginOutcomeKind.Ok)
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, outcome.Kind.ToString());
        return outcome.Kind switch
        {
            LoginOutcomeKind.Ok => outcome.Token!,
            LoginOutcomeKind.BadCredentials => throw new ApcoaFlowCredentialsRejectedException(outcome.Detail),
            _ => throw new HttpRequestException(
                $"APCOA login failed with an API error ({outcome.Detail}). Try again shortly."),
        };
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        if (original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }
}
