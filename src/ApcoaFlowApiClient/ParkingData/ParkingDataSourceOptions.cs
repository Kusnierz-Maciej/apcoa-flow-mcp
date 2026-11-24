namespace ApcoaFlowApiClient.ParkingData;

public class ParkingDataSourceOptions
{
    /// <summary>
    /// Past this age, a search returns the current (stale) index immediately and triggers a
    /// non-blocking background rebuild (stale-while-revalidate). Default 12 h.
    /// </summary>
    public TimeSpan SoftRefreshAge { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Past this age, the index is too old to serve: the next search blocks on an in-place rebuild
    /// before returning. Safety floor. Default 48 h.
    /// </summary>
    public TimeSpan HardRefreshAge { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    /// Cadence of the proactive background refresh performed by the hosted service (web host only).
    /// Keeps the index fresh so the soft/hard paths rarely fire. Default 24 h.
    /// </summary>
    public TimeSpan BackgroundRefreshInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Directory where the parking snapshot is persisted (mapped Docker volume). Default /data/parking.</summary>
    public string StorePath { get; set; } = "/data/parking";

    /// <summary>
    /// Geohash precision used for spatial bucketing. 6 ≈ 1.2 × 0.6 km cells, tuned for the typical
    /// 500 m–2 km search. Correctness holds for any radius regardless of this value.
    /// </summary>
    public int GeohashPrecision { get; set; } = 6;

    /// <summary>Company ID used to fetch the app config (and therefore the decryption key). Defaults to 9 (Poland).</summary>
    public int CompanyId { get; set; } = 9;
}
