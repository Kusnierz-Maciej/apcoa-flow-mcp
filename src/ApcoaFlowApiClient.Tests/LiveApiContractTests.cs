using System.Net;
using System.Net.Http.Headers;
using ApcoaFlowApiClient.Models.Auth;
using ApcoaFlowApiClient.Models.Session;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlowApiClient.Tests;

/// <summary>
/// Live contract tests: each pins one APCOA behaviour the server's code relies on (see the "Contracts guarded
/// by live tests" table in docs/API-NOTES.md). If one fails, APCOA changed — re-probe with the sandbox
/// (<c>login-probe</c>) and adapt the code, not just the test.
/// Wrong-password logins count towards APCOA's lockout (5 attempts per 2 minutes): this class makes one.
/// </summary>
[Category("LiveApi")]
[NotInParallel(LiveApi.Serial)]
public class LiveApiContractTests
{
    [Before(HookType.Class)]
    public static Task SweepBefore() => LiveApi.SweepOnceAtStartAsync();

    [After(HookType.Class)]
    public static Task SweepAfter() => LiveApi.EndActiveSessionsAsync("after-class");

    private static IHttpClientFactory Factory => ApiClientFactory.Services.GetRequiredService<IHttpClientFactory>();

    private static IApcoaFlowApi LoginApi =>
        RestService.For<IApcoaFlowApi>(Factory.CreateClient(ServiceCollectionExtensions.LoginClientName));

    // ─── Login classification (ApcoaFlowLogin, OAuth sign-in form, auth handler) ───────────────────────

    [Test]
    public async Task Login_ValidCredentials_IsOk_WithToken()
    {
        var o = ApiClientFactory.Options;
        var outcome = await ApcoaFlowLogin.TryLoginAsync(Factory, o.Email, o.Password, null, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(LoginOutcomeKind.Ok);
        await Assert.That(outcome.Token).IsNotNull().And.IsNotEmpty();
    }

    [Test]
    public async Task Login_WrongPassword_Is200_WithFailedLoginFlag_AndNoToken()
    {
        // APCOA rejects a bad login with HTTP 200 + failedLogin=true (not 401) — the classifier must read the
        // flag. The failedLoginResponse carries the lockout policy.
        var o = ApiClientFactory.Options;
        var response = await LoginApi.LoginAsync(
            new LoginRequest { Email = o.Email, Password = o.Password + "-wrong", LocationCode = "PL" });

        await Assert.That(response.FailedLogin).IsTrue();
        await Assert.That(response.AuthToken).IsNull().Or.IsEmpty();
        await Assert.That(ApcoaFlowLogin.Classify(response).Kind).IsEqualTo(LoginOutcomeKind.BadCredentials);
    }

    [Test]
    public async Task Login_UnknownEmail_IsBadCredentials()
    {
        var outcome = await ApcoaFlowLogin.TryLoginAsync(
            Factory, $"nobody-{Guid.NewGuid():N}@example.invalid", "x-wrong-password", null, CancellationToken.None);

        await Assert.That(outcome.Kind).IsEqualTo(LoginOutcomeKind.BadCredentials);
    }

    [Test]
    public async Task InvalidBearerToken_Is419()
    {
        // The auth handler re-logs in once on 401/419; APCOA uses 419 "Invalid or Expired Token".
        using var http = Factory.CreateClient(ServiceCollectionExtensions.LoginClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/authentication/v1/consumer");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-" + Guid.NewGuid().ToString("N"));
        using var response = await http.SendAsync(request);

        await Assert.That((int)response.StatusCode).IsEqualTo(419);
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Invalid or Expired Token");
    }
}
