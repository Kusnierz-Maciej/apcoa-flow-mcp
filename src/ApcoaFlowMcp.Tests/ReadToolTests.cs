using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using ApcoaFlowMcp.Apcoa;
using NSubstitute;

namespace ApcoaFlowMcp.Tests;

public class GeoTests
{
    // SPP Czarny Dunajec's outline (bbox pin geoBorder, 2026-11-27).
    private static readonly GeoBorder CzarnyDunajec = new()
    {
        Coordinates =
        [
            new(49.44119, 19.85535), new(49.44114, 19.85526), new(49.441, 19.85543), new(49.44089, 19.85517),
            new(49.44061, 19.85556), new(49.44079, 19.85591), new(49.44083, 19.85591), new(49.441, 19.85568),
            new(49.44119, 19.85535),
        ],
    };

    [Test]
    public async Task Inside_the_outline_is_zero_and_inside()
    {
        var d = Geo.To(49.4409, 19.8555, new Coordinates(49.4410, 19.8555), CzarnyDunajec)!.Value;
        await Assert.That(d.Inside).IsTrue();
        await Assert.That(d.Meters).IsEqualTo(0);
    }

    [Test]
    public async Task Outside_the_outline_is_the_distance_to_its_edge_not_its_pin()
    {
        // ~100 m north of the outline's northern tip (49.44119).
        var d = Geo.To(49.44209, 19.85535, new Coordinates(49.4409, 19.8555), CzarnyDunajec)!.Value;
        await Assert.That(d.Inside).IsFalse();
        await Assert.That(d.FromOutline).IsTrue();
        await Assert.That(d.Meters).IsBetween(95, 105);
    }

    [Test]
    public async Task Without_an_outline_the_distance_is_to_the_pin()
    {
        var d = Geo.To(50.0, 18.0, new Coordinates(50.001, 18.0), outline: null)!.Value;
        await Assert.That(d.FromOutline).IsFalse();
        await Assert.That(d.Meters).IsBetween(110, 112); // 0.001° latitude ≈ 111 m
    }

    [Test]
    public async Task A_box_covers_the_radius_both_ways()
    {
        var (swLat, swLon, neLat, neLon) = Geo.Box(50.0, 18.0, 1000);
        await Assert.That(Geo.Meters(50.0, 18.0, neLat, 18.0)).IsBetween(999, 1001);
        await Assert.That(Geo.Meters(50.0, 18.0, 50.0, neLon)).IsBetween(999, 1001);
        await Assert.That(swLat).IsLessThan(50.0);
        await Assert.That(swLon).IsLessThan(18.0);
    }
}

public class PlatesTests
{
    [Test]
    [Arguments("ABC12345")]
    [Arguments("abc12345")]
    [Arguments("ABC 123 45")]
    [Arguments(" abc\t12345 ")]
    public async Task Plates_match_without_whitespace_and_case(string typed)
    {
        var vehicles = new[] { new Vehicle { Id = "v", LicensePlate = "ABC12345" } };
        await Assert.That(Plates.Find(vehicles, typed)?.Id).IsEqualTo("v");
    }

    [Test]
    [Arguments("")]
    [Arguments("XYZ999")]
    public async Task Unknown_or_empty_plates_dont_match(string typed) =>
        await Assert.That(Plates.Find([new Vehicle { Id = "v", LicensePlate = "ABC12345" }], typed)).IsNull();
}

public class ReadToolTests
{
    private static MapSpace Pin(string id, string name, double lat, double lon, GeoBorder? outline = null) =>
        new() { Id = id, Name = name, Coordinates = new Coordinates(lat, lon), GeoBorder = outline };

    [Test]
    public async Task FindParkings_with_a_name_and_a_position_uses_the_search_and_measures_nearby_outlines()
    {
        var api = FakeApi.Create();
        var square = new GeoBorder { Coordinates = [new(49.9999, 17.9999), new(49.9999, 18.0001), new(50.0001, 18.0001), new(50.0001, 17.9999), new(49.9999, 17.9999)] };
        api.SearchAsync(Arg.Is<SearchQuery>(q => q!.Query == "Biedronka" && q.Latitude == 50.0 && q.Longitude == 18.0), Arg.Any<CancellationToken>())
            .Returns(new SearchResponse
            {
                Spaces =
                [
                    new SearchSpace { Id = "far", Name = "Biedronka Daleka", Distance = 12.3, TariffCode = "111" },
                    new SearchSpace { Id = "here", Name = "Biedronka Tutaj", Distance = 0.09, TariffCode = "222" },
                ],
            });
        api.GetSpacesByBoundingBoxAsync(Arg.Any<BoundingBoxQuery>(), Arg.Any<CancellationToken>())
            .Returns([Pin("here", "Biedronka Tutaj", 50.0008, 18.0, square)]);

        var lines = (await FakeApi.Tools(api).FindParkingsAsync("Biedronka", 50.0, 18.0)).Split('\n');

        await Assert.That(lines[0]).IsEqualTo("[here] Biedronka Tutaj - you are inside it (code 222)");
        await Assert.That(lines[1]).IsEqualTo("[far] Biedronka Daleka - 12.3 km (code 111)");
    }

