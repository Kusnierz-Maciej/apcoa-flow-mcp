using System.Net;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Auth;
using Microsoft.Extensions.DependencyInjection;
using static ApcoaFlowApiClient.Tests.FakeNetwork;

namespace ApcoaFlowApiClient.Tests;

public class AuthTests
{
    private const string Token = "POST /connect/token";
    private const string Profile = "GET /api/v1/users/profile";
    private const string ProfileJson = """{"userId":"u1","loginMethods":["email"],"termsToAccept":[]}""";

    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The auth exception a caller finds inside Refit's wrapper (see <see cref="ApcoaFlowAuthErrors.Find{T}"/>).</summary>
    private static async Task<Exception?> CallFailure(IServiceProvider sp)
    {
        try { await sp.GetRequiredService<IApcoaFlowApi>().GetUserProfileAsync(); return null; }
        catch (Exception ex)
        {
            return (Exception?)ApcoaFlowAuthErrors.Find<ApcoaFlowCredentialsRejectedException>(ex)
                ?? ApcoaFlowAuthErrors.Find<ApcoaFlowAuthUnavailableException>(ex) ?? ex;
        }
    }

    [Test]
    public async Task First_call_logs_in_with_the_password_grant_and_sends_the_bearer_and_app_headers()
    {
        var net = new FakeNetwork().On(Token, Token("A1", "R1")).On(Profile, Reply(HttpStatusCode.OK, ProfileJson));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        await sp.GetRequiredService<IApcoaFlowApi>().GetUserProfileAsync();

        var login = net.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/connect/token");
        await Assert.That(login.Body).Contains("grant_type=password");
        await Assert.That(login.Body).Contains("client_id=flownextapp.prd.nucleus.valuespaces.io");
        await Assert.That(login.Body).Contains("scope=offline_access");
        var call = net.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/v1/users/profile").Request;
        await Assert.That(call.Headers.Authorization?.ToString()).IsEqualTo("Bearer A1");
        await Assert.That(call.Headers.GetValues("x-user-agent").Single()).IsEqualTo("Flow 26.12.1 (Web)");
        await Assert.That(call.Headers.GetValues("X-Timezone-Offset").Single()).IsEqualTo("60"); // Warsaw in November
    }

