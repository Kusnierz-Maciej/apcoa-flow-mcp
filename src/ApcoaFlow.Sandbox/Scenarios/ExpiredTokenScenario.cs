using System.Net.Http.Headers;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Auth;
using ApcoaFlow.Sandbox.Dumping;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: signs in once, then calls <c>users/profile</c> with that same access token right away, at 59 min and at
/// 65 min (past its 1 h lifetime), bypassing the client's token store so nothing renews it. Read-only. Takes
/// ~66 minutes: run it in the background.
/// WHY: the auth handler treats <c>403 Unauthenticated</c> and 401 as "token refused"; this shows which one an
/// expired token gets, and whether the server allows any clock skew.
/// FOUND (2026-11-27): fresh 200; at 59.5 min 200; at 65 min <c>403 {"code":"Unauthenticated","message":"An error
/// has occurred."}</c>, no WWW-Authenticate: the same answer as no token at all.
/// </summary>
public sealed class ExpiredTokenScenario : IScenario
{
    public string Name => "expired-token";
    public string Description => "What an expired access token gets from the API (waits ~66 min). Read-only.";

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var login = await services.GetRequiredService<TokenClient>()
            .PasswordGrantAsync(services.GetRequiredService<ApcoaFlowCredentials>(), ct);
        if (login.Kind != TokenGrantOutcomeKind.Ok)
            throw new InvalidOperationException($"sign-in failed: {login.Kind} {login.Detail}");
        var token = login.Tokens!.AccessToken;
        var issued = DateTimeOffset.UtcNow;
        run.Log($"signed in; token expires {login.Tokens.ExpiresAt:u}");

        // A plain client with only the dump handler: no auth handler, so the old token is sent as is.
        using var http = new HttpClient(new DumpingHttpHandler(run) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(ApcoaFlowEndpoints.ApiBaseUrl),
        };

        async Task Call(string label)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/profile");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            foreach (var (name, value) in ApcoaFlowEndpoints.AppHeaders)
                request.Headers.TryAddWithoutValidation(name, value);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            run.Log($"{label} (token age {(DateTimeOffset.UtcNow - issued).TotalMinutes:0.0} min): HTTP {(int)response.StatusCode} " +
                    $"www-authenticate=[{string.Join(", ", response.Headers.WwwAuthenticate)}] body={body[..Math.Min(200, body.Length)]}");
        }

        await Call("fresh");
        await Task.Delay(issued.AddMinutes(59) - DateTimeOffset.UtcNow, ct);
        await Call("at 59 min");
        await Task.Delay(issued.AddMinutes(65) - DateTimeOffset.UtcNow, ct);
        await Call("at 65 min (expired)");
    }
}
