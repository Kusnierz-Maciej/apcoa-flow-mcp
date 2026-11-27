using System.Diagnostics;

namespace ApcoaFlowApiClient;

/// <summary>
/// App tracing: one <see cref="ActivitySource"/> plus null-safe helpers that tag or mark the current span. With no
/// listener (telemetry off) every call is a cheap no-op. Tag names follow the API's naming (space, vehicle, session).
/// </summary>
public static class ApcoaTelemetry
{
    /// <summary>Subscribe with <c>tracing.AddSource(ApcoaTelemetry.SourceName)</c>.</summary>
    public const string SourceName = "ApcoaFlow";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Span attribute keys (custom <c>apcoa.*</c> namespace + OTel semconv <c>user.email</c>).</summary>
    public static class Tags
    {
        public const string UserEmail = "user.email";
        public const string Outcome = "apcoa.outcome";
        public const string ClientName = "apcoa.client.name";
        public const string ClientVersion = "apcoa.client.version";
        public const string ClientTraceContext = "apcoa.client.trace_context";
        public const string ClientMetaKeys = "apcoa.client.meta_keys";
        public const string ResultsCount = "apcoa.results.count";
        public const string LicensePlates = "apcoa.license_plates";
    }

    /// <summary>Longest list attribute we record (ids, plates); a count tag carries the full size.</summary>
    public const int MaxListedValues = 20;

    /// <summary>A capped string-array attribute value (first <see cref="MaxListedValues"/> entries).</summary>
    public static string[] Capped(IEnumerable<string> values) => values.Take(MaxListedValues).ToArray();

    /// <summary>A child span for one phase of a tool (search, quote, start…). Dispose to end it.</summary>
    public static Activity? Phase(string name, params (string Key, object? Value)[] tags)
    {
        var activity = Source.StartActivity(name);
        if (activity is not null)
            foreach (var (k, v) in tags) activity.SetTag(k, v);
        return activity;
    }

    /// <summary>Sets a tag on the current span (the MCP <c>tools/call</c> span inside a tool).</summary>
    public static void Tag(string key, object? value) => Activity.Current?.SetTag(key, value);

    /// <summary>Records a business outcome on the current span (e.g. "started", "quote", "refused").</summary>
    public static void Outcome(string outcome) => Tag(Tags.Outcome, outcome);

    /// <summary>Marks the current span failed for a handled failure, with an outcome tag.</summary>
    public static void Fail(string outcome, string description)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        activity.SetTag(Tags.Outcome, outcome);
        activity.SetStatus(ActivityStatusCode.Error, description);
    }

    /// <summary>Adds a timestamped event to the current span.</summary>
    public static void Event(string name, params (string Key, object? Value)[] tags)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        var collection = new ActivityTagsCollection();
        foreach (var (k, v) in tags) collection[k] = v;
        activity.AddEvent(new ActivityEvent(name, tags: collection));
    }
}
