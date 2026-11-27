using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Auth;
using ApcoaFlow.Sandbox.Dumping;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: the auth flow the client relies on, step by step: password grant with the account e-mail, refresh grant
/// (rotation, old refresh token reuse), an API call with the new token, then the password grant with the account's
/// phone number (E.164, from <c>users/profile</c>). Read-only; no wrong-password attempt (lockout policy unknown).
/// WHY: e-mail login is the MCP's credential model; whether it needs a verified e-mail is open (PLAN Next 0a).
/// FOUND (2026-11-27, private account): both grants 200; refresh rotates, old refresh token still accepted once;
/// password grant ~7.2 s server-side, refresh ~0.2 s.
/// </summary>
public sealed class LoginScenario : IScenario
{
    public string Name => "login";
    public string Description => "Auth flow: e-mail password grant, refresh (rotation), API call, phone password grant. Read-only.";

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var tokens = services.GetRequiredService<TokenClient>();
        var credentials = services.GetRequiredService<ApcoaFlowCredentials>();
        var api = services.GetRequiredService<IApcoaFlowApi>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var login = await tokens.PasswordGrantAsync(credentials, ct);
        run.Log($"password grant (e-mail): {login.Kind} {login.Detail} in {sw.ElapsedMilliseconds} ms; " +
                $"expires {login.Tokens?.ExpiresAt:u}, refresh token {(login.Tokens?.RefreshToken is null ? "missing" : "present")}");
        if (login.Kind != TokenGrantOutcomeKind.Ok)
        {
            run.Log("e-mail login failed: if the e-mail isn't verified yet, that's the answer to PLAN Next 0a. Stopping.");
            return;
        }

        sw.Restart();
        var refreshed = await tokens.RefreshGrantAsync(login.Tokens!.RefreshToken!, ct);
        run.Log($"refresh grant: {refreshed.Kind} {refreshed.Detail} in {sw.ElapsedMilliseconds} ms; " +
                $"refresh token rotated: {refreshed.Tokens?.RefreshToken != login.Tokens.RefreshToken}");

        var reuse = await tokens.RefreshGrantAsync(login.Tokens.RefreshToken!, ct);
        run.Log($"refresh grant with the OLD refresh token again: {reuse.Kind} {reuse.Detail}");

        // Through the real pipeline (its own token store login), to see the API accept a token.
        var profile = await api.GetUserProfileAsync(ct);
        run.Log($"users/profile: userId={profile.UserId}, email={profile.Email?.Address ?? "(none)"} status={profile.Email?.Status ?? "(none)"}, " +
                $"loginMethods=[{string.Join(", ", profile.LoginMethods)}], phone={(profile.PhoneNumber is null ? "(none)" : "set")}, " +
                $"termsToAccept=[{string.Join(", ", profile.TermsToAccept.Select(t => $"{t.Category ?? t.TermsId}{(t.Required ? " (required)" : "")}"))}]");

        if (profile.PhoneNumber is { } phone)
        {
            sw.Restart();
            var byPhone = await tokens.PasswordGrantAsync(credentials with { Username = phone }, ct);
            run.Log($"password grant (phone, E.164): {byPhone.Kind} {byPhone.Detail} in {sw.ElapsedMilliseconds} ms");
        }
    }
}
