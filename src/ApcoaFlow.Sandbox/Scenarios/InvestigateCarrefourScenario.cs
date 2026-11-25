using ApcoaFlow.Sandbox.Dumping;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Parking;
using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Tariff;
using ApcoaFlowApiClient.ParkingData;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: READ-ONLY investigation of Carrefour Tarnowskie Góry (parking 9882, ul. Kościuszki 5) and
/// its coupon. For each bookable lot it dumps: lot + tariff config, applicable offers, promotion/validate
/// for any free-time coupon, and tariff breakdowns at 30/60/90/120 min — both WITHOUT and WITH the promo.
/// Starts / extends / stops NOTHING. Safe on the personal account.
///
/// WHY: the user asked for a 1-hour (free) session here via the MCP tools and the tool quoted "6 PLN for
/// 120 min" with no free time. This scenario captures the real wire traffic to explain both numbers.
///
/// FOUND (2026-11-25): (1) the API prices 60 min at 2 PLN — there is NO 120-min lot minimum; the MCP
/// tool had INFLATED the 60-min request to the coupon's 120-min free window, landing on the 6 PLN tier.
/// (2) The coupon FREE120PL2_4H is per-person, one use / 4 h; when on cooldown promotion/validate returns
/// 422 INVALID_PROMOTION_CODE/5007 and tariffBreakDownV2 "with promo" only applies a 10% discount (never
/// the free window, freeTimeSlot:[]). This drove the fix to validate a promo BEFORE expanding the
/// duration. See memory: project_carrefour_9882_promo.
/// </summary>
public sealed class InvestigateCarrefourScenario : IScenario
{
    public string Name => "investigate-carrefour";
    public string Description => "Read-only: Carrefour Tarnowskie Góry (parking 9882) lots, tariff, coupon, and 60/120-min breakdowns.";

    // Confirmed from the MCP chat: Carrefour Tarnowskie Góry ul. Kościuszki 5.
    private const int CarrefourParkingId = 9882;

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();
        var data = services.GetRequiredService<IApcoaParkingDataSource>();

        var consumer = await api.GetConsumerAsync(ct: ct);
        // The MCP chat started the session for plate STA 0521F — prefer that exact plate if the
        // account has it, otherwise fall back to the default vehicle.
        var plate = consumer.VehicleInformationResponses
                        .FirstOrDefault(v => v.VehicleNumber == "STA 0521F")?.VehicleNumber
                    ?? consumer.VehicleInformationResponses.First(v => v.IsDefault == 1).VehicleNumber;
        run.Log($"consumer id={consumer.Id} plate='{plate}' " +
                $"vehicles=[{string.Join(", ", consumer.VehicleInformationResponses.Select(v => v.VehicleNumber))}] " +
                $"paymentMethods={consumer.ConsumerUserPaymentMethods.Count}");

        var entry = await data.GetByIdAsync(CarrefourParkingId, ct);
        run.Log(entry is null
            ? $"parking {CarrefourParkingId} not in offline dataset — querying lots directly anyway."
            : $"parking {CarrefourParkingId} '{entry.DisplayName}' {entry.City} ({entry.Latitude},{entry.Longitude})");

        await InvestigateParkingAsync(api, CarrefourParkingId, entry?.DisplayName ?? "(unknown)", plate, consumer.Id, run, ct);

