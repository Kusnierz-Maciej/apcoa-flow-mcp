using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Tariff;
using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Apcoa;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: exercises the REAL MCP free-start path (ApcoaFlowSessionService.StartAsync in OnlyFree mode)
/// against Aldi Tarnowskie Góry (parking 8331) for plate STA 0521. OnlyFree refuses unless the quote is
/// genuinely 0 PLN, so this cannot incur a charge — if the fix works it starts the free 60-min session,
/// otherwise it refuses with pricing. Dumps every HTTP exchange.
///
/// WHY: confirm end-to-end that the nested-promotion fix ([[project_nested_promotion_code]]) makes the
/// MCP recognise and start the free session the app grants at 8331.
///
/// SAFETY (defence in depth — three independent gates so a lapsed/removed promo can NEVER charge):
///   (1) an explicit pre-flight breakdown MUST quote 0 cents for the default window WITH a validated
///       promo, or the scenario aborts before any start;
///   (2) StartAsync is called in OnlyFree mode with maxCostPln=0 (it refuses any non-zero quote itself);
///   (3) after start, the session is ended immediately AND its final amount is asserted to be 0 — a
///       non-zero amount is logged as a loud error.
/// </summary>
public sealed class StartFreeAldiScenario : IScenario
{
    public string Name => "start-free-aldi";
    public string Description => "Free-only start of parking 8331 (Aldi TG) via the real MCP service; starts then immediately ends. Cannot charge.";

    private const int AldiParkingId = 8331;
    private const string Plate = "STA 0521";

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();
        var data = services.GetRequiredService<IApcoaParkingDataSource>();
        var sessions = new ApcoaFlowSessionService(api, data);

        // ── Gate 1: independent pre-flight. Resolve lot/tariff, find + validate the free-time promo, and
        // quote the default window WITH it. Abort unless that quote is exactly 0 cents. This does not
        // trust StartAsync's own logic — if this fails, we never attempt a start.
        if (!await PreflightIsFreeAsync(api, run, ct))
        {
            run.Log("ABORT: pre-flight did not confirm a 0-cost quote for parking 8331 — NOT attempting a start.");
            return;
        }

        run.Log($"OnlyFree start attempt: parking {AldiParkingId}, plate '{Plate}' (OnlyFree refuses unless 0 PLN).");

        var result = await sessions.StartAsync(
            AldiParkingId, Plate, durationMinutes: null,
            ApcoaFlowSessionService.StartMode.OnlyFree,
            confirmedCostPln: null, maxCostPln: 0m, bypassCap: false, ct);

        run.Log($"result: started={result.Started} cost={result.CostPln:0.00} PLN dur={result.DurationMinutes}min " +
                $"promo='{result.PromotionName}' freeMin={result.FreeMinutes}");
        run.Log($"message: {result.Message}");
        run.Log($"cancelNote: {result.CancelRefundNote}");

        // Gate 2 belt-and-braces: StartAsync should never start a non-zero session in OnlyFree, but assert it.
        if (result.Started && result.CostPln != 0m)
            run.Log($"ERROR: OnlyFree start reported a NON-ZERO cost {result.CostPln:0.00} PLN — this must never happen.");

        if (result.Started && result.Session is { } started)
        {
            var sessionId = started.ParkingSession?.Id ?? started.Id;
            run.Log($"session {sessionId} started FREE — ending it immediately to leave nothing running.");
            var ended = await sessions.StopAsync(sessionId, ct);
            run.Log($"ended: id={ended?.Id} status={ended?.ParkingSessionStatus} amount={ended?.Amount}");

            // Gate 3: the completed session MUST have cost nothing.
            if (ended is { } e && e.Amount != 0m)
                run.Log($"ERROR: completed session {e.Id} was CHARGED {e.Amount} — expected 0. Investigate immediately.");
        }
        else if (result.Started)
        {
            run.Log("WARNING: started but no session payload returned — check get_active_sessions.");
        }
        else
        {
            run.Log("not started (refused) — nothing to clean up.");
        }

        run.Log("start-free-aldi complete.");
    }

    /// <summary>
    /// Independent money-guard: resolve the bookable lot/tariff, find the active free-time promo, validate
    /// it, and quote the default (60-min) window WITH the validated promo. Returns true ONLY when that
    /// quote is exactly 0 cents. Any missing promo, failed validate, non-zero quote, or API error → false.
    /// </summary>
    private static async Task<bool> PreflightIsFreeAsync(IApcoaFlowApi api, DumpRun run, CancellationToken ct)
    {
        try
        {
            var lots = await api.GetParkingLotsAsync(AldiParkingId, ct: ct);
            var lot = lots.FirstOrDefault(l => l.LotId is not null && l.PrimaryTariffId is not null);
            if (lot?.LotId is not { } lotId || lot.PrimaryTariffId is not { } tariffId)
            {
                run.Log("  preflight: no bookable lot/tariff.");
                return false;
            }

            var consumer = await api.GetConsumerAsync(ct: ct);
            var offers = await api.GetApplicableOffersAsync(new ApplicableOffersRequest
            {
                ParkingLot = lotId, Identifier = Plate, Company = "PL",
            }, ct);
            var promo = offers.FirstOrDefault(o => o.IsActiveFreeTime);
            if (promo is null)
            {
                run.Log("  preflight: no active free-time promo for this lot.");
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var window = promo.FreeWindow ?? TimeSpan.FromMinutes(ApcoaFlowSessionService.DefaultDurationMinutes);

            var validate = await api.ValidatePromotionAsync(new ValidatePromotionRequest
            {
                ConsumerID = consumer.Id,
                VehicleNumber = Plate,
                PromotionName = promo.PromotionName ?? string.Empty,
                Company = "PL",
                ParkingLot = lotId,
                StartTime = now,
                DurationValue = window,
            }, ct);
            if (!validate.IsSuccessStatusCode)
            {
                run.Log($"  preflight: validate failed HTTP {validate.StatusCode} — not free.");
                return false;
            }

            var quote = await api.GetTariffBreakdownAsync(TariffBreakdownQuery.For(
                lotId, tariffId, now, window, Plate,
                promotionId: promo.PromotionId, discountName: promo.PromotionName,
                offerDiscountType: "PROMOTION", consumerUserId: consumer.Id), ct);

            run.Log($"  preflight: promo='{promo.PromotionName}' window={window.TotalMinutes:0}min " +
                    $"quote={quote.TotalPayablePln:0.00} PLN (cents={quote.TotalPayableAmountInCents}).");

            return quote.TotalPayableAmountInCents == 0;
        }
        catch (Refit.ApiException ex)
        {
            run.Log($"  preflight: API error HTTP {(int)ex.StatusCode} — treating as NOT free.");
            return false;
        }
    }
}
