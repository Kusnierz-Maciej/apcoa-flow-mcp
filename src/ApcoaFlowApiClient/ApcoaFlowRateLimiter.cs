using System.Threading.RateLimiting;

namespace ApcoaFlowApiClient;

/// <summary>
/// Client-side rate limit shared by every APCOA call (API and login), so the process as a whole never
/// hammers the backend and trips its 403 lockout. Token bucket: a burst of up to <see cref="TokenLimit"/>
/// requests, then a long-run average of one request per <see cref="ReplenishmentPeriod"/>. Requests over the
/// limit queue (FIFO) rather than fail, so callers just slow down.
/// Registered as a singleton; both HTTP pipelines use the same <see cref="Limiter"/> instance.
/// </summary>
public sealed class ApcoaFlowRateLimiter : IDisposable
{
    public const int TokenLimit = 10;
    public static readonly TimeSpan ReplenishmentPeriod = TimeSpan.FromSeconds(1);

    public RateLimiter Limiter { get; } = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = TokenLimit,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = ReplenishmentPeriod,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = int.MaxValue,
        AutoReplenishment = true,
    });

    public void Dispose() => Limiter.Dispose();
}