    [Test]
    public async Task A_fresh_token_is_reused_across_calls()
    {
        var net = new FakeNetwork().On(Token, Token("A1", "R1")).On(Profile, Reply(HttpStatusCode.OK, ProfileJson));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));
        var api = sp.GetRequiredService<IApcoaFlowApi>();

        await api.GetUserProfileAsync();
        await api.GetUserProfileAsync();

        await Assert.That(net.Count(Token)).IsEqualTo(1);
    }

    [Test]
    public async Task An_expiring_token_is_renewed_with_the_refresh_token()
    {
        var net = new FakeNetwork().On(Token, Token("A1", "R1"), Token("A2", "R2")).On(Profile, Reply(HttpStatusCode.OK, ProfileJson));
        var clock = new ManualTimeProvider(T0);
        using var sp = TestClient.Build(net, clock);
        var api = sp.GetRequiredService<IApcoaFlowApi>();

        await api.GetUserProfileAsync();
        clock.Advance(TimeSpan.FromMinutes(59)); // inside the renewal margin
        await api.GetUserProfileAsync();

        var grants = net.Requests.Where(r => r.Request.RequestUri!.AbsolutePath == "/connect/token").Select(r => r.Body).ToList();
        await Assert.That(grants.Count).IsEqualTo(2);
        await Assert.That(grants[1]).Contains("grant_type=refresh_token");
        await Assert.That(grants[1]).Contains("refresh_token=R1");
        await Assert.That(net.Requests.Last().Request.Headers.Authorization?.ToString()).IsEqualTo("Bearer A2");
    }

    [Test]
    public async Task A_dead_refresh_token_falls_back_to_a_new_login()
    {
        var net = new FakeNetwork().On(Token, Token("A1", "R1"), InvalidGrant, Token("A3", "R3"))
            .On(Profile, Reply(HttpStatusCode.OK, ProfileJson));
        var clock = new ManualTimeProvider(T0);
        using var sp = TestClient.Build(net, clock);
        var api = sp.GetRequiredService<IApcoaFlowApi>();

        await api.GetUserProfileAsync();
        clock.Advance(TimeSpan.FromHours(2));
        await api.GetUserProfileAsync();

        var grants = net.Requests.Where(r => r.Request.RequestUri!.AbsolutePath == "/connect/token").Select(r => r.Body!).ToList();
        await Assert.That(grants.Count).IsEqualTo(3);
        await Assert.That(grants[2]).Contains("grant_type=password");
        await Assert.That(net.Requests.Last().Request.Headers.Authorization?.ToString()).IsEqualTo("Bearer A3");
    }

    [Test]
    public async Task A_refused_token_is_renewed_and_the_call_retried_once()
    {
        var net = new FakeNetwork().On(Token, Token("A1", "R1"), Token("A2", "R2"))
            .On(Profile, Unauthenticated, Reply(HttpStatusCode.OK, ProfileJson));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        var profile = await sp.GetRequiredService<IApcoaFlowApi>().GetUserProfileAsync();

        await Assert.That(profile.UserId).IsEqualTo("u1");
        await Assert.That(net.Count(Profile)).IsEqualTo(2);
        await Assert.That(net.Requests.Last().Request.Headers.Authorization?.ToString()).IsEqualTo("Bearer A2");
    }

    [Test]
    public async Task A_fresh_token_refused_again_means_the_account_is_refused()
    {
        var net = new FakeNetwork().On(Token, Token("A1", "R1"), Token("A2", "R2")).On(Profile, Unauthenticated);
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        await Assert.That(await CallFailure(sp)).IsTypeOf<ApcoaFlowCredentialsRejectedException>();
        await Assert.That(net.Count(Profile)).IsEqualTo(2);
    }

    [Test]
    public async Task A_403_that_is_not_Unauthenticated_is_not_retried()
    {
        var net = new FakeNetwork().On(Token, Token("A1", "R1"))
            .On(Profile, Reply(HttpStatusCode.Forbidden, """{"code":"Forbidden","message":"no"}"""));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        await Assert.That(async () => { await sp.GetRequiredService<IApcoaFlowApi>().GetUserProfileAsync(); }).Throws<Refit.ApiException>();
        await Assert.That(net.Count(Profile)).IsEqualTo(1);
        await Assert.That(net.Count(Token)).IsEqualTo(1);
    }

    [Test]
    public async Task Wrong_password_throws_credentials_rejected()
    {
        var net = new FakeNetwork().On(Token, InvalidGrant);
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        await Assert.That(await CallFailure(sp)).IsTypeOf<ApcoaFlowCredentialsRejectedException>();
        await Assert.That(net.Count(Profile)).IsEqualTo(0);
    }

    [Test]
    public async Task Auth_server_errors_are_not_reported_as_bad_credentials()
    {
        var net = new FakeNetwork().On(Token, Reply(HttpStatusCode.ServiceUnavailable));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        await Assert.That(await CallFailure(sp)).IsTypeOf<ApcoaFlowAuthUnavailableException>();
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""", TokenGrantOutcomeKind.Rejected)]
    [Arguments(HttpStatusCode.BadRequest, """{"error":"unauthorized_client"}""", TokenGrantOutcomeKind.ApiError)]
    [Arguments(HttpStatusCode.InternalServerError, "<html>", TokenGrantOutcomeKind.ApiError)]
    public async Task Sign_in_validation_tells_a_wrong_password_from_an_API_problem(
        HttpStatusCode status, string body, TokenGrantOutcomeKind expected)
    {
        var net = new FakeNetwork().On(Token, _ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        var outcome = await sp.GetRequiredService<IApcoaFlowLoginValidator>().ValidateAsync(TestClient.Account);

        await Assert.That(outcome.Kind).IsEqualTo(expected);
    }

    [Test]
    public async Task Concurrent_calls_share_one_login()
    {
        var net = new FakeNetwork().On(Token, Token("A1", "R1")).On(Profile, Reply(HttpStatusCode.OK, ProfileJson));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));
        var api = sp.GetRequiredService<IApcoaFlowApi>();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => api.GetUserProfileAsync()));

        await Assert.That(net.Count(Token)).IsEqualTo(1);
    }
}
