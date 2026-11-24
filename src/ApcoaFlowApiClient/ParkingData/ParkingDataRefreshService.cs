using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApcoaFlowApiClient.ParkingData;

/// <summary>
/// Proactively keeps the parking index fresh in the web host. On start it warms the index
/// (from disk or a build); then it rebuilds on <see cref="ParkingDataSourceOptions.BackgroundRefreshInterval"/>.
/// Failures are logged and swallowed so a refresh never crashes the host.
/// Not registered in the stdio host.
/// </summary>
public sealed class ParkingDataRefreshService : BackgroundService
{
    private readonly IApcoaParkingDataSource _source;
    private readonly ParkingDataSourceOptions _options;
    private readonly ILogger<ParkingDataRefreshService> _logger;

    public ParkingDataRefreshService(
        IApcoaParkingDataSource source,
        IOptions<ParkingDataSourceOptions> options,
        ILogger<ParkingDataRefreshService> logger)
    {
        _source = source;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Warm the index on startup (loads from disk if a fresh snapshot exists, else builds).
        await SafeRefreshAsync(warmOnly: true, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.BackgroundRefreshInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await SafeRefreshAsync(warmOnly: false, stoppingToken);
        }
    }

    /// <summary>
    /// Warms or rebuilds the index, swallowing failures. When <paramref name="warmOnly"/> is true it
    /// triggers the normal freshness path (load-from-disk or build) via a throwaway lookup rather than
    /// forcing a rebuild; otherwise it forces a full rebuild.
    /// </summary>
    private async Task SafeRefreshAsync(bool warmOnly, CancellationToken ct)
    {
        try
        {
            if (warmOnly)
            {
                // A no-op search warms the index via the normal freshness path without forcing a rebuild.
                await _source.GetByIdAsync(0, ct);
            }
            else if (_source is ApcoaS3ParkingDataSource concrete)
            {
                await concrete.RefreshAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled parking index refresh failed; keeping current data.");
        }
    }
}
