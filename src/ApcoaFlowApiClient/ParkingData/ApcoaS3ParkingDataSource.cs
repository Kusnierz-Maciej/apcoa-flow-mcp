using System.Security.Cryptography;
using System.Text.Json;
using ApcoaFlowApiClient.ParkingData.Index;
using ApcoaFlowApiClient.ParkingData.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApcoaFlowApiClient.ParkingData;

/// <summary>
/// Orchestrates the APCOA offline parking dataset: downloads + decrypts (AES-128-GCM) all country
/// tiles from the public S3 bucket, builds a single in-memory <see cref="ParkingSpatialIndex"/>,
/// persists a slim snapshot to disk for warm restarts, and serves searches with a three-tier
/// freshness model (soft stale-while-revalidate, hard blocking rebuild, hosted-service cadence).
///
/// Decryption key comes from <c>GET /authentication/v1/dashboard/tenant/appConfigByCompanyId/{id}?version=v3</c>
/// → <c>modules[].subModules[].config.parkingDataKey</c> (base64, 16 bytes = AES-128). Key never touches disk.
/// </summary>
public sealed class ApcoaS3ParkingDataSource : IApcoaParkingDataSource, IDisposable
{
    private const string S3BaseUrl = "https://apcoa-parking-data.s3.eu-central-1.amazonaws.com";

