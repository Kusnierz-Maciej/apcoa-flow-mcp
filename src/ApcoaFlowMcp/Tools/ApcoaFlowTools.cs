using System.ComponentModel;
using System.Globalization;
using System.Text;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using ApcoaFlowMcp.Apcoa;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Tools;

/// <summary>
/// The MCP tools. Each public method with <c>[McpServerTool]</c> is registered by <see cref="ToolRegistration"/>
/// (a fresh instance per call, from the request's services). Naming: a <b>parking</b> is the place, a
/// <b>session</b> is parking there. docs/MCP-TOOLS.md is the human-facing reference: update it with any change here.
/// </summary>
[McpServerToolType]
public sealed class ApcoaFlowTools(
    IApcoaFlowApi api,
    ParkingFinder finder,
    Pricing pricing,
    IOptions<ApcoaFlowToolsOptions> options,
    TimeProvider time,
    ILogger<ApcoaFlowTools> logger)
{
    private readonly ApcoaFlowToolsOptions _options = options.Value;

    private const string LicensePlateDescription = "License plate of one of the account's vehicles (see get_vehicles), e.g. \"ABC12345\"; spaces and case don't matter.";

    // ── Vehicles ───────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_vehicles", Title = "Get vehicles", ReadOnly = true, Idempotent = true)]
    [Description("List the vehicles (license plates) on the account. Returns one line per vehicle as " +
                 "\"plate [default] - description\".")]
    public Task<string> GetVehiclesAsync(CancellationToken ct = default)
        => ToolErrors.GuardAsync("get_vehicles", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "get_vehicles");
        var vehicles = await api.GetVehiclesAsync(ct);
        logger.LogInformation("get_vehicles: {Count} vehicle(s) [{Plates}]",
            vehicles.Count, string.Join(", ", vehicles.Select(v => v.LicensePlate)));
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ResultsCount, vehicles.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.LicensePlates, ApcoaTelemetry.Capped(vehicles.Select(v => v.LicensePlate)));
        ApcoaTelemetry.Outcome("ok");
        if (vehicles.Count == 0)
            return "No vehicles on the account.";

        var sb = new StringBuilder();
        foreach (var v in vehicles)
        {
            var isDefault = v.IsDefault ? " [default]" : "";
            var description = string.IsNullOrWhiteSpace(v.Description) ? "" : $" - {v.Description}";
            sb.AppendLine($"{v.LicensePlate}{isDefault}{description}");
        }
        return sb.ToString().TrimEnd();
    });

    // ── Parkings ───────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "find_parkings", Title = "Find APCOA parkings", ReadOnly = true, Idempotent = true)]
    [Description("Find APCOA Flow parkings by text (name, shop brand like \"Biedronka\", town, postcode or the " +
                 "parking's location code) and/or near a position. Near a position the nearest come first, measured to " +
                 "the parking's outline when it has one (\"inside\" when the position is in it), else to its pin. " +
                 "Returns one line per parking: \"[parkingId] name - distance - address (code …)\".")]
    public Task<string> FindParkingsAsync(
        [Description("Text to search for: name, brand, town, postcode or location code. Optional when a position is given.")] string? query = null,
        [Description("Latitude of the position to search around (decimal degrees). Give it with longitude.")] double? latitude = null,
        [Description("Longitude of the position to search around (decimal degrees).")] double? longitude = null,
        [Description("Search radius in metres around the position (default 2000). Keep it small for 'near me'.")] double? radiusMeters = null,
        CancellationToken ct = default)
        => ToolErrors.GuardAsync("find_parkings", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "find_parkings");
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var hasPosition = latitude is not null && longitude is not null;
        if (text is null && !hasPosition)
        {
            ApcoaTelemetry.Fail("missing_input", "neither query nor position");
            return "Give a text to search for (name, brand, town, location code), a position (latitude and longitude), or both.";
        }

        var radius = radiusMeters is > 0 ? radiusMeters.Value : 2000;
        List<FoundParking> found;
        var note = "";
        if (hasPosition)
        {
            found = await finder.NearAsync(latitude!.Value, longitude!.Value, radius, text, ct);
            if (found.Count == 0 && text is not null)
            {
                // Nothing by that name in the radius: the nearest matches anywhere, ranked by the API.
                found = await finder.SearchAsync(text, latitude, longitude, ct);
                if (found.Count > 0)
                    note = FormattableString.Invariant($"No \"{text}\" parking within {radius:0} m; the nearest ones:\n");
            }
        }
        else
        {
            found = await finder.SearchAsync(text!, null, null, ct);
        }

        logger.LogInformation("find_parkings '{Query}' at {Lat},{Lon} r={Radius}: {Count} parking(s)",
            text, latitude, longitude, radius, found.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ResultsCount, found.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingIds, ApcoaTelemetry.Capped(found.Select(p => p.Id)));
        ApcoaTelemetry.Outcome("ok");
        if (found.Count == 0)
            return hasPosition
                ? FormattableString.Invariant($"No APCOA Flow parking{(text is null ? "" : $" matching \"{text}\"")} within {radius:0} m.")
                : $"No APCOA Flow parking matches \"{text}\".";

        var sb = new StringBuilder(note);
        foreach (var p in found)
        {
            sb.Append($"[{p.Id}] {p.Name}");
            if (p.DistanceMeters is { } m)
                sb.Append(p.Inside ? " - you are inside it" : $" - {Meters(m)}{(p.FromOutline ? " (to its edge)" : "")}");
            if (p.Address is not null) sb.Append($" - {p.Address}");
            if (p.Code is not null) sb.Append($" (code {p.Code})");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    });

    [McpServerTool(Name = "get_parking", Title = "Get parking details", ReadOnly = true, Idempotent = true)]
    [Description("Details of one parking: address, location code, whether it's open, its tariff table and rules (as " +
                 "the operator writes them), the longest allowed stay. For the price of a specific stay use get_price.")]
    public Task<string> GetParkingAsync(
        [Description("Parking id (from find_parkings)")] string parkingId,
        CancellationToken ct = default)
        => ToolErrors.GuardAsync("get_parking", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "get_parking");
        var p = await api.GetSpaceAsync(parkingId.Trim(), ct);
        logger.LogInformation("get_parking [{Id}] {Name}", p.Id, p.Name);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingId, p.Id);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingName, p.Name);
        ApcoaTelemetry.Outcome("ok");

        var sb = new StringBuilder($"[{p.Id}] {p.Name}");
        if (!string.IsNullOrWhiteSpace(p.TariffCode)) sb.Append($" (code {p.TariffCode})");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(p.Address?.DisplayValue)) sb.AppendLine($"Address: {p.Address.DisplayValue.Trim().TrimStart(',').Trim()}");
        var status = string.Join(", ", p.ParkingOverview.Select(o => o.Title).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (status.Length > 0) sb.AppendLine($"Status: {status}");
        if (p.MaxDuration is > 0) sb.AppendLine($"Longest stay: {Duration(p.MaxDuration.Value)}");
        foreach (var section in p.Prices)
        {
            // Items can be a note without a price ("Prices include convenience fees").
            var items = string.Join(", ", section.Prices
                .Where(i => !string.IsNullOrWhiteSpace(i.Label) || !string.IsNullOrWhiteSpace(i.Price))
                .Select(i => string.IsNullOrWhiteSpace(i.Price) ? i.Label!.Trim() : $"{i.Label?.Trim()}: {Pln(i.Price)}"));
            sb.AppendLine(items.Length > 0 ? $"{section.SectionName}: {items}" : $"{section.SectionName}");
            if (!string.IsNullOrWhiteSpace(section.Description)) sb.AppendLine($"  {section.Description.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(p.Disclaimer)) sb.AppendLine(p.Disclaimer.Trim());
        return sb.ToString().TrimEnd();
    });

    [McpServerTool(Name = "get_price", Title = "Get parking price", ReadOnly = true, Idempotent = true)]
    [Description("The price of a stay starting now at a parking for one of the account's vehicles, as APCOA quotes it " +
                 "(free time the parking gives, e.g. a shop's free first 1.5 h, is already included), plus how long the " +
                 "stay can be for free when it is free.")]
    public Task<string> GetPriceAsync(
        [Description("Parking id (from find_parkings)")] string parkingId,
        [Description(LicensePlateDescription)] string licensePlate,
        [Description("Length of the stay in minutes.")] int durationMinutes,
        CancellationToken ct = default)
        => ToolErrors.GuardAsync("get_price", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "get_price");
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingId, parkingId);
        if (durationMinutes < 1)
        {
            ApcoaTelemetry.Fail("bad_duration", "duration < 1 min");
            return "The duration must be at least 1 minute.";
        }
        var vehicle = Plates.Find(await api.GetVehiclesAsync(ct), licensePlate);
        if (vehicle is null)
        {
            ApcoaTelemetry.Fail("plate_not_registered", "unknown plate");
            return $"\"{licensePlate}\" isn't one of the account's vehicles. Call get_vehicles for the plates.";
        }
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.LicensePlate, vehicle.LicensePlate);

        var space = await api.GetSpaceAsync(parkingId.Trim(), ct);
        var quote = await pricing.QuoteAsync(space.Id, vehicle.Id, durationMinutes, ct);
        var total = quote.Total;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)total.Amount);

        var sb = new StringBuilder($"{space.Name}, {vehicle.LicensePlate}, {Duration(durationMinutes)} from now: {total}");
        sb.Append(quote.PrepaymentMode == "autoCharge" ? " (charged at start)" : " (charged at the end)");
        sb.AppendLine(".");
        var window = await pricing.FreeWindowAsync(space.Id, vehicle.Id, space.DefaultLengthOfStayInMinutes, space.MaxDuration, ct);
        if (window.Minutes > 0)
            sb.AppendLine(window.AtLeast
                ? $"Free for at least {Duration(window.Minutes)} for this vehicle right now."
                : $"Free for up to {Duration(window.Minutes)} for this vehicle right now; longer stays cost money.");
        if (space.MaxDuration is > 0 && durationMinutes > space.MaxDuration)
            sb.AppendLine($"Note: the longest allowed stay here is {Duration(space.MaxDuration.Value)}.");

        logger.LogInformation("get_price [{Id}] {Name}: {Plate}, {Minutes} min → {Total}; free window {Free} min",
            space.Id, space.Name, vehicle.LicensePlate, durationMinutes, total, window.Minutes);
        ApcoaTelemetry.Outcome("ok");
        return sb.ToString().TrimEnd();
    });

    // ── Sessions ───────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_active_sessions", Title = "Get active parking sessions", ReadOnly = true, Idempotent = true)]
    [Description("The running parking sessions on the account: session id (for extend_session / end_session), parking, " +
                 "plate, start and end time, time left, how it's paid.")]
    public Task<string> GetActiveSessionsAsync(CancellationToken ct = default)
        => ToolErrors.GuardAsync("get_active_sessions", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "get_active_sessions");
        var sessions = await api.GetSessionsAsync(ct);
        logger.LogInformation("get_active_sessions: {Count} session(s)", sessions.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ResultsCount, sessions.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionIds, ApcoaTelemetry.Capped(sessions.Select(s => s.Id)));
        ApcoaTelemetry.Outcome("ok");
        if (sessions.Count == 0)
            return "No parking session is running.";

        var now = time.GetUtcNow();
        var sb = new StringBuilder();
        foreach (var s in sessions)
            sb.AppendLine(DescribeSession(s, now));
        return sb.ToString().TrimEnd();
    });

    [McpServerTool(Name = "get_session_history", Title = "Get parking history", ReadOnly = true, Idempotent = true)]
    [Description("The account's recent parking sessions, newest first: parking, plate, when it ended, what it cost.")]
    public Task<string> GetSessionHistoryAsync(CancellationToken ct = default)
        => ToolErrors.GuardAsync("get_session_history", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "get_session_history");
        var history = await api.GetSessionsHistoryAsync(page: 1, size: 10, ct: ct);
        var total = history.Select(h => Money.TryParse(h.TotalPrice, out var m) ? m.Amount : 0m).Sum();
        logger.LogInformation("get_session_history: {Count} session(s), {Total:0.00} PLN in total", history.Count, total);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ResultsCount, history.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.SessionIds, ApcoaTelemetry.Capped(history.Select(h => h.Id)));
        ApcoaTelemetry.Outcome("ok");
        if (history.Count == 0)
            return "No past parking sessions on the account.";

        var sb = new StringBuilder();
        foreach (var h in history)
        {
            var ended = h.EndDate is { } end ? $"ended {LocalTime(end)}" : "end time unknown";
            var state = h.State is null or "billed" ? "" : $", {h.State}";
            sb.AppendLine($"{h.SpaceName} - {h.LicensePlate}, {ended}, {Pln(h.TotalPrice)}{state}");
        }
        return sb.ToString().TrimEnd();
    });

    // ── Formatting ─────────────────────────────────────────────────────────────────────────────

    internal string DescribeSession(Session s, DateTimeOffset now)
    {
        var sb = new StringBuilder($"[{s.Id}] {s.Space?.Name ?? "unknown parking"} - {s.Vehicle?.LicensePlate}, from {LocalTime(s.StartDate)}");
        if (s.EndDate is { } end)
        {
            var left = end - now;
            sb.Append($" until {LocalTime(end)}");
            sb.Append(left > TimeSpan.Zero ? $" ({Duration((int)Math.Ceiling(left.TotalMinutes))} left)" : " (overdue)");
        }
        sb.Append(s.PaymentFlow switch
        {
            "prePay" => ", paid at start",
            "postPay" => ", charged at the end",
            _ => "",
        });
        if (!s.SessionCanBeExtended) sb.Append(", can't be extended");
        if (!s.SessionCanBeStopped) sb.Append(", can't be stopped early");
        return sb.ToString();
    }

    private string LocalTime(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, ApcoaFlowEndpoints.AppTimeZone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), ApcoaFlowEndpoints.AppTimeZone).Date;
        return local.Date == today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    internal static string Duration(int minutes) => minutes switch
    {
        < 60 => $"{minutes} min",
        _ when minutes % (24 * 60) == 0 => minutes == 24 * 60 ? "1 day" : $"{minutes / (24 * 60)} days",
        _ when minutes % 60 == 0 => minutes == 60 ? "1 h" : $"{minutes / 60} h",
        _ => $"{minutes / 60} h {minutes % 60} min",
    };

    private static string Meters(double m) =>
        m < 1000 ? FormattableString.Invariant($"{m:0} m") : FormattableString.Invariant($"{m / 1000:0.0} km");

    private static string Pln(string? apiPrice) => Money.TryParse(apiPrice, out var money) ? money.ToString() : apiPrice ?? "?";
}
