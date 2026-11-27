using System.Net;
using ApcoaFlowApiClient.Models.Auth;
using Microsoft.Extensions.Logging;
using Refit;

namespace ApcoaFlowApiClient;

/// <summary>
/// Shared APCOA login logic: a single call on the rate-limited login client (which carries the static app
/// headers), classifying the result into <see cref="LoginOutcome"/>. Both <see cref="ApcoaFlowAuthHandler"/> (to
/// obtain a bearer token) and the OAuth <c>/authorize</c> validator use this so the notion of a
/// "bad-credential login" is defined in exactly one place.
/// </summary>
public static class ApcoaFlowLogin
{
    /// <summary>
    /// Attempts a login and classifies the outcome. A non-success HTTP status that is a credential
    /// rejection (401/403/422), or a 2xx body with <see cref="LoginResponse.FailedLogin"/> true / an
    /// empty token, is <see cref="LoginOutcomeKind.BadCredentials"/>. A 419 / 5xx / network fault is
    /// <see cref="LoginOutcomeKind.ApiError"/>. Never throws for the expected failure cases.
    /// </summary>
    public static async Task<LoginOutcome> TryLoginAsync(
        IHttpClientFactory httpClientFactory, string email, string password, ILogger? logger, CancellationToken ct)
    {
        // Factory client: pooled handlers and the shared rate limit (see ServiceCollectionExtensions).
        var api = RestService.For<IApcoaFlowApi>(
            httpClientFactory.CreateClient(ServiceCollectionExtensions.LoginClientName));

        try
        {
            var response = await api.LoginAsync(new LoginRequest
            {
                Email = email,
                Password = password,
                LocationCode = "PL",
            }, ct);

            if (response.FailedLogin || string.IsNullOrWhiteSpace(response.AuthToken))
            {
                logger?.LogInformation("APCOA login rejected the credentials (FailedLogin flag or empty token).");
                return LoginOutcome.Bad("FailedLogin flag or empty token");
            }

            return LoginOutcome.Success(response.AuthToken);
        }
        catch (ApiException ex) when (IsCredentialRejection(ex.StatusCode))
        {
            logger?.LogInformation("APCOA login rejected the credentials (HTTP {Status}).", (int)ex.StatusCode);
            return LoginOutcome.Bad($"HTTP {(int)ex.StatusCode}");
        }
        catch (ApiException ex)
        {
            // 419 (invalid/expired token), 5xx, etc. — an API problem, NOT a wrong password.
            logger?.LogWarning("APCOA login failed with an API error (HTTP {Status}); not a credential failure.", (int)ex.StatusCode);
            return LoginOutcome.Api($"HTTP {(int)ex.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            logger?.LogWarning(ex, "APCOA login failed with a transport error; treating as an API error.");
            return LoginOutcome.Api("transport error");
        }
    }

    // A credential rejection is an auth/validation status on the login call itself. 419 is explicitly
    // NOT here: APCOA uses it for invalid/expired token, which is an API condition, not a bad password.
    private static bool IsCredentialRejection(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity;
}

/// <summary>Default <see cref="IApcoaFlowLoginValidator"/> backed by <see cref="ApcoaFlowLogin"/>.</summary>
public sealed class ApcoaFlowLoginValidator(
    IHttpClientFactory httpClientFactory, ILogger<ApcoaFlowLoginValidator> logger) : IApcoaFlowLoginValidator
{
    public Task<LoginOutcome> ValidateAsync(string email, string password, CancellationToken ct = default) =>
        ApcoaFlowLogin.TryLoginAsync(httpClientFactory, email, password, logger, ct);
}
