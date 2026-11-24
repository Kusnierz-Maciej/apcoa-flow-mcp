using System.Collections.Concurrent;

namespace ApcoaFlowApiClient;

/// <summary>
/// Thread-safe, per-credential Bearer token store shared across all requests.
/// </summary>
public sealed class ApcoaFlowTokenStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed class Entry
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public string? Token;
    }

    private static string KeyFor(ApcoaFlowClientOptions options) =>
        $"{options.Email}:{options.Password}";

    public async Task<string> GetOrLoginAsync(
        ApcoaFlowClientOptions credentials,
        Func<CancellationToken, Task<string>> login,
        CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(KeyFor(credentials), _ => new Entry());

        if (entry.Token is { } cached)
            return cached;

        await entry.Lock.WaitAsync(cancellationToken);
        try
        {
            return entry.Token ??= await login(cancellationToken);
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public void Invalidate(ApcoaFlowClientOptions credentials, string staleToken)
    {
        if (_entries.TryGetValue(KeyFor(credentials), out var entry) &&
            entry.Token == staleToken)
        {
            entry.Token = null;
        }
    }
}
