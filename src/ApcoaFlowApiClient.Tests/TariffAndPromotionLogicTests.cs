using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Tariff;

namespace ApcoaFlowApiClient.Tests;

public class TariffAndPromotionLogicTests
{
    [Test]
    public async Task Offer_DeserializesNestedPromotion_AndPrefersItOverOfferCode()
    {
        // Trimmed real response from v2/applicableOffersWithPromotion (Aldi 8331, HAR 2026-11-25): the
        // offer's own id/name are the DISPLAY code; the real promotion the API accepts is nested.
        const string json = """
        [{
          "id": 4107,
          "name": "Darmowe60minutparkowania.",
          "isActive": 1,
          "isDisplay": 1,
          "autoApply": 1,
          "concessionType": "free_time",
          "concessionValue": "60",
          "offerDiscountType": null,
          "promotionResponseList": [{
            "id": 104539,
            "name": "FRE60MIN07",
            "offerCode": 4107,
            "offerName": "Darmowe60minutparkowania.",
            "isPrimary": 1,
            "isActive": 1,
            "usageType": "per_person",
            "usageLimit": 1
          }]
        }]
        """;

        // Refit's default System.Text.Json content serializer matches property names case-insensitively;
        // mirror that here so the test exercises the same binding the live client uses.
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var offers = System.Text.Json.JsonSerializer.Deserialize<List<Offer>>(json, options)!;
        var offer = offers[0];

        await Assert.That(offer.Id).IsEqualTo(4107);
        await Assert.That(offer.Name).IsEqualTo("Darmowe60minutparkowania.");
        await Assert.That(offer.IsActiveFreeTime).IsTrue();
        await Assert.That(offer.FreeMinutes).IsEqualTo(60);

        // The accessors used for validate / breakdown / start must prefer the nested promotion.
        await Assert.That(offer.PromotionId).IsEqualTo(104539)
            .Because("the id sent to the API must be the nested promotion id, not the offer code");
        await Assert.That(offer.PromotionName).IsEqualTo("FRE60MIN07")
            .Because("the name sent to the API must be the nested promotion name, not the offer display name");
    }

    [Test]
    public async Task Offer_WithoutNestedPromotion_FallsBackToOfferIdAndName()
    {
        var offer = new Offer { Id = 55, Name = "SomeOffer", ConcessionType = "free_time", IsActive = 1 };

        await Assert.That(offer.PromotionId).IsEqualTo(55)
            .Because("with no nested promotion, fall back to the offer id");
        await Assert.That(offer.PromotionName).IsEqualTo("SomeOffer")
            .Because("with no nested promotion, fall back to the offer name");
    }

    [Test]
    public async Task Offer_PrefersPrimaryNestedPromotion()
    {
        var offer = new Offer
        {
            Id = 1, Name = "offer", ConcessionType = "free_time", IsActive = 1,
            PromotionResponseList =
            [
                new PromotionResponse { Id = 10, Name = "SECONDARY", IsPrimary = 0 },
                new PromotionResponse { Id = 20, Name = "PRIMARY", IsPrimary = 1 },
            ],
        };

        await Assert.That(offer.PromotionId).IsEqualTo(20);
        await Assert.That(offer.PromotionName).IsEqualTo("PRIMARY");
    }

    [Test]
    public async Task TariffBreakdown_ConvertsCentsToPln_AndDetectsFree()
    {
        var paid = new TariffBreakdown { TotalPayableAmountInCents = 200, CurrencyCode = "PLN" };
        await Assert.That(paid.TotalPayablePln).IsEqualTo(2.00m);
        await Assert.That(paid.IsFree).IsFalse();

        var free = new TariffBreakdown { TotalPayableAmountInCents = 0, CurrencyCode = "PLN" };
        await Assert.That(free.TotalPayablePln).IsEqualTo(0m);
        await Assert.That(free.IsFree).IsTrue();
    }

    [Test]
    public async Task Offer_FreeWindow_ComesFromConcessionValue_NotName()
    {        // Name says FREE120 but the window must come from concessionValue (parsed as minutes), not the name.
        var offer = new Offer
        {
            Name = "FREE120PL_MO_SAT_1D",
            IsActive = 1,
            ConcessionType = "free_time",
            ConcessionValue = TimeSpan.FromMinutes(120),
        };
        await Assert.That(offer.IsActiveFreeTime).IsTrue()
            .Because("an active free_time offer should be recognised as a free-time coupon");
        await Assert.That(offer.FreeWindow).IsEqualTo(TimeSpan.FromMinutes(120))
            .Because("the free window must come from concessionValue, not the name");
        await Assert.That(offer.FreeMinutes).IsEqualTo(120)
            .Because("the minutes accessor should reflect the 120-minute window");
    }

    [Test]
    public async Task Offer_NotFreeTime_OrInactive_HasNoFreeWindow()
    {
        var inactive = new Offer { IsActive = 0, ConcessionType = "free_time", ConcessionValue = TimeSpan.FromMinutes(120) };
        await Assert.That(inactive.IsActiveFreeTime).IsFalse()
            .Because("an inactive offer is not a usable free-time coupon");
        await Assert.That(inactive.FreeWindow).IsNull()
            .Because("an inactive offer must not expose a free window");

        var discount = new Offer { IsActive = 1, ConcessionType = "percentage", ConcessionValue = TimeSpan.FromMinutes(50) };
        await Assert.That(discount.FreeWindow).IsNull()
            .Because("a percentage discount is not a free-time offer");

        // A FREE-looking name but no concession metadata must NOT yield a free window.
        var nameOnly = new Offer { Name = "FREE120MIN", IsActive = 1, ConcessionType = null, ConcessionValue = null };
        await Assert.That(nameOnly.FreeWindow).IsNull()
            .Because("the free window must never be inferred from the name");
    }

    [Test]
    public async Task CostCap_Decision_UsesBreakdownTotal()
    {
        const decimal cap = 100m;
        var cheap = new TariffBreakdown { TotalPayableAmountInCents = 200 };   // 2 zł
        var pricey = new TariffBreakdown { TotalPayableAmountInCents = 15000 }; // 150 zł

        await Assert.That(cheap.TotalPayablePln <= cap).IsTrue();
        await Assert.That(pricey.TotalPayablePln <= cap).IsFalse();
    }
}
