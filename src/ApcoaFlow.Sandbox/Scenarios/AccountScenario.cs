using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using ApcoaFlow.Sandbox.Dumping;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: account snapshot: profile, vehicles, payment methods (and whether the default one is usable by the MCP: a
/// saved card), running sessions, the latest history entries and the newest one's details. Strictly read-only.
/// WHY: the first thing to look at when a read tool misbehaves; also checks the test account's setup (car, card).
/// FOUND (2026-11-27, private account): vehicles carry plates without spaces; paymentMethods lists masterCard and
/// applePay (both <c>flow: saved</c>) plus a <c>hosted-checkout</c> pseudo-method.
/// </summary>
public sealed class AccountScenario : IScenario
{
    public string Name => "account";
    public string Description => "Account snapshot: profile, vehicles, payment methods, sessions, history. Read-only.";

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();

        var profile = await api.GetUserProfileAsync(ct);
        run.Log($"profile: email={profile.Email?.Address ?? "(none)"} ({profile.Email?.Status ?? "no status"}), " +
                $"loginMethods=[{string.Join(", ", profile.LoginMethods)}], " +
                $"required terms not accepted: {profile.TermsToAccept.Count(t => t.Required)}");

        var vehicles = await api.GetVehiclesAsync(ct);
        run.Log($"vehicles ({vehicles.Count}): " + string.Join("; ", vehicles.Select(v =>
            $"{v.LicensePlate} '{v.Description}' id={v.Id}{(v.IsDefault ? " [default]" : "")}")));

        var methods = await api.GetPaymentMethodsAsync(ct: ct);
        run.Log($"payment methods ({methods.Count}): " + string.Join("; ", methods.Select(m =>
            $"{m.Type} {m.Identifier} flow={m.Flow} action={m.RequiredAction} {m.Availability}{(m.IsDefault ? " [default]" : "")}")));
        var defaultMethod = methods.FirstOrDefault(m => m.IsDefault);
        run.Log(defaultMethod is null
            ? "default payment method: none → the MCP can't start sessions"
            : $"default payment method: {defaultMethod.Type} → usable by the MCP: {IsSavedCard(defaultMethod)}");

        var sessions = await api.GetSessionsAsync(ct);
        run.Log($"running sessions ({sessions.Count}): " + string.Join("; ", sessions.Select(s =>
            $"{s.Id} '{s.Space?.Name}' {s.Vehicle?.LicensePlate} {s.StartDate:u}→{s.EndDate:u} {s.PaymentFlow}")));

        var history = await api.GetSessionsHistoryAsync(ct: ct);
        run.Log($"history ({history.Count}): " + string.Join("; ", history.Select(h =>
            $"{h.Id} '{h.SpaceName}' {h.LicensePlate} {h.TotalPrice} {h.State}")));
        if (history.FirstOrDefault() is { } newest)
        {
            var details = await api.GetSessionsHistoryDetailsAsync(newest.Id, ct);
            run.Log($"newest history details: {details.StartDate:u}→{details.EndDate:u}, {details.TotalPrice}, " +
                    $"paid with {details.PaymentMethod?.Type} {details.PaymentMethod?.Identifier}, receipts {details.ReceiptUrls.Count}");
        }
    }

    /// <summary>D5: only the default method, and only a non-interactive saved card.</summary>
    internal static bool IsSavedCard(PaymentMethod m) =>
        m.Flow == "saved" && m.RequiredAction == "none" && m.Availability == "available" &&
        m.Type is not ("applePay" or "googlePay" or "unknown");
}
