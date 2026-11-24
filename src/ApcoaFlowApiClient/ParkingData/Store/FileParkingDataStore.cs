using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApcoaFlowApiClient.ParkingData.Store;

/// <summary>
/// Stores the slim parking dataset as a compact binary file on disk (a mapped Docker volume).
///
/// Layout: a header { int schemaVersion, long builtAtUtcTicks, int count } followed by `count`
/// packed records. The data is public (freely downloadable + decryptable from S3), so it is stored
/// plaintext; the decryption key is never written to disk.
///
/// Writes are crash-safe: the snapshot is written to a temp file, flushed to disk, then atomically
/// renamed over the live file. A corrupt or schema-mismatched file loads as null, forcing a rebuild.
/// </summary>
public sealed class FileParkingDataStore : IParkingDataStore
{
    private const int SchemaVersion = 1;
    private const string FileName = "parking.snapshot";
    private const string TempFileName = "parking.snapshot.tmp";

    private readonly string _dir;
    private readonly ILogger<FileParkingDataStore> _logger;

    public FileParkingDataStore(IOptions<ParkingDataSourceOptions> options, ILogger<FileParkingDataStore> logger)
    {
        _dir = options.Value.StorePath;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_dir, FileName);
    private string TempPath => Path.Combine(_dir, TempFileName);

    public Task<ParkingSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
            return Task.FromResult<ParkingSnapshot?>(null);

        try
        {
            using var stream = File.OpenRead(FilePath);
            using var reader = new BinaryReader(stream);

            var schema = reader.ReadInt32();
            if (schema != SchemaVersion)
            {
                _logger.LogInformation(
                    "Parking snapshot schema {Found} != expected {Expected}; ignoring (will rebuild).",
                    schema, SchemaVersion);
                return Task.FromResult<ParkingSnapshot?>(null);
            }

            var builtAtTicks = reader.ReadInt64();
            var count = reader.ReadInt32();

            var entries = new List<ParkingEntry>(count);
            for (var i = 0; i < count; i++)
                entries.Add(ReadEntry(reader));

            var snapshot = new ParkingSnapshot
            {
                BuiltAtUtc = new DateTimeOffset(builtAtTicks, TimeSpan.Zero),
                Entries = entries,
            };
            return Task.FromResult<ParkingSnapshot?>(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read parking snapshot at {Path}; will rebuild.", FilePath);
            return Task.FromResult<ParkingSnapshot?>(null);
        }
    }

    public async Task SaveAsync(ParkingSnapshot snapshot, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);

        await using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new BinaryWriter(stream);
            writer.Write(SchemaVersion);
            writer.Write(snapshot.BuiltAtUtc.UtcTicks);
            writer.Write(snapshot.Entries.Count);
            foreach (var entry in snapshot.Entries)
                WriteEntry(writer, entry);

            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(TempPath, FilePath, overwrite: true);
        _logger.LogInformation(
            "Wrote parking snapshot: {Count} entries, built {BuiltAt:u}.",
            snapshot.Entries.Count, snapshot.BuiltAtUtc);
    }

    // -------------------------------------------------------------------------

    private static void WriteEntry(BinaryWriter w, ParkingEntry e)
    {
        w.Write(e.Id);
        w.Write(e.DisplayName);
        WriteNullable(w, e.Address);
        WriteNullable(w, e.City);
        WriteNullable(w, e.ZipCode);
        WriteNullable(w, e.CountryCode);
        WriteNullable(w, e.CurrencyCode);
        w.Write(e.Latitude);
        w.Write(e.Longitude);
        w.Write(e.IsParkingEnabled);
        w.Write(e.IsEvOnly);
        w.Write(e.IsPassAvailable);
        w.Write(e.IsPermitAvailable);
        w.Write(e.IsPreBookingAvailable);
        WriteStringList(w, e.AccessTypes);
        WriteStringList(w, e.Services);
        WriteStringList(w, e.ParkingLotTypes);

        w.Write(e.OperationalHours.Count);
        foreach (var h in e.OperationalHours)
        {
            w.Write(h.Day);
            w.Write(h.OpenTime);
            w.Write(h.CloseTime);
        }
    }

    private static ParkingEntry ReadEntry(BinaryReader r)
    {
        var entry = new ParkingEntry
        {
            Id = r.ReadInt32(),
            DisplayName = r.ReadString(),
            Address = ReadNullable(r),
            City = ReadNullable(r),
            ZipCode = ReadNullable(r),
            CountryCode = ReadNullable(r),
            CurrencyCode = ReadNullable(r),
            Latitude = r.ReadDouble(),
            Longitude = r.ReadDouble(),
            IsParkingEnabled = r.ReadBoolean(),
            IsEvOnly = r.ReadBoolean(),
            IsPassAvailable = r.ReadBoolean(),
            IsPermitAvailable = r.ReadBoolean(),
            IsPreBookingAvailable = r.ReadBoolean(),
            AccessTypes = ReadStringList(r),
            Services = ReadStringList(r),
            ParkingLotTypes = ReadStringList(r),
        };

        var hourCount = r.ReadInt32();
        for (var i = 0; i < hourCount; i++)
        {
            entry.OperationalHours.Add(new OperationalHoursEntry
            {
                Day = r.ReadInt32(),
                OpenTime = r.ReadString(),
                CloseTime = r.ReadString(),
            });
        }

        return entry;
    }

    private static void WriteNullable(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
            w.Write(value);
    }

    private static string? ReadNullable(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    private static void WriteStringList(BinaryWriter w, List<string> list)
    {
        w.Write(list.Count);
        foreach (var s in list)
            w.Write(s);
    }

    private static List<string> ReadStringList(BinaryReader r)
    {
        var count = r.ReadInt32();
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
            list.Add(r.ReadString());
        return list;
    }
}
