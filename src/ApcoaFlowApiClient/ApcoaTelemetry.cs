using System.Diagnostics;

namespace ApcoaFlowApiClient;

/// <summary>
/// App-specific tracing: one <see cref="ActivitySource"/> plus helpers that tag/mark the current span.
/// Everything is null-safe — with no listener (telemetry off) <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns null and <see cref="Activity.Current"/> carries no recorded span, so each call is a cheap no-op.
/// </summary>
public static class ApcoaTelemetry
{
    /// <summary>ActivitySource name — subscribe with <c>tracing.AddSource(ApcoaTelemetry.SourceName)</c>.</summary>
    public const string SourceName = "ApcoaFlow";

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Span attribute keys (custom <c>apcoa.*</c> namespace + OTel semconv <c>user.email</c>).</summary>
    public static class Tags
    {
        public const string UserEmail = "user.email";
        public const string ParkingId = "apcoa.parking.id";
        public const string ParkingName = "apcoa.parking.name";
        public const string LotId = "apcoa.lot.id";
        public const string TariffId = "apcoa.tariff.id";
        public const string Plate = "apcoa.plate";
        public const string StartMode = "apcoa.start.mode";
        public const string BypassCap = "apcoa.start.bypass_cap";
        public const string DurationMinutes = "apcoa.duration_min";
        public const string CostPln = "apcoa.cost_pln";
        public const string ConfirmedCostPln = "apcoa.confirmed_cost_pln";
        public const string Promotion = "apcoa.promotion";
        public const string SessionId = "apcoa.session.id";
        public const string DistanceMeters = "apcoa.distance_m";
        public const string RadiusMeters = "apcoa.radius_m";
        public const string NameFilter = "apcoa.name_filter";
        public const string ResultsCount = "apcoa.results.count";
        public const string ParkingIds = "apcoa.parking.ids";
        public const string Plates = "apcoa.plates";
        public const string SessionIds = "apcoa.session.ids";
        public const string Outcome = "apcoa.outcome";
        public const string IndexEntries = "apcoa.parking_index.entries";
        public const string IndexSource = "apcoa.parking_index.source";
        public const string IndexTiles = "apcoa.parking_index.tiles";
        public const string IndexFailedTiles = "apcoa.parking_index.failed_tiles";
    }

    /// <summary>Longest list attribute we record (ids/plates); the matching count tag carries the full size.</summary>
    public const int MaxListedValues = 20;

    /// <summary>A capped string-array attribute value (first <see cref="MaxListedValues"/> entries).</summary>
    public static string[] Capped(IEnumerable<string> values) => values.Take(MaxListedValues).ToArray();

    /// <summary>
    /// Starts a child span for one phase of a tool (resolve lot, quote, start, stop…) so a trace shows where time
    /// goes and which backend calls belong together. Dispose to end it. Null (free) when telemetry is off.
    /// While a phase is open it is <see cref="Activity.Current"/>, so set tool-level tags outside it.
    /// </summary>
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

    /// <summary>
    /// Marks the current span as failed for a handled, non-exception failure (refused start, cost cap,
    /// credentials rejected…) so Grafana's "Errors" filter and TraceQL <c>{ status = error }</c> find it.
    /// </summary>
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
