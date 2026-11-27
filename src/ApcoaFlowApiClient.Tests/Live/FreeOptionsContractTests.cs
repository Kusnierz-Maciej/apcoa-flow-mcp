using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;

namespace ApcoaFlowApiClient.Tests.Live;

/// <summary>
/// The free options the server relies on, read-only (nothing starts): finding parkings that come with free time
/// (searching "Biedronka"), the free time showing in the dated quote exactly up to the parking's usual stay, the
/// reusable free test parking, location-code search, outlines on the map. No paid path is tested and none relies on a
/// grace period.
/// </summary>
[Category("LiveApi")]
[NotInParallel(LiveApi.Serial)]
public class FreeOptionsContractTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    [Before(HookType.Class)]
    public static Task SweepAtStart() => LiveApi.SweepOnceAtStartAsync();

    [After(HookType.Class)]
    public static Task Sweep() => LiveApi.EndActiveSessionsAsync("after FreeOptionsContractTests");

    [Test]
    public async Task Searching_the_free_time_query_returns_matching_parkings_with_location_codes()
    {
        var hits = (await LiveApi.Api.SearchAsync(new SearchQuery { Query = LiveApi.FreeTimeQuery, Size = 20, IsParking = true, IsCharging = false })).Spaces;

        await Assert.That(hits.Count).IsGreaterThan(0);
        await Assert.That(hits.All(h => h.Name.Contains(LiveApi.FreeTimeQuery, StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(hits.Count(h => !string.IsNullOrWhiteSpace(h.TariffCode))).IsGreaterThan(hits.Count / 2)
            .Because("search hits carry the parking's location code");
    }

    [Test]
    public async Task Free_time_is_in_the_quote_exactly_up_to_the_parkings_usual_stay()
    {
        var api = LiveApi.Api;
        var vehicle = LiveApi.FirstRegisteredCar(await api.GetVehiclesAsync());
        var spaceId = await LiveApi.FindFreeParkingAsync(api, vehicle, minutes: 1, includeOstrow: false);
        if (spaceId is null)
            Skip.Test($"No \"{LiveApi.FreeTimeQuery}\" parking has free time for this vehicle right now (once per 4 h each).");

        var details = await api.GetSpaceAsync(spaceId!);
        await Assert.That(details.DefaultLengthOfStayInMinutes).IsNotNull().Because("free-time parkings preselect their free window");
        var window = details.DefaultLengthOfStayInMinutes!.Value;

        await Assert.That((await LiveApi.QuoteAsync(api, spaceId!, vehicle.Id, window)).IsZero).IsTrue()
            .Because($"{details.Name} is free for its usual stay ({window} min)");
        await Assert.That((await LiveApi.QuoteAsync(api, spaceId!, vehicle.Id, window + 1)).IsZero).IsFalse()
            .Because("one minute past the free window costs money");
    }

    [Test]
    public async Task Details_of_a_free_time_parking_carry_its_tariff_and_say_it_can_be_started()
    {
        var api = LiveApi.Api;
        // With a position: a position-less search returns parkings that aren't on the map (no coordinates).
        var hit = (await api.SearchAsync(new SearchQuery { Query = LiveApi.FreeTimeQuery, Latitude = 52.0, Longitude = 19.0, Size = 5, IsParking = true, IsCharging = false })).Spaces.First();

        var details = await api.GetSpaceAsync(hit.Id);

        await Assert.That(details.Name).IsEqualTo(hit.Name);
        await Assert.That(details.CtaButtonType).IsEqualTo("startParking");
        await Assert.That(details.Prices.Any(section => section.Prices.Any(p => Money.TryParse(p.Price, out _)))).IsTrue()
            .Because("the tariff table lists prices as \"PLN 0.00\"");
        await Assert.That(details.Coordinates).IsNotNull();
    }

    [Test]
    public async Task A_search_with_a_position_returns_mapped_parkings_without_one_unmapped()
    {
        var api = LiveApi.Api;
        var withPosition = (await api.SearchAsync(new SearchQuery { Query = LiveApi.FreeTimeQuery, Latitude = 52.0, Longitude = 19.0, Size = 20, IsParking = true, IsCharging = false })).Spaces;
        var without = (await api.SearchAsync(new SearchQuery { Query = LiveApi.FreeTimeQuery, Size = 20, IsParking = true, IsCharging = false })).Spaces;

        await Assert.That(withPosition.All(h => h.Coordinates is not null && !string.IsNullOrWhiteSpace(h.Address?.DisplayValue))).IsTrue()
            .Because("find_parkings relies on search with a position returning parkings on the map");
        await Assert.That(without.Any(h => h.Coordinates is null)).IsTrue()
            .Because("without a position the search returned unmapped parkings (2026-11-27); if this changes, find_parkings can drop its anchor");
    }

    [Test]
    public async Task The_test_parking_is_free_outside_Saturday_06_to_15()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Warsaw);
        var end = now.AddHours(2);
        bool InPaidWindow(DateTimeOffset t) => t.DayOfWeek == DayOfWeek.Saturday && t.Hour is >= 6 and < 15;
        if (InPaidWindow(now) || InPaidWindow(end) || (now.DayOfWeek == DayOfWeek.Friday && end.DayOfWeek == DayOfWeek.Saturday && end.Hour >= 6))
            Skip.Test("Ostrów is paid on Saturday 06–15.");

        var vehicle = LiveApi.FirstRegisteredCar(await LiveApi.Api.GetVehiclesAsync());
        await Assert.That((await LiveApi.QuoteAsync(LiveApi.Api, LiveApi.Ostrow, vehicle.Id, 120)).IsZero).IsTrue();
    }

    [Test]
    public async Task A_location_code_search_returns_exactly_that_parking()
    {
        var hits = (await LiveApi.Api.SearchAsync(new SearchQuery { Query = "22941", Latitude = 52.0, Longitude = 19.0, IsParking = true, IsCharging = false })).Spaces;

        await Assert.That(hits.Select(h => h.Id)).IsEquivalentTo(new[] { LiveApi.Ostrow });
    }

    [Test]
    public async Task Search_distance_is_in_kilometres()
    {
        // From 1 km south of the test parking (0.009° latitude ≈ 1.0 km).
        var hit = (await LiveApi.Api.SearchAsync(new SearchQuery
        {
            Query = "22941", Latitude = 51.651796 - 0.009, Longitude = 17.836953, IsParking = true, IsCharging = false,
        })).Spaces.Single();

        await Assert.That(hit.Distance).IsNotNull();
        await Assert.That(hit.Distance!.Value).IsBetween(0.8, 1.2).Because("find_parkings multiplies the API's distance by 1000");
    }

    [Test]
    public async Task Map_pins_carry_closed_outlines()
    {
        // Czarny Dunajec: 7 of 8 pins have an outline (2026-11-27).
        var pins = await LiveApi.Api.GetSpacesByBoundingBoxAsync(new BoundingBoxQuery
        {
            NeLatitude = 49.446, NeLongitude = 19.8605, SwLatitude = 49.436, SwLongitude = 19.8505, Zoom = 16,
        });
        var outlines = pins.Where(p => p.GeoBorder is { Coordinates.Count: >= 4 }).Select(p => p.GeoBorder!.Coordinates).ToList();

        await Assert.That(outlines.Count).IsGreaterThan(0);
        await Assert.That(outlines.All(ring => ring[0] == ring[^1])).IsTrue().Because("an outline is a closed ring");
    }

    [Test]
    public async Task Price_options_give_the_price_curve_hour_by_hour()
    {
        // Whole hours, repeated: one "Up to N hour(s)" row each — the price of a stay of up to N hours starting now
        // (2026-11-28). Pricing reads the free window and the longest stay at the same price off this curve.
        var api = LiveApi.Api;
        var details = await api.GetSpaceAsync(LiveApi.Ostrow, [1, 2, 3, 5, 8]);
        var labels = details.Prices.Where(s => s.SectionName == "Parking").SelectMany(s => s.Prices).Select(i => i.Label).ToList();

        await Assert.That(labels).Contains("Up to 1 hour");
        foreach (var hours in new[] { 2, 3, 5, 8 })
            await Assert.That(labels).Contains($"Up to {hours} hours");
        await Assert.That(details.Prices.SelectMany(s => s.Prices).Where(i => i.Label!.StartsWith("Up to"))
            .All(i => Money.TryParse(i.Price, out _))).IsTrue().Because("every row is a parseable PLN price");

        // A dated row agrees with the quote for the same stay.
        var car = LiveApi.FirstRegisteredCar(await api.GetVehiclesAsync());
        var threeHours = details.Prices.SelectMany(s => s.Prices).Single(i => i.Label == "Up to 3 hours");
        var quote = await LiveApi.QuoteAsync(api, LiveApi.Ostrow, car.Id, 180);
        await Assert.That(Money.Parse(threeHours.Price!).Amount).IsEqualTo(quote.Amount);
    }
}