    [Test]
    public async Task FindParkings_near_a_position_alone_lists_the_map_pins_within_the_radius()
    {
        var api = FakeApi.Create();
        api.GetSpacesByBoundingBoxAsync(Arg.Any<BoundingBoxQuery>(), Arg.Any<CancellationToken>()).Returns(
        [
            Pin("far", "Parking Daleki", 50.0090, 18.0),
            Pin("near", "Parking Bliski", 50.0010, 18.0),
            Pin("outside", "Parking Poza", 50.0300, 18.0), // ~3.3 km: outside 2 km
        ]);

        var lines = (await FakeApi.Tools(api).FindParkingsAsync(latitude: 50.0, longitude: 18.0)).Split('\n');

        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo("[near] Parking Bliski - 111 m");
        await Assert.That(lines[1]).IsEqualTo("[far] Parking Daleki - 1.0 km");
    }

    [Test]
    public async Task FindParkings_says_when_you_are_inside_a_parking()
    {
        var api = FakeApi.Create();
        var outline = new GeoBorder { Coordinates = [new(49.9999, 17.9999), new(49.9999, 18.0001), new(50.0001, 18.0001), new(50.0001, 17.9999), new(49.9999, 17.9999)] };
        api.GetSpacesByBoundingBoxAsync(Arg.Any<BoundingBoxQuery>(), Arg.Any<CancellationToken>()).Returns(
        [
            Pin("pin-closer", "Pin only", 50.0002, 18.0),
            Pin("here", "Around you", 50.0009, 18.0, outline),
        ]);

        var text = await FakeApi.Tools(api).FindParkingsAsync(latitude: 50.0, longitude: 18.0);

        await Assert.That(text.Split('\n')[0]).IsEqualTo("[here] Around you - you are inside it");
    }

    [Test]
    public async Task FindParkings_by_text_lists_address_and_location_code()
    {
        var api = FakeApi.Create();
        api.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns(new SearchResponse
        {
            Spaces = [new SearchSpace { Id = "c9a5", Name = "Targowiska Miejskie Ostrów Wlkp. III", TariffCode = "22941",
                Address = new SpaceAddress { DisplayValue = "ul. Grabowska 29a, Ostrów Wielkopolski, 63-400 Poland" } }],
        });

        var text = await FakeApi.Tools(api).FindParkingsAsync("22941");

        await Assert.That(text).IsEqualTo("[c9a5] Targowiska Miejskie Ostrów Wlkp. III - ul. Grabowska 29a, Ostrów Wielkopolski, 63-400 Poland (code 22941)");
    }

    [Test]
    public async Task FindParkings_needs_a_query_or_a_position() =>
        await Assert.That(await FakeApi.Tools(FakeApi.Create()).FindParkingsAsync()).StartsWith("Give a name or text to search for");

    [Test]
    public async Task GetPrice_reports_the_quote_and_the_free_window_from_the_parkings_hint()
    {
        var api = FakeApi.Create();
        api.GetSpaceAsync("bied", Arg.Any<CancellationToken>()).Returns(new SpaceDetails
        {
            Id = "bied", Name = "Biedronka Opole ul. Ozimska 48", DefaultLengthOfStayInMinutes = 90, MaxDuration = 43200,
        });
        FakeApi.Quotes(api, minutes => minutes <= 90 ? 0m : 2m);

        var text = await FakeApi.Tools(api).GetPriceAsync("bied", "abc 12345", 120);

        await Assert.That(text).Contains("Biedronka Opole ul. Ozimska 48, ABC12345, 2 h from now: 2.00 PLN (charged at start).");
        await Assert.That(text).Contains("Free for up to 1 h 30 min for this vehicle right now");
    }

    [Test]
    public async Task Free_window_without_a_hint_is_found_by_search()
    {
        var api = FakeApi.Create();
        FakeApi.Quotes(api, minutes => minutes <= 237 ? 0m : 3m);

        var window = await new Pricing(api, new FakeApi.FixedTime(FakeApi.Now)).FreeWindowAsync("p", FakeApi.VehicleId, null, null, default);

        await Assert.That(window.Minutes).IsEqualTo(237);
        await Assert.That(window.AtLeast).IsFalse();
    }

    [Test]
    public async Task Free_window_is_at_least_a_day_when_a_day_is_free()
    {
        var api = FakeApi.Create();
        FakeApi.Quotes(api, _ => 0m);

        var window = await new Pricing(api, new FakeApi.FixedTime(FakeApi.Now)).FreeWindowAsync("p", FakeApi.VehicleId, null, null, default);

        await Assert.That(window).IsEqualTo(new Pricing.FreeWindow(24 * 60, AtLeast: true));
    }

    [Test]
    public async Task GetPrice_with_an_unknown_plate_says_so()
    {
        var text = await FakeApi.Tools(FakeApi.Create()).GetPriceAsync("p", "XYZ999", 30);
        await Assert.That(text).Contains("isn't one of the account's vehicles");
    }

