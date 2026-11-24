namespace ApcoaFlowApiClient.ParkingData.Store;

/// <summary>A point-in-time slim parking dataset with the UTC time it was built.</summary>
public sealed class ParkingSnapshot
{
    public required DateTimeOffset BuiltAtUtc { get; init; }
    public required IReadOnlyList<ParkingEntry> Entries { get; init; }
}
