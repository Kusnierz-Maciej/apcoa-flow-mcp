using System.Net;
using System.Net.Http.Headers;
using ApcoaFlowApiClient.Models.Auth;
using Refit;

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

    public ApcoaFlowAuthHandler(IApcoaFlowCredentialProvider credentials, ApcoaFlowTokenStore tokens)
    {
        _credentials = credentials;
        _tokens = tokens;
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
            _tokens.Invalidate(credentials, token);

            token = await _tokens.GetOrLoginAsync(credentials, ct => LoginAsync(credentials, ct), cancellationToken);

            var retryRequest = await CloneRequestAsync(request);
            ApplyAppHeaders(retryRequest);
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            response = await base.SendAsync(retryRequest, cancellationToken);
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

    private static async Task<string> LoginAsync(ApcoaFlowClientOptions credentials, CancellationToken cancellationToken)
    {
        var http = new HttpClient { BaseAddress = new Uri(ApcoaFlowClientOptions.BaseUrl) };
        foreach (var (name, value) in ApcoaFlowClientOptions.AppHeaders)
            http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);

        var loginApi = RestService.For<IApcoaFlowApi>(http);

        var response = await loginApi.LoginAsync(new LoginRequest
        {
            Email = credentials.Email,
            Password = credentials.Password,
            LocationCode = "PL"
        }, cancellationToken);

        return response.AuthToken;
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