    private static readonly Dictionary<string, string[]> CountryTiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AT"] = ["7s", "7z", "td", "u0", "u2", "um"],
        ["BE"] = ["u1"],
        ["CH"] = ["u0"],
        ["DE"] = ["7z", "td", "u0", "u1", "u2", "u3"],
        ["DK"] = ["7z", "u1", "u3", "u4", "v1"],
        ["IE"] = ["gc"],
        ["IT"] = ["7z", "sn", "sp", "sq", "sr", "u0", "u2"],
        ["NL"] = ["u1"],
        ["NO"] = ["7z", "u4", "u5", "u6", "u7", "uk", "us"],
        ["PL"] = ["7z", "t5", "td", "u1", "u2", "u3", "u8", "u9", "v1"],
        ["SE"] = ["31", "7z", "s0", "t4", "t7", "u3", "u4", "u6", "u7", "v6"],
        ["UK"] = ["gb", "gc", "gf", "u1"],
    };

    private readonly IApcoaFlowApi _api;
    private readonly HttpClient _http;
    private readonly IParkingDataStore _store;
    private readonly ParkingDataSourceOptions _options;
    private readonly ILogger<ApcoaS3ParkingDataSource> _logger;

    private volatile ParkingSpatialIndex? _index;

    private string? _decryptionKey;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    // Single-flight guard for rebuilds (shared by read-path refresh and hosted service).
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private volatile Task? _backgroundRefresh;

    public ApcoaS3ParkingDataSource(
        IApcoaFlowApi api,
        IHttpClientFactory httpFactory,
        IParkingDataStore store,
        IOptions<ParkingDataSourceOptions> options,
        ILogger<ApcoaS3ParkingDataSource> logger)
    {
        _api = api;
        _http = httpFactory.CreateClient(nameof(ApcoaS3ParkingDataSource));
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(ParkingEntry Parking, double DistanceKm)>> FindNearbyAsync(
        double latitude, double longitude, double radiusKm, string? nameContains = null, CancellationToken ct = default)
    {
        var index = await EnsureIndexAsync(ct);
        return index.FindNearby(latitude, longitude, radiusKm, nameContains);
    }

    /// <inheritdoc />
    public async Task<ParkingEntry?> GetByIdAsync(int parkingId, CancellationToken ct = default)
    {
        var index = await EnsureIndexAsync(ct);
        return index.GetById(parkingId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParkingEntry>> GetAllForCountryAsync(string countryCode, CancellationToken ct = default)
    {
        var index = await EnsureIndexAsync(ct);
        return index.GetAllForCountry(countryCode);
    }

    /// <summary>Forces a full rebuild of the index from S3. Called by the hosted background refresh service.</summary>
    public Task RefreshAsync(CancellationToken ct = default) => BuildIndexAsync(ct);

    // -------------------------------------------------------------------------
    // Freshness orchestration

    /// <summary>
    /// Returns a usable index, applying the three-tier freshness policy:
    /// <list type="bullet">
    /// <item>Cold (no index): load a non-expired snapshot from disk, otherwise block on a full build.</item>
    /// <item>Older than <see cref="ParkingDataSourceOptions.HardRefreshAge"/>: block on a rebuild before serving.</item>
    /// <item>Older than <see cref="ParkingDataSourceOptions.SoftRefreshAge"/>: serve the current index now and
    /// rebuild in the background (stale-while-revalidate).</item>
    /// </list>
    /// </summary>
    private async Task<ParkingSpatialIndex> EnsureIndexAsync(CancellationToken ct)
    {
        var index = _index;

        // Cold: no in-memory index yet. Try disk, else build.
        if (index is null)
        {
            await _buildLock.WaitAsync(ct);
            try
            {
                if (_index is not null)
                    return _index;

                var snapshot = await _store.LoadAsync(ct);
                if (snapshot is not null && Age(snapshot.BuiltAtUtc) < _options.HardRefreshAge)
                {
                    _logger.LogInformation(
                        "Loaded parking index from disk: {Count} entries, built {BuiltAt:u}.",
                        snapshot.Entries.Count, snapshot.BuiltAtUtc);
                    _index = new ParkingSpatialIndex(snapshot, _options.GeohashPrecision);
                    return _index;
                }

                await BuildIndexCoreAsync(ct);
                return _index!;
            }
            finally
            {
                _buildLock.Release();
            }
        }

        var age = Age(index.BuiltAtUtc);

        // Hard-expired: block on a rebuild before serving.
        if (age >= _options.HardRefreshAge)
        {
            await BuildIndexAsync(ct);
            return _index!;
        }

        // Soft-expired: serve current data now, refresh in the background.
        if (age >= _options.SoftRefreshAge)
            TriggerBackgroundRefresh();

        return index;
    }

    /// <summary>
    /// Starts a background rebuild unless one is already running (single-flight). Never throws —
    /// a failed refresh is logged and the current index keeps serving.
    /// </summary>
    private void TriggerBackgroundRefresh()
    {
        if (_backgroundRefresh is { IsCompleted: false })
            return;

        _backgroundRefresh = Task.Run(async () =>
        {
            try
            {
                await BuildIndexAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background parking index refresh failed; keeping current data.");
            }
        });
    }

    /// <summary>Runs a build under the single-flight lock so parallel callers don't rebuild concurrently.</summary>
    private async Task BuildIndexAsync(CancellationToken ct)
    {
        await _buildLock.WaitAsync(ct);
        try
        {
            await BuildIndexCoreAsync(ct);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Download + decrypt + build

    /// <summary>
    /// Downloads every country tile, decrypts and parses them into the slim model, persists the
    /// result as a snapshot, and swaps in a freshly built index. Not locked itself — always invoked
    /// via <see cref="BuildIndexAsync"/> or under the cold-start lock in <see cref="EnsureIndexAsync"/>.
    /// </summary>
    private async Task BuildIndexCoreAsync(CancellationToken ct)
    {
        _logger.LogInformation("Building parking index from S3 (all countries)…");

        var key = await GetDecryptionKeyAsync(ct);

        // Each tile file is country-specific in name, so iterate the (country, tile) pairs.
        // Dedupe by id: a parking appears in exactly one tile, but guard against overlap anyway.
        var byId = new Dictionary<int, ParkingEntry>();
        foreach (var (cc, countryTiles) in CountryTiles)
        {
            foreach (var tile in countryTiles)
            {
                ct.ThrowIfCancellationRequested();
                var entries = await DownloadTileAsync(tile, cc, key, ct);
                foreach (var e in entries)
                    byId[e.Id] = e;
            }
        }

        var snapshot = new ParkingSnapshot
        {
            BuiltAtUtc = DateTimeOffset.UtcNow,
            Entries = byId.Values.ToList(),
        };

        // Serve from memory first; persisting is a warm-start optimization, not a hard requirement.
        // A read-only or non-writable StorePath (e.g. a misconfigured volume) must not break parking.
        _index = new ParkingSpatialIndex(snapshot, _options.GeohashPrecision);
        _logger.LogInformation("Parking index built: {Count} entries.", snapshot.Entries.Count);

        try
        {
            await _store.SaveAsync(snapshot, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not persist the parking snapshot (serving from memory only). " +
                "Check that StorePath is writable to warm-start after a restart.");
        }
    }

    /// <summary>
    /// Downloads and decrypts a single S3 tile into slim entries. A missing/empty tile (≤28 bytes is
    /// just a GCM nonce+tag with no payload) or any download/decrypt failure yields an empty list and
    /// a warning, so one bad tile never aborts the whole build.
    /// </summary>
    private async Task<IReadOnlyList<ParkingEntry>> DownloadTileAsync(
        string geohash2, string countryCode, byte[] key, CancellationToken ct)
    {
        var url = $"{S3BaseUrl}/apcoa_{geohash2}_{countryCode}_parking";
        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to download tile {Tile}/{Country}; skipping.", geohash2, countryCode);
            return [];
        }

        if (bytes.Length <= 28)
            return [];

        try
        {
            var decrypted = DecryptGcm(bytes, key);
            return ParseEntries(decrypted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt/parse tile {Tile}/{Country}; skipping.", geohash2, countryCode);
            return [];
        }
    }

    /// <summary>
    /// Parses a decrypted tile (a JSON object keyed by parking id) into slim <see cref="ParkingEntry"/>
    /// records, keeping only the fields the MCP tools need. Entries without a geo location are skipped.
    /// </summary>
    private static IReadOnlyList<ParkingEntry> ParseEntries(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var entries = new List<ParkingEntry>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var el = prop.Value;
            if (!el.TryGetProperty("geoLocation", out var geo) || geo.ValueKind != JsonValueKind.Object)
                continue;

            entries.Add(new ParkingEntry
            {
                Id = el.GetProperty("id").GetInt32(),
                DisplayName = el.GetProperty("displayName").GetString() ?? string.Empty,
                Address = el.TryGetProperty("address", out var a) ? a.GetString() : null,
                City = el.TryGetProperty("city", out var c) ? c.GetString() : null,
                ZipCode = el.TryGetProperty("zipCode", out var z) ? z.GetString() : null,
                CountryCode = el.TryGetProperty("countryCode", out var cc) ? cc.GetString() : null,
                CurrencyCode = el.TryGetProperty("currencyCode", out var cur) ? cur.GetString() : null,
                Latitude = geo.GetProperty("latitude").GetDouble(),
                Longitude = geo.GetProperty("longitude").GetDouble(),
                IsParkingEnabled = el.TryGetProperty("isParkingEnabled", out var en) && en.GetInt32() == 1,
                IsEvOnly = el.TryGetProperty("isEvOnly", out var ev) && ev.GetInt32() == 1,
                IsPassAvailable = el.TryGetProperty("isPassAvailable", out var pa) && pa.GetInt32() == 1,
                IsPermitAvailable = el.TryGetProperty("isPermitAvailable", out var pe) && pe.GetInt32() == 1,
                IsPreBookingAvailable = el.TryGetProperty("isPreBookingAvailable", out var pb) && pb.GetInt32() == 1,
                AccessTypes = ReadStringArray(el, "accessTypeList"),
                Services = ReadStringArray(el, "services"),
                ParkingLotTypes = ReadStringArray(el, "parkingLotTypeList"),
                OperationalHours = ReadOperationalHours(el),
            });
        }

        return entries;
    }

    private static List<string> ReadStringArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        return arr.EnumerateArray()
            .Select(x => x.GetString())
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
    }

    private static List<OperationalHoursEntry> ReadOperationalHours(JsonElement el)
    {
        if (!el.TryGetProperty("operationalHours", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray().Select(h => new OperationalHoursEntry
        {
            Day = h.GetProperty("day").GetInt32(),
            OpenTime = h.GetProperty("openTime").GetString() ?? "00:00:00.000",
            CloseTime = h.GetProperty("closeTime").GetString() ?? "00:00:00.000",
        }).ToList();
    }

    // -------------------------------------------------------------------------
    // Decryption key management

    /// <summary>
    /// Returns the AES-128 key for decrypting S3 tiles, fetching it from the app config on first use
    /// and caching it for the process lifetime. The key is held only in memory — never written to disk.
    /// </summary>
    private async Task<byte[]> GetDecryptionKeyAsync(CancellationToken ct)
    {
        if (_decryptionKey is not null)
            return Convert.FromBase64String(_decryptionKey);

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_decryptionKey is not null)
                return Convert.FromBase64String(_decryptionKey);

            var config = await _api.GetAppConfigAsync(_options.CompanyId, "v3", ct);
            _decryptionKey = ExtractParkingDataKey(config);
            return Convert.FromBase64String(_decryptionKey);
        }
        finally
        {
            _keyLock.Release();
        }
    }

    /// <summary>
    /// Pulls <c>parkingDataKey</c> out of the app config JSON (<c>modules[].subModules[].config</c>,
    /// both keyed "app"). Throws if the shape is unexpected or the key is absent.
    /// </summary>
    private static string ExtractParkingDataKey(object config)
    {
        if (config is not JsonElement root)
            throw new InvalidOperationException("Unexpected app config type.");

        var modules = root.GetProperty("modules");
        foreach (var module in modules.EnumerateArray())
        {
            if (module.GetProperty("id").GetString() != "app") continue;
            foreach (var sub in module.GetProperty("subModules").EnumerateArray())
            {
                if (sub.GetProperty("id").GetString() != "app") continue;
                var cfg = sub.GetProperty("config");
                if (cfg.TryGetProperty("parkingDataKey", out var keyProp))
                    return keyProp.GetString() ?? throw new InvalidOperationException("parkingDataKey is null.");
            }
        }

        throw new InvalidOperationException("parkingDataKey not found in app config.");
    }

    /// <summary>Decrypts an S3 tile laid out as <c>[nonce:12][ciphertext][tag:16]</c> with AES-128-GCM.</summary>
    private static byte[] DecryptGcm(byte[] data, byte[] key)
    {
        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(data.Length - 16, 16);
        var ciphertext = data.AsSpan(12, data.Length - 28);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>Age of a snapshot relative to now (UTC).</summary>
    private static TimeSpan Age(DateTimeOffset builtAtUtc) => DateTimeOffset.UtcNow - builtAtUtc;

    public void Dispose()
    {
        _keyLock.Dispose();
        _buildLock.Dispose();
        _http.Dispose();
    }
}
