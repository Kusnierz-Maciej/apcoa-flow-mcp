using System.Net;
using ApcoaFlowApiClient.Auth;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlowApiClient.Tests.Live;

/// <summary>
/// The API contract the client relies on, read-only part: login, account reads, error shapes that need no session.
/// One deliberate wrong-password login per run (lockout policy unknown; keep it to one).
/// </summary>
[Category("LiveApi")]
[NotInParallel(LiveApi.Serial)]
public class AccountContractTests
{
    [Before(HookType.Class)]
    public static Task SweepAtStart() => LiveApi.SweepOnceAtStartAsync();

    [After(HookType.Class)]
    public static Task Sweep() => LiveApi.EndActiveSessionsAsync("after AccountContractTests");

    [Test]
    public async Task Password_grant_with_the_email_returns_an_access_and_a_refresh_token()
    {
        var outcome = await LiveApi.Services.GetRequiredService<TokenClient>().PasswordGrantAsync(LiveApi.Credentials, default);

        await Assert.That(outcome.Kind).IsEqualTo(TokenGrantOutcomeKind.Ok);
        await Assert.That(outcome.Tokens!.RefreshToken).IsNotNull().Because("scope offline_access must yield a refresh token");
        await Assert.That(outcome.Tokens.ExpiresAt).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(30)).Because("access tokens live 1 h");
    }

    [Test]
    public async Task Refresh_grant_rotates_the_refresh_token()
    {
        var client = LiveApi.Services.GetRequiredService<TokenClient>();
        var login = await client.PasswordGrantAsync(LiveApi.Credentials, default);
        var refreshed = await client.RefreshGrantAsync(login.Tokens!.RefreshToken!, default);

        await Assert.That(refreshed.Kind).IsEqualTo(TokenGrantOutcomeKind.Ok);
        await Assert.That(refreshed.Tokens!.RefreshToken).IsNotEqualTo(login.Tokens.RefreshToken);
    }

    [Test]
    public async Task Wrong_password_is_400_invalid_grant()
    {
        var outcome = await LiveApi.Services.GetRequiredService<IApcoaFlowLoginValidator>()
            .ValidateAsync(LiveApi.Credentials with { Password = LiveApi.Credentials.Password + "-wrong" });

        await Assert.That(outcome.Kind).IsEqualTo(TokenGrantOutcomeKind.Rejected);
        await Assert.That(outcome.Detail).Contains("invalid_grant");
    }

    [Test]
    public async Task No_token_on_the_API_is_403_Unauthenticated()
    {
        using var http = new HttpClient { BaseAddress = new Uri(ApcoaFlowEndpoints.ApiBaseUrl) };
        using var response = await http.GetAsync("/api/v1/users/profile");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(ApcoaFlowApiError.TryParse(await response.Content.ReadAsStringAsync())?.Code)
            .IsEqualTo(ApcoaFlowApiError.Codes.Unauthenticated);
    }

    [Test]
    public async Task Profile_has_a_verified_email()
    {
        var profile = await LiveApi.Api.GetUserProfileAsync();

        await Assert.That(profile.UserId).IsNotEmpty();
        await Assert.That(profile.Email?.Address).IsEqualTo(LiveApi.Credentials.Username).Because("the account logs in with this e-mail");
        await Assert.That(profile.Email?.Status).IsEqualTo("approved");
    }

    [Test]
    public async Task Vehicles_carry_an_id_and_a_plate_without_spaces()
    {
        var car = LiveApi.FirstRegisteredCar(await LiveApi.Api.GetVehiclesAsync());

        await Assert.That(car.Id).IsNotEmpty().Because("the id is the licensePlateId that prices and startParking take");
        await Assert.That(car.LicensePlate).IsNotEmpty();
        await Assert.That(car.LicensePlate.Any(char.IsWhiteSpace)).IsFalse();
    }

    [Test]
    public async Task The_default_payment_method_is_a_saved_card()
    {
        var methods = await LiveApi.Api.GetPaymentMethodsAsync();
        var defaultMethod = methods.SingleOrDefault(m => m.IsDefault);

        await Assert.That(defaultMethod).IsNotNull().Because("the MCP pays with the default method only");
        await Assert.That(defaultMethod!.Flow).IsEqualTo("saved");
        await Assert.That(defaultMethod.RequiredAction).IsEqualTo("none");
        await Assert.That(defaultMethod.Availability).IsEqualTo("available");
    }

    [Test]
    public async Task Sessions_and_history_are_lists()
    {
        var sessions = await LiveApi.Api.GetSessionsAsync();
        var history = await LiveApi.Api.GetSessionsHistoryAsync();

        await Assert.That(sessions).IsNotNull();
        await Assert.That(history).IsNotNull();
    }

    [Test]
    public async Task Stopping_an_unknown_session_is_404_SessionNotFound()
    {
        var ex = await Assert.That(async () => await LiveApi.Api.StopParkingAsync(Guid.NewGuid().ToString())).Throws<ApiException>();

        await Assert.That(ex!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(ApcoaFlowApiError.TryRead(ex)?.Code).IsEqualTo(ApcoaFlowApiError.Codes.SessionNotFound);
    }
}
