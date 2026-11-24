namespace ApcoaFlowApiClient.ParkingData.Store;

/// <summary>
/// Persists the slim parking dataset to durable storage (a mapped Docker volume in production),
/// so the index survives process restarts without re-downloading from S3.
/// </summary>
public interface IParkingDataStore
{
    /// <summary>Loads the persisted snapshot, or null if none exists or it is unreadable/incompatible.</summary>
    Task<ParkingSnapshot?> LoadAsync(CancellationToken ct = default);

    /// <summary>Persists the snapshot atomically (temp file + rename).</summary>
    Task SaveAsync(ParkingSnapshot snapshot, CancellationToken ct = default);
}
