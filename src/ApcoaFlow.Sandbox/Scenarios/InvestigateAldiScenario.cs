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
/// WHAT: READ-ONLY investigation of Aldi Tarnowskie Góry (parking 8331, ul. Sobieskiego 3) and any
/// promotion on its bookable lot. Dumps lot + tariff config, ALL applicable offers (via the same
/// v2/applicableOffersWithPromotion endpoint the official app uses, keyed by plate), the nested
/// promotionResponseList, promotion/validate for the nested promotion code, and tariff breakdowns at
/// 30/60/90/120 min both WITHOUT and WITH each offer. Starts / extends / stops NOTHING.
///
/// WHY: the MCP reported "No free parking is available at parking 8331" for plate STA 0521 while the app
/// grants a free 60-min session. Captured from the phone HAR.
///
/// FOUND (2026-11-25): the offer "Darmowe60minutparkowania." (offerCode 4107) carries the REAL promotion
/// in its nested promotionResponseList: id=104539, name="FRE60MIN07", isPrimary=1. The MCP had been
/// sending the offer's display name/id to promotion/validate → 422 INVALID_PROMOTION_CODE/5007, then
/// discarding a perfectly usable promo. With the nested name/id the breakdown returns
/// totalPayableAmountInCents=0 (genuinely free). The app itself never calls promotion/validate — it
/// trusts the 0-cost breakdown. Fix: Offer.PromotionId/PromotionName read the nested promotion; the
/// session service uses them for validate/breakdown/start. A prior note blaming a 4h cooldown for the
/// Carrefour 422 is suspect — that 422 may be the same wrong-code bug.
/// </summary>
public sealed class InvestigateAldiScenario : IScenario
{
    public string Name => "investigate-aldi";
    public string Description => "Read-only: Aldi Tarnowskie Góry (parking 8331) lots, tariff, offers, validate, and 30/60/90/120-min breakdowns.";

    // From the MCP chat: Aldi Tarnowskie Góry ul. Sobieskiego 3.
    private const int AldiParkingId = 8331;

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();
        var data = services.GetRequiredService<IApcoaParkingDataSource>();

        var consumer = await api.GetConsumerAsync(ct: ct);
        var plate = consumer.VehicleInformationResponses
                        .FirstOrDefault(v => v.VehicleNumber == "STA 0521F")?.VehicleNumber
                    ?? consumer.VehicleInformationResponses.First(v => v.IsDefault == 1).VehicleNumber;
        run.Log($"consumer id={consumer.Id} plate='{plate}' " +
                $"vehicles=[{string.Join(", ", consumer.VehicleInformationResponses.Select(v => v.VehicleNumber))}]");

        var entry = await data.GetByIdAsync(AldiParkingId, ct);
        run.Log(entry is null
            ? $"parking {AldiParkingId} not in offline dataset — querying lots directly anyway."
            : $"parking {AldiParkingId} '{entry.DisplayName}' {entry.City} ({entry.Latitude},{entry.Longitude})");

        await InvestigateParkingAsync(api, AldiParkingId, entry?.DisplayName ?? "(unknown)", plate, consumer.Id, run, ct);

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

        // ALL applicable offers via the SAME endpoint + identity the app uses (plate-keyed). Log every
        // field the MCP keys on, plus the nested promotionResponseList that carries the real promo code.
        var offers = await api.GetApplicableOffersAsync(new ApplicableOffersRequest
        {
            ParkingLot = lotId, Identifier = plate, Company = "PL",
        }, ct);
        run.Log($"  offers: {offers.Count}");
        foreach (var o in offers)
        {
            run.Log($"    offer id={o.Id} name='{o.Name}' active={o.IsActive} display={o.IsDisplay} autoApply={o.AutoApply} " +
                    $"concession='{o.ConcessionType}' concessionValue={o.ConcessionValue} " +
                    $"isActiveFreeTime={o.IsActiveFreeTime} freeWindow={o.FreeWindow} freeMin={o.FreeMinutes} " +
                    $"discountType='{o.OfferDiscountType}' starts={o.OfferStartsAt:u} expires={o.OfferExpiresAt:u}");
            foreach (var p in o.PromotionResponseList)
                run.Log($"      nested promo id={p.Id} name='{p.Name}' offerCode={p.OfferCode} isPrimary={p.IsPrimary} " +
                        $"active={p.IsActive} usageType={p.UsageType} usageLimit={p.UsageLimit}");
            run.Log($"      => PromotionId={o.PromotionId} PromotionName='{o.PromotionName}'");
        }

        var now = DateTimeOffset.UtcNow;

        // Validate EVERY named offer using the NESTED promotion name/id (what the API accepts).
        foreach (var o in offers.Where(o => !string.IsNullOrWhiteSpace(o.PromotionName)))
        {
            var validate = await api.ValidatePromotionAsync(new ValidatePromotionRequest
            {
                ConsumerID = consumerId,
                VehicleNumber = plate,
                PromotionName = o.PromotionName ?? string.Empty,
                Company = "PL",
                ParkingLot = lotId,
                StartTime = now,
                DurationValue = o.FreeWindow ?? TimeSpan.FromMinutes(120),
            }, ct);
            run.Log($"  validate '{o.PromotionName}': success={validate.IsSuccessStatusCode} http={validate.StatusCode} " +
                    $"msg={validate.Content?.Message} code={validate.Content?.StatusCode} " +
                    $"err={(validate.Error as ApiException)?.Content}");
        }

        var primaryOffer = offers.FirstOrDefault(o => o.IsActiveFreeTime)
                           ?? offers.FirstOrDefault();

        foreach (var minutes in new[] { 30, 60, 90, 120 })
        {
            var window = TimeSpan.FromMinutes(minutes);

            var noPromo = await Quote(api, lotId, tariffId, window, now, plate, consumerId, null);
            run.Log($"  breakdown {minutes} min NO promo: total={noPromo.TotalPayablePln:0.00} {noPromo.CurrencyCode} " +
                    $"(cents={noPromo.TotalPayableAmountInCents}, noDisc={noPromo.TotalPayableAmountInCentsWithoutDiscount}, " +
                    $"discApplicable={noPromo.DiscountApplicable}, discAmt={noPromo.TotalDiscountAmount}, " +
                    $"freeApplied={noPromo.FreeDurationApplied}, freeDur={noPromo.FreeDuration}, dur={noPromo.Duration})");

            if (primaryOffer is not null)
            {
                var withPromo = await Quote(api, lotId, tariffId, window, now, plate, consumerId, primaryOffer);
                run.Log($"  breakdown {minutes} min WITH '{primaryOffer.Name}': total={withPromo.TotalPayablePln:0.00} {withPromo.CurrencyCode} " +
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
            promotionId: promo?.PromotionId,
            discountName: promo?.PromotionName,
            offerDiscountType: promo is null ? null : "PROMOTION",
            consumerUserId: consumerId));
}
