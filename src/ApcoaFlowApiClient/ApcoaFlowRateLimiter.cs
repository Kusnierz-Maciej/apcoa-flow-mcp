using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace ApcoaFlowApiClient;

/// <summary>Binds the <c>RateLimit</c> section: the client-side limit on every APCOA call.</summary>
public sealed class ApcoaFlowRateLimitOptions
{
    public const string Section = "RateLimit";

    /// <summary>Burst size: calls allowed back to back before throttling. Default 10.</summary>
    public int TokenLimit { get; set; } = 10;

    /// <summary>One more call is allowed per period once the burst is used. Default 1 s (1 call/s).</summary>
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Positive, sane values only — a typo must not disable the limit or stall every call.</summary>
public sealed class ApcoaFlowRateLimitOptionsValidator : IValidateOptions<ApcoaFlowRateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, ApcoaFlowRateLimitOptions options)
    {
        var failures = new List<string>();
        if (options.TokenLimit is < 1 or > 1000)
            failures.Add($"RateLimit:TokenLimit must be between 1 and 1000 (got {options.TokenLimit}).");
        if (options.ReplenishmentPeriod <= TimeSpan.Zero || options.ReplenishmentPeriod > TimeSpan.FromMinutes(1))
            failures.Add($"RateLimit:ReplenishmentPeriod must be greater than 0 and at most 00:01:00 (got {options.ReplenishmentPeriod}).");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Client-side rate limit shared by every APCOA call (API and login), so the process as a whole never
/// hammers the backend and trips its 403 lockout. Token bucket: a burst of up to
/// <see cref="ApcoaFlowRateLimitOptions.TokenLimit"/> calls, then one call per
/// <see cref="ApcoaFlowRateLimitOptions.ReplenishmentPeriod"/>. Calls over the limit queue (FIFO) rather than
/// fail, so callers just slow down. Singleton; both HTTP pipelines share <see cref="Limiter"/>.
/// </summary>
public sealed class ApcoaFlowRateLimiter(IOptions<ApcoaFlowRateLimitOptions> options) : IDisposable
{
    public ApcoaFlowRateLimitOptions Options { get; } = options.Value;

    public RateLimiter Limiter { get; } = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = options.Value.TokenLimit,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = options.Value.ReplenishmentPeriod,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = int.MaxValue,
        AutoReplenishment = true,
    });

    public void Dispose() => Limiter.Dispose();
}