        run.Log("investigation complete — read the NNNN_*.http files for full request/response bodies.");
    }

    private static async Task InvestigateParkingAsync(
        IApcoaFlowApi api, int parkingId, string name, string plate, int consumerId, DumpRun run, CancellationToken ct)
    {
        run.Log($"───── parking {parkingId} '{name}' ─────");

        List<ParkingLot> lots;
        try { lots = await api.GetParkingLotsAsync(parkingId, ct: ct); }
        catch (ApiException ex)
        {
            run.Log($"  GetParkingLots failed HTTP {(int)ex.StatusCode} — see dump.");
            return;
        }

        var lot = lots.FirstOrDefault(l => l.LotId is not null && l.PrimaryTariffId is not null);
        if (lot?.LotId is null || lot.PrimaryTariffId is null)
        {
            run.Log("  no bookable lot/tariff.");
            return;
        }

        var lotId = lot.LotId.Value;
        var tariffId = lot.PrimaryTariffId.Value;
        var cfg = lot.PrimaryConfig;
        run.Log($"  lot={lotId} tariff={tariffId} min={cfg?.MinDuration} ext={cfg?.MinExtension} max={lot.MaxDuration}");

        // Applicable offers (the coupon).
        var offers = await api.GetApplicableOffersV2Async(new ApplicableOffersV2Request
        {
            ParkingLot = lotId, UserId = consumerId, Company = "PL",
        }, ct);
        run.Log($"  offers: {offers.Entities.Count}");
        foreach (var o in offers.Entities)
            run.Log($"    offer id={o.Id} name='{o.Name}' active={o.IsActive} autoApply={o.AutoApply} " +
                    $"concession={o.ConcessionType} freeWindow={o.FreeWindow} freeMin={o.FreeMinutes} " +
                    $"discountType={o.OfferDiscountType} starts={o.OfferStartsAt:u} expires={o.OfferExpiresAt:u}");

        var promo = offers.Entities.FirstOrDefault(o => o.IsActiveFreeTime)
                    ?? offers.Entities.FirstOrDefault();
        var now = DateTimeOffset.UtcNow;

        // Validate-promotion for the coupon (the authoritative "can I use it now" check).
        if (promo is not null)
        {
            var validate = await api.ValidatePromotionAsync(new ValidatePromotionRequest
            {
                ConsumerID = consumerId,
                VehicleNumber = plate,
                PromotionName = promo.Name ?? string.Empty,
                Company = "PL",
                ParkingLot = lotId,
                StartTime = now,
                DurationValue = TimeSpan.FromMinutes(120),
            }, ct);
            run.Log($"  validate '{promo.Name}': success={validate.IsSuccessStatusCode} http={validate.StatusCode} " +
                    $"msg={validate.Content?.Message} code={validate.Content?.StatusCode} " +
                    $"err={(validate.Error as ApiException)?.Content}");
        }

        // Breakdowns across several durations, no-promo then with-promo. The user reported a 60-min
        // request coming back as 6 PLN/120 min; probing 30/60/90/120 reveals the pricing granularity
        // and whether duration is being snapped to a tariff block.
        foreach (var minutes in new[] { 30, 60, 90, 120 })
        {
            var window = TimeSpan.FromMinutes(minutes);

            var noPromo = await Quote(api, lotId, tariffId, window, now, plate, consumerId, null);
            run.Log($"  breakdown {minutes} min NO promo: total={noPromo.TotalPayablePln:0.00} {noPromo.CurrencyCode} " +
                    $"(cents={noPromo.TotalPayableAmountInCents}, noDisc={noPromo.TotalPayableAmountInCentsWithoutDiscount}, " +
                    $"discApplicable={noPromo.DiscountApplicable}, discAmt={noPromo.TotalDiscountAmount}, " +
                    $"freeApplied={noPromo.FreeDurationApplied}, freeDur={noPromo.FreeDuration}, dur={noPromo.Duration})");

            if (promo is not null)
            {
                var withPromo = await Quote(api, lotId, tariffId, window, now, plate, consumerId, promo);
                run.Log($"  breakdown {minutes} min WITH '{promo.Name}': total={withPromo.TotalPayablePln:0.00} {withPromo.CurrencyCode} " +
                        $"(cents={withPromo.TotalPayableAmountInCents}, noDisc={withPromo.TotalPayableAmountInCentsWithoutDiscount}, " +
                        $"discApplicable={withPromo.DiscountApplicable}, discAmt={withPromo.TotalDiscountAmount}, " +
                        $"freeApplied={withPromo.FreeDurationApplied}, freeDur={withPromo.FreeDuration}, dur={withPromo.Duration})");
            }
        }
    }

    private static Task<TariffBreakdown> Quote(
        IApcoaFlowApi api, int lotId, int tariffId, TimeSpan window,
        DateTimeOffset entry, string plate, int consumerId, Offer? promo) =>
        api.GetTariffBreakdownAsync(TariffBreakdownQuery.For(
            lotId, tariffId, entry, window, plate,
            promotionId: promo?.Id,
            discountName: promo?.Name,
            offerDiscountType: promo is null ? null : "PROMOTION",
            consumerUserId: consumerId));
}
