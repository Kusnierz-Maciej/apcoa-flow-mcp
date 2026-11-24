using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Tariff;

namespace ApcoaFlowApiClient.Tests;

public class TariffAndPromotionLogicTests
{
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
    {
        // Name says FREE120 but the window must come from concessionValue (parsed as minutes), not the name.
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
