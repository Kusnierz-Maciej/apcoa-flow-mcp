using System.Net.Http.Headers;
using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Auth;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: Login probe — three logins on the dumped login client (the real credentials, the real email with a
/// wrong password, an unknown email), then one authenticated GET with an invalid bearer token. Touches no data.
///
/// WHY: the login outcome classifier (<see cref="ApcoaFlowLogin"/>: bad credentials vs API error), the OAuth
/// sign-in form and the auth handler's re-login trigger (401/419) all rely on exactly which status code and
/// body APCOA returns. Live contract tests pin the result.
///
/// FOUND: see the dated notes in docs/API-NOTES.md ("Login and token rejection").
/// Run sparingly — repeated failed logins could trip account protection.
/// </summary>
public sealed class LoginProbeScenario : IScenario
{
    public string Name => "login-probe";
    public string Description => "Logins: valid, wrong password, unknown email; invalid bearer on GET consumer (no data touched).";

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var options = services.GetRequiredService<IApcoaFlowCredentialProvider>().GetCredentials();
        var factory = services.GetRequiredService<IHttpClientFactory>();
        var api = RestService.For<IApcoaFlowApi>(factory.CreateClient(ServiceCollectionExtensions.LoginClientName));

        await TryAsync(run, "valid", () => api.LoginAsync(Login(options.Email, options.Password), ct));
        await TryAsync(run, "wrong-password", () => api.LoginAsync(Login(options.Email, options.Password + "-wrong"), ct));
        await TryAsync(run, "unknown-email", () => api.LoginAsync(
            Login($"nobody-{Guid.NewGuid():N}@example.invalid", "x-wrong-password"), ct));

        // Invalid bearer on an authenticated endpoint, bypassing the auth handler (no re-login).
        using var http = factory.CreateClient(ServiceCollectionExtensions.LoginClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/authentication/v1/consumer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token-" + Guid.NewGuid().ToString("N"));
        using var response = await http.SendAsync(request, ct);
        run.Log($"invalid-bearer GET consumer: HTTP {(int)response.StatusCode} body={await response.Content.ReadAsStringAsync(ct)}");
    }

    private static LoginRequest Login(string email, string password) =>
        new() { Email = email, Password = password, LocationCode = "PL" };

    private static async Task TryAsync(DumpRun run, string label, Func<Task<LoginResponse>> login)
    {
        try
        {
            var r = await login();
            run.Log($"{label}: 2xx failedLogin={r.FailedLogin} authToken={(string.IsNullOrEmpty(r.AuthToken) ? "EMPTY" : "present")} " +
                    $"ttl={r.Ttl} failedLoginResponse={System.Text.Json.JsonSerializer.Serialize(r.FailedLoginResponse)}");
        }
        catch (ApiException ex)
        {
            run.Log($"{label}: HTTP {(int)ex.StatusCode} {ex.StatusCode} body={ex.Content}");
        }
    }
}
