using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowApiClient.ParkingData.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApcoaFlowApiClient.Tests;

public class FileParkingDataStoreTests
{
    private static (FileParkingDataStore Store, string Dir) NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "apcoa-store-test-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new ParkingDataSourceOptions { StorePath = dir });
        return (new FileParkingDataStore(options, NullLogger<FileParkingDataStore>.Instance), dir);
    }

    private static ParkingEntry SampleEntry() => new()
    {
        Id = 616319,
        DisplayName = "Tarnowskie Góry ul. Oświęcimska",
        Address = "ul. Oświęcimska 21",
        City = "Tarnowskie Góry",
        ZipCode = "42-600",
        CountryCode = "PL",
        CurrencyCode = "PLN",
        Latitude = 50.44333171,
        Longitude = 18.86299094,
        IsParkingEnabled = true,
        AccessTypes = ["PND"],
        ParkingLotTypes = ["CAR"],
        OperationalHours = [new OperationalHoursEntry { Day = 1, OpenTime = "00:00:00.000", CloseTime = "00:00:00.000" }],
    };

    [Test]
    public async Task SaveThenLoad_RoundTripsEntriesAndTimestamp()
    {
        var (store, dir) = NewStore();
        try
        {
            var builtAt = DateTimeOffset.UtcNow;
            var snapshot = new ParkingSnapshot { BuiltAtUtc = builtAt, Entries = [SampleEntry()] };

            await store.SaveAsync(snapshot);
            var loaded = await store.LoadAsync();

            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.BuiltAtUtc).IsEqualTo(builtAt);
            await Assert.That(loaded.Entries.Count).IsEqualTo(1);

            var e = loaded.Entries[0];
            await Assert.That(e.Id).IsEqualTo(616319);
            await Assert.That(e.DisplayName).IsEqualTo("Tarnowskie Góry ul. Oświęcimska");
            await Assert.That(e.Latitude).IsEqualTo(50.44333171);
            await Assert.That(e.AccessTypes).IsEquivalentTo(new[] { "PND" });
            await Assert.That(e.OperationalHours[0].Day).IsEqualTo(1);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Load_WhenNoFile_ReturnsNull()
    {
        var (store, _) = NewStore();
        await Assert.That(await store.LoadAsync()).IsNull();
    }

    [Test]
    public async Task Load_WhenFileCorrupt_ReturnsNull()
    {
        var (store, dir) = NewStore();
        try
        {
            await store.SaveAsync(new ParkingSnapshot { BuiltAtUtc = DateTimeOffset.UtcNow, Entries = [SampleEntry()] });

            // Corrupt the snapshot by truncating it to garbage.
            var file = Path.Combine(dir, "parking.snapshot");
            await File.WriteAllBytesAsync(file, [0xDE, 0xAD, 0xBE, 0xEF]);

            await Assert.That(await store.LoadAsync()).IsNull();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
