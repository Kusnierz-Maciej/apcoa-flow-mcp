using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowApiClient.ParkingData.Index;
using ApcoaFlowApiClient.ParkingData.Store;

namespace ApcoaFlowApiClient.Tests;

public class ParkingSpatialIndexTests
{
    private const int Precision = 6;

    // Tarnowskie Góry ul. Oświęcimska (parking 616319) — real fixture coordinate.
    private const double TgLat = 50.44333171;
    private const double TgLon = 18.86299094;

    private static ParkingEntry Entry(int id, string name, double lat, double lon, string? city = null) => new()
    {
        Id = id,
        DisplayName = name,
        Latitude = lat,
        Longitude = lon,
        City = city,
        CountryCode = "PL",
    };

    private static ParkingSpatialIndex Build(params ParkingEntry[] entries) =>
        new(new ParkingSnapshot { BuiltAtUtc = DateTimeOffset.UtcNow, Entries = entries }, Precision);

    [Test]
    public async Task FindNearby_ReturnsOnlyWithinRadius_OrderedByDistance()
    {
        var index = Build(
            Entry(1, "Close",   TgLat,            TgLon),               // ~0 km
            Entry(2, "Mid",     TgLat + 0.009,    TgLon),               // ~1 km north
            Entry(3, "Far",     TgLat + 0.09,     TgLon));              // ~10 km north

        var results = index.FindNearby(TgLat, TgLon, radiusKm: 2.0);

        await Assert.That(results.Select(r => r.Parking.Id)).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(results[0].Parking.Id).IsEqualTo(1); // nearest first
        await Assert.That(results[1].Parking.Id).IsEqualTo(2);
    }

    [Test]
    public async Task FindNearby_FindsPointJustAcrossGeohashCellBoundary()
    {
        // Two points ~200 m apart deliberately chosen to risk landing in different gh6 cells.
        var a = Entry(1, "A", TgLat, TgLon);
        var b = Entry(2, "B", TgLat + 0.0018, TgLon + 0.0018); // ~0.2 km diagonal

        var index = Build(a, b);

        var results = index.FindNearby(TgLat, TgLon, radiusKm: 1.0);

        // Neighbor-covering must include B even if it sits in an adjacent cell.
        await Assert.That(results.Select(r => r.Parking.Id)).IsEquivalentTo(new[] { 1, 2 });
    }

    [Test]
    public async Task FindNearby_NameFilter_IsCaseAndDiacriticInsensitive()
    {
        var index = Build(
            Entry(1, "Carrefour Tarnowskie Góry ul. Kościuszki 5", TgLat, TgLon),
            Entry(2, "Lidl Tarnowskie Góry",                        TgLat + 0.001, TgLon),
            Entry(3, "Biedronka Tarnowskie Góry",                   TgLat + 0.002, TgLon));

        var carrefour = index.FindNearby(TgLat, TgLon, 5.0, nameContains: "carrefour");
        await Assert.That(carrefour.Select(r => r.Parking.Id)).IsEquivalentTo(new[] { 1 });

        // "gory" (no diacritics, lowercase) must match "Góry".
        var gory = index.FindNearby(TgLat, TgLon, 5.0, nameContains: "gory");
        await Assert.That(gory.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GetById_ReturnsEntryOrNull()
    {
        var index = Build(Entry(616319, "Tarnowskie Góry ul. Oświęcimska", TgLat, TgLon));

        await Assert.That(index.GetById(616319)!.DisplayName).IsEqualTo("Tarnowskie Góry ul. Oświęcimska");
        await Assert.That(index.GetById(999999)).IsNull();
    }

    [Test]
    public async Task GetAllForCountry_FiltersByCountryCode()
    {
        var pl = Entry(1, "PL one", TgLat, TgLon);
        var de = Entry(2, "DE one", 52.52, 13.405);
        de.CountryCode = "DE";

        var index = Build(pl, de);

        var plOnly = index.GetAllForCountry("PL");
        await Assert.That(plOnly.Select(e => e.Id)).IsEquivalentTo(new[] { 1 });
    }
}
