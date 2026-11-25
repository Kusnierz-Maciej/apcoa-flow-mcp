using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowApiClient.Models.Tariff;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: WRITE scenario (BILLABLE, user-authorised). Starts ONE real paid 60-min session at Carrefour
/// Tarnowskie Góry (parking 9882), captures the genuine start AND stop request/response on the wire,
/// then IMMEDIATELY ends it. Expected cost ~2.00 PLN. Money-guard: re-quotes first and ABORTS without
/// starting if the quote exceeds <see cref="MaxAllowedPln"/> (3.00); the session is ended in all paths.
///
/// WHY: to build the MCP paid-start/stop logic against verified wire shapes rather than guesses.
///
/// FOUND (2026-11-25, session 84290864): a start CAPTUREs money immediately (response transactionStatus
/// SUCCESSFUL, transactionAmount in CENTS — 200.0 = 2.00 PLN); ending early does NOT refund
/// (parkingLotConfiguration.allowRefund:0, isProrationAllowed:0 — stop allowed but remainder forfeited);
/// the stop response's real amount is nested at parkingSession.amount, not the null top-level
/// transactionAmount. See memory: project_start_stop_wire_facts.
///
/// NOT a routine scenario — run deliberately; each run spends real money on a non-refundable lot.
/// </summary>
public sealed class CaptureCarrefourPaidStartScenario : IScenario
{
    public string Name => "capture-carrefour-paid-start";
    public string Description => "BILLABLE (~2 PLN): start+immediately-end a real 60-min paid session at Carrefour 9882 to capture wire shapes.";

    private const int ParkingId = 9882;
    private const int DurationMinutes = 60;
    private const decimal MaxAllowedPln = 3.00m;

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();

        var consumer = await api.GetConsumerAsync(ct: ct);
        var vehicle = consumer.VehicleInformationResponses.First(v => v.IsDefault == 1);
        var payment = consumer.ConsumerUserPaymentMethods.First(p => p.IsDefault == 1);
        if (consumer.DefaultProfileId is not { } profileId)
        {
            run.Log("no default profile — aborting.");
            return;
        }
        run.Log($"consumer={consumer.Id} plate='{vehicle.VehicleNumber}' payment={payment.PaymentType}(id {payment.Id}) profile={profileId}");

        var lots = await api.GetParkingLotsAsync(ParkingId, ct: ct);
        var lot = lots.First(l => l.LotId is not null && l.PrimaryTariffId is not null);
        var lotId = lot.LotId!.Value;
        var tariffId = lot.PrimaryTariffId!.Value;

        var checkIn = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromMinutes(DurationMinutes);
        var checkOut = checkIn.Add(window);

        // Money-guard: quote (NO promo) and refuse to start if above the ceiling.
        var quote = await api.GetTariffBreakdownAsync(TariffBreakdownQuery.For(
            lotId, tariffId, checkIn, window, vehicle.VehicleNumber,
            consumerUserId: consumer.Id), ct);
        run.Log($"money-guard: {DurationMinutes} min quotes {quote.TotalPayablePln:0.00} {quote.CurrencyCode} (cents={quote.TotalPayableAmountInCents}).");
        if (quote.TotalPayablePln > MaxAllowedPln)
        {
            run.Log($"ABORT: quote {quote.TotalPayablePln:0.00} PLN exceeds guard {MaxAllowedPln:0.00} PLN — NOT starting.");
            return;
        }

        run.Log($"starting PAID session: parking {ParkingId}, {DurationMinutes} min, checkout {checkOut:u}…");
        StartSessionResponse start;
        try
        {
            start = await api.StartParkingSessionAsync(new StartParkingSessionRequest
            {
                TariffId = tariffId,
                ParkingId = ParkingId,
                ParkingLotId = lotId,
                VehicleNumber = vehicle.VehicleNumber,
                PaymentMethod = payment.PaymentType ?? "BLIK",
                ConsumerUserPaymentId = payment.Id,
                ConsumerProfileId = profileId,
                SessionCheckInTime = checkIn,
                ScheduledSessionCheckInTime = checkIn,
                ScheduledSessionCheckOutTime = checkOut,
            }, ct);
        }
        catch (ApiException ex)
        {
            run.Log($"START failed HTTP {(int)ex.StatusCode}: {ex.Content} — see dump.");
            return;
        }

        run.Log($"STARTED: id={start.SessionId} status={start.ParkingSessionStatus} tx={start.TransactionStatus} charged={start.TransactionAmount:0.00} {start.CurrencyCode}");

        // End it immediately, no matter what.
        try
        {
            var stop = await api.StopParkingSessionAsync(new StopParkingSessionRequest
            {
                ParkingSessionId = start.SessionId,
                SessionCheckOutTime = DateTimeOffset.UtcNow,
            }, ct);
            run.Log($"ENDED: id={stop.Id} status={stop.ParkingSessionStatus} amount={stop.Amount:0.00} {stop.CurrencyCode}");
        }
        catch (ApiException ex)
        {
            run.Log($"STOP failed HTTP {(int)ex.StatusCode}: {ex.Content} — session {start.SessionId} may still be active, check get-sessions!");
            return;
        }

        run.Log("capture complete — read the start (POST parkingSession) and stop (PUT parkingSession) .http files.");
    }
}