    [Test]
    public async Task ActiveSessions_show_id_parking_plate_times_in_Warsaw_and_time_left()
    {
        var api = FakeApi.Create();
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Session
            {
                Id = "35da0b7f", StartDate = FakeApi.Now.AddMinutes(-10), EndDate = FakeApi.Now.AddMinutes(20),
                Space = new SessionSpace { Id = "s", Name = "Targowiska Miejskie Ostrów Wlkp. III" },
                Vehicle = new SessionVehicle { Id = "v", LicensePlate = "ABC12345" },
                PaymentFlow = "prePay", SessionCanBeExtended = true, SessionCanBeStopped = true,
            },
        ]);

        var text = await FakeApi.Tools(api).GetActiveSessionsAsync();

        await Assert.That(text).IsEqualTo("[35da0b7f] Targowiska Miejskie Ostrów Wlkp. III - ABC12345, from 14:50 until 15:20 (20 min left), paid at start");
    }

    [Test]
    public async Task History_shows_cost_and_end_time()
    {
        var api = FakeApi.Create();
        api.GetSessionsHistoryAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new SessionsHistoryItem { Id = "h1", SpaceName = "Biedronka Żyrardów", LicensePlate = "ABC12345",
                EndDate = new DateTimeOffset(2026, 11, 26, 14, 4, 0, TimeSpan.Zero), TotalPrice = "PLN 0.00", State = "billed" },
        ]);

        var text = await FakeApi.Tools(api).GetSessionHistoryAsync();

        await Assert.That(text).IsEqualTo("Biedronka Żyrardów - ABC12345, ended 2026-11-26 15:04, 0.00 PLN");
    }

    [Test]
    public async Task GetParkingDetails_gives_location_sessions_pricing_and_amenities()
    {
        var api = FakeApi.Create();
        api.GetSpaceAsync("lidl", Arg.Any<CancellationToken>()).Returns(new SpaceDetails
        {
            Id = "lidl", Name = "Lidl Poznań ul. Fabryczna 23", TariffCode = "12345",
            Coordinates = new Coordinates(52.4, 16.95),
            Address = new SpaceAddress { DisplayValue = "ul. Fabryczna 23, Poznań, 61-000 Poland" },
            ParkingOverview = [new SpaceOverviewItem { Title = "Open" }, new SpaceOverviewItem { Title = "AutoPay" }],
            CtaButtonType = "startParking", SessionsStartAutomatically = true, DefaultLengthOfStayInMinutes = 90, MaxDuration = 1440,
            HasAvailableContracts = true, CanStartCharging = true, AllowedPSPs = ["stripe"],
            Prices =
            [
                new SpacePriceSection { SectionName = "Parking", Prices = [new PriceItem { Label = "Up to 1 hour", Price = "PLN 0.00" }],
                    Description = "1,5 godziny parkowania za darmo" },
                new SpacePriceSection { SectionName = "Parking fees", Prices = [new PriceItem { Label = "Prices include convenience fees", Price = "" }] },
            ],
        });
        api.GetSpacesByBoundingBoxAsync(Arg.Any<BoundingBoxQuery>(), Arg.Any<CancellationToken>()).Returns(
        [
            Pin("lidl", "Lidl Poznań ul. Fabryczna 23", 52.4, 16.95,
                new GeoBorder { Coordinates = [new(52.4, 16.95), new(52.401, 16.95), new(52.401, 16.951), new(52.4, 16.95)] }),
        ]);

        var text = await FakeApi.Tools(api).GetParkingDetailsAsync("lidl");

        await Assert.That(text).IsEqualTo(
            "[lidl] Lidl Poznań ul. Fabryczna 23 (location code 12345)\n" +
            "Location:\n" +
            "  Address: ul. Fabryczna 23, Poznań, 61-000 Poland\n" +
            "  Coordinates: 52.4, 16.95\n" +
            "  Outline on the map: yes (4 points; distances are measured to its edge)\n" +
            "Sessions:\n" +
            "  Status: Open, AutoPay\n" +
            "  You start and end a session (start_session / start_free_session, or in the APCOA Flow app).\n" +
            "  Plate recognition: sessions start and end automatically when the car enters and leaves.\n" +
            "  Usual stay: 1 h 30 min\n" +
            "  Longest stay: 1 day\n" +
            "Pricing:\n" +
            "  Parking: Up to 1 hour: 0.00 PLN\n" +
            "    1,5 godziny parkowania za darmo\n" +
            "  Parking fees: Prices include convenience fees\n" +
            "Amenities: EV charging, season tickets\n" +
            "Payment: card (Stripe)");
    }

    [Test]
    [Arguments(5, "5 min")]
    [Arguments(60, "1 h")]
    [Arguments(90, "1 h 30 min")]
    [Arguments(120, "2 h")]
    [Arguments(1440, "1 day")]
    [Arguments(43200, "30 days")]
    public async Task Durations_read_naturally(int minutes, string expected) =>
        await Assert.That(Text.Duration(minutes)).IsEqualTo(expected);
}
