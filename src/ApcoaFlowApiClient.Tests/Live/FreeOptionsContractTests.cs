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
    public async Task Price_options_rows_come_in_the_asked_order_and_equal_the_quotes()
    {
        // One row per distinct hour asked, in the asked order (2026-11-28); each = the dated quote for that stay.
        // Pricing maps rows by position and never reads their labels.
        var api = LiveApi.Api;
        var car = LiveApi.FirstRegisteredCar(await api.GetVehiclesAsync());
        int[] asked = [3, 1, 2];
        var rows = PriceRows(await api.GetSpaceAsync(LiveApi.Ostrow, asked));

        await Assert.That(rows.Count).IsEqualTo(asked.Length);
        for (var i = 0; i < asked.Length; i++)
            await Assert.That(rows[i].Amount).IsEqualTo((await LiveApi.QuoteAsync(api, LiveApi.Ostrow, car.Id, asked[i] * 60)).Amount)
                .Because($"row {i} is the stay of {asked[i]} h");

        // Duplicates are merged: ask distinct hours.
        await Assert.That(PriceRows(await api.GetSpaceAsync(LiveApi.Ostrow, [5, 1, 5])).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Price_row_labels_still_read_as_hours_and_a_day()
    {
        // Loose canary, not a contract the code computes from: the tools show these labels to the user as the server
        // writes them ("Up to 1 hour", "Up to 2 hours", "Max day price", 2026-11-28). If it fails, the wording changed:
        // check that find_parkings / get_parking_details still read well.
        var labels = (await LiveApi.Api.GetSpaceAsync(LiveApi.Ostrow, [1, 2, 24])).Prices
            .SelectMany(s => s.Prices).Where(i => Money.TryParse(i.Price, out _)).Select(i => i.Label ?? "").ToList();

        await Assert.That(labels.Count).IsEqualTo(3);
        await Assert.That(labels[0]).Contains("hour", StringComparison.OrdinalIgnoreCase);
        await Assert.That(labels[1]).Contains("hour", StringComparison.OrdinalIgnoreCase);
        await Assert.That(labels[2]).Contains("day", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task Every_country_on_the_map_has_a_time_zone()
    {
        // The map answers every pin in a box (zoom doesn't matter): all of Europe in one call (~5,500 pins, 2026-11-28).
        // Pins carry no country, so read one parking's details per 2° cell (~80 calls) and check each country it finds
        // is in CountryTimeZones — times are shown in the parking's zone.
        var api = LiveApi.Api;
        var pins = await api.GetSpacesByBoundingBoxAsync(new BoundingBoxQuery
        {
            SwLatitude = 34, SwLongitude = -25, NeLatitude = 72, NeLongitude = 45, Zoom = 3,
        });
        var samples = pins.Where(p => p.Coordinates is not null)
            .GroupBy(p => ((int)Math.Floor(p.Coordinates!.Latitude / 2), (int)Math.Floor(p.Coordinates.Longitude / 2)))
            .Select(g => g.First().Id).ToList();
        await Assert.That(samples.Count).IsGreaterThan(10).Because("the map should cover many areas");

        var countries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in samples)
        {
            try { if ((await api.GetSpaceAsync(id)).CountryCode is { } country) countries.Add(country); }
            catch (Refit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { } // pins without a tariff
        }

        await Assert.That(countries).Contains("PL");
        var missing = countries.Where(c => !CountryTimeZones.TryGet(c, out _)).ToList();
        await Assert.That(missing).IsEmpty().Because($"add these countries to CountryTimeZones (seen: {string.Join(", ", countries.Order())})");
    }

    // The price section's rows as money, found by shape (every row a price), not by name.
    private static List<Money> PriceRows(SpaceDetails details) =>
        details.Prices.Select(s => s.Prices.Select(i => Money.TryParse(i.Price, out var m) ? m : (Money?)null).ToList())
            .First(rows => rows.Count > 0 && rows.All(m => m is not null)).Select(m => m!.Value).ToList();
}
