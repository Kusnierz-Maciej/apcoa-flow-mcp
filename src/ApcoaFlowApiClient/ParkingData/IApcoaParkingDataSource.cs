namespace ApcoaFlowApiClient.ParkingData;

/// <summary>
/// Provides searchable access to the APCOA offline parking dataset (S3-backed, AES-128-GCM encrypted).
/// Data is cached in memory for <see cref="ParkingDataSourceOptions.CacheDuration"/> (default 24 h).
/// </summary>
public interface IApcoaParkingDataSource
{
    /// <summary>
    /// Returns all parkings within <paramref name="radiusKm"/> of the given coordinates, ordered by distance ascending.
    /// If <paramref name="nameContains"/> is provided, only parkings whose display name contains it
    /// (case- and diacritic-insensitive) are returned — e.g. "Carrefour near me".
    /// Served from the in-memory index; triggers a background refresh if the data is stale.
    /// </summary>
    Task<IReadOnlyList<(ParkingEntry Parking, double DistanceKm)>> FindNearbyAsync(
        double latitude,
        double longitude,
        double radiusKm,
        string? nameContains = null,
        CancellationToken ct = default);

    /// <summary>Returns a single parking by ID, or null if not found.</summary>
    Task<ParkingEntry?> GetByIdAsync(int parkingId, CancellationToken ct = default);

    /// <summary>Returns all parkings for the given ISO country code.</summary>
    Task<IReadOnlyList<ParkingEntry>> GetAllForCountryAsync(string countryCode, CancellationToken ct = default);
}
