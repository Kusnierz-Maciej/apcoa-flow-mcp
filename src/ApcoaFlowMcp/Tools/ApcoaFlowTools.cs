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
    Sessions sessions,
    IOptions<ApcoaFlowToolsOptions> options,
    TimeProvider time,
    ILogger<ApcoaFlowTools> logger)
{
    private readonly ApcoaFlowToolsOptions _options = options.Value;

    private const string ParkingNameDescription =
        "The parking's name exactly as find_parkings shows it. Shown in the permission prompt so the user can confirm " +
        "the right parking; not sent to the API.";

    private const string ConfirmedCostDescription =
        "For a PAID stay: the exact cost in PLN from this tool's previous answer, after the user agreed to it. Omit on the " +
        "first call; a free stay never needs it.";

    /// <summary>Two parkings whose distances differ by less than this are too close to call from a phone's GPS.</summary>
    public const double GpsToleranceMeters = 30;

    private const string LicensePlateDescription = "License plate of one of the account's vehicles (see get_vehicles), e.g. \"ABC12345\"; whitespace and case don't matter.";

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
    [Description("Find APCOA Flow parkings by name or text (shop brand like \"Biedronka\", town, street, postcode or the " +
                 "parking's location code) and/or near a position. With a position the nearest come first, measured to " +
                 "the parking's outline when it has one (\"you are inside it\" when the position is in it), else to its pin. " +
                 "Returns one line per parking: \"[parkingId] name - distance - address (code …)\"; the first 5 also get a " +
                 "short summary: free time, the 1 h / 2 h prices and today's day max (e.g. \"free up to 1 h 30 min (any car) · " +
                 "1 h 0.00 / 2 h 2.00 PLN · day max today 90.00 PLN\"). With a licensePlate the free time is checked for that " +
                 "vehicle (\"free now for up to …\" or \"paid now\").")]
    public Task<string> FindParkingsAsync(
        [Description("Name or text to search for: brand, town, street, postcode or location code. Optional when a position is given.")] string? query = null,
        [Description("Latitude of the position to search around (decimal degrees). Give it with longitude.")] double? latitude = null,
        [Description("Longitude of the position to search around (decimal degrees).")] double? longitude = null,
        [Description("Without a query: the radius in metres around the position (default 2000). With a query the nearest matches anywhere are listed.")] double? radiusMeters = null,
        [Description("Optional: one of the account's vehicles. Then the free time in the first 5 summaries is checked for it right now (e.g. a shop's free time may be used up). A bit slower: pass it when free parking matters.")] string? licensePlate = null,
        CancellationToken ct = default)
        => ToolErrors.GuardAsync("find_parkings", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "find_parkings");
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var hasPosition = latitude is not null && longitude is not null;
        if (text is null && !hasPosition)
        {
            ApcoaTelemetry.Fail("missing_input", "neither query nor position");
            return "Give a name or text to search for (brand, town, street, location code), a position (latitude and longitude), or both.";
        }

        var radius = radiusMeters is > 0 ? radiusMeters.Value : 2000;
        var found = text is null
            ? await finder.NearAsync(latitude!.Value, longitude!.Value, radius, ct)
            : await finder.SearchAsync(text, hasPosition ? latitude : null, hasPosition ? longitude : null, radius, ct);

        logger.LogInformation("find_parkings '{Query}' at {Lat},{Lon} r={Radius}: {Count} parking(s)",
            text, latitude, longitude, radius, found.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ResultsCount, found.Count);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingIds, ApcoaTelemetry.Capped(found.Select(p => p.Id)));
        ApcoaTelemetry.Outcome("ok");
        if (found.Count == 0)
            return text is null
                ? FormattableString.Invariant($"No APCOA Flow parking within {radius:0} m.")
                : $"No APCOA Flow parking matches \"{text}\".";

        var sb = new StringBuilder();
        string? vehicleId = null;
        if (!string.IsNullOrWhiteSpace(licensePlate))
        {
            vehicleId = Plates.Find(await api.GetVehiclesAsync(ct), licensePlate)?.Id;
            if (vehicleId is null)
            {
                ApcoaTelemetry.Fail("plate_not_registered", "free-time check: unknown plate");
                sb.AppendLine($"(\"{licensePlate}\" isn't one of the account's vehicles, so free time wasn't checked for it; see get_vehicles.)");
            }
        }
        var freeNotes = await SummariesAsync(found.Take(FreeCheckCount), vehicleId, ct);

        foreach (var p in found)
        {
            sb.Append($"[{p.Id}] {p.Name}");
            if (p.DistanceMeters is { } m)
                sb.Append(p.Inside ? " - you are inside it" : $" - {Text.Meters(m)}{(p.FromOutline ? " (to its edge)" : "")}");
            if (p.Address is not null) sb.Append($" - {p.Address}");
            if (p.Code is not null) sb.Append($" (code {p.Code})");
            if (freeNotes.TryGetValue(p.Id, out var free)) sb.Append($" - {free}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    });

    /// <summary>How many search results <c>find_parkings</c> summarises (free time and prices).</summary>
    public const int FreeCheckCount = 5;

    /// <summary>
    /// A short summary per parking: free time, the 1 h / 2 h prices and the day maximum, from one details call with the
    /// hourly prices. With a vehicle the free time is verified with one quote for it ("free now for up to …" / "paid
    /// now"); without one it's what the prices say for any car.
    /// </summary>
    private async Task<Dictionary<string, string>> SummariesAsync(IEnumerable<FoundParking> parkings, string? vehicleId, CancellationToken ct)
    {
        var notes = new Dictionary<string, string>();
        foreach (var p in parkings)
        {
            try
            {
                var details = await pricing.DetailsWithCurveAsync(p.Id, ct);
                var curve = TariffTable.Read(details);
                var parts = new List<string>();
                if (vehicleId is not null)
                {
                    var window = await pricing.FreeWindowAsync(details, vehicleId, ct);
                    parts.Add(window switch
                    {
                        { Minutes: 0 } => "paid now",
                        { AtLeast: false } => $"free now for up to {Text.Duration(window.Minutes)}",
                        { Minutes: > 1 } => $"free now for at least {Text.Duration(window.Minutes)}",
                        _ => "free now (get_price says for how long)",
                    });
                }
                else if (TariffTable.FreeGuess(curve, details.DefaultLengthOfStayInMinutes) is { } free && curve.Marks.Count > 0)
                    parts.Add($"free {(free.Exact ? "up to" : "for at least")} {Text.Duration(free.Minutes)} (any car)");

                var hourly = new[] { 1, 2 }.Select(h => (h, price: curve.At(h))).Where(x => x.price is not null).ToList();
                if (hourly.Count > 0)
                    parts.Add(string.Join(" / ", hourly.Select(x => $"{x.h} h {x.price!.Value.Amount.ToString("0.00", CultureInfo.InvariantCulture)}")) + " PLN");
                if (curve.DayMax is { } dayMax)
                    parts.Add(DayMaxNote(dayMax));
                if (parts.Count > 0)
                    notes[p.Id] = string.Join(" · ", parts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ToolErrors.Map(ex) is not null)
            {
                logger.LogInformation("find_parkings: no summary for {Parking} ({Error})", p.Id, ex.Message);
            }
        }
        return notes;
    }

    /// <summary>The "Max day price" row: the current day's maximum (not the price of a 24 h stay).</summary>
    internal static string DayMaxNote(Money dayMax) => $"day max today {dayMax}";

    [McpServerTool(Name = "get_parking_details", Title = "Get parking details", ReadOnly = true, Idempotent = true)]
    [Description("Everything about one parking: name and location code, location (address, coordinates, whether it has " +
                 "an outline on the map), status, how sessions work there (started in the app, or automatically by plate " +
                 "recognition), the longest and the usual stay, the pricing as the operator writes it (tariff table and " +
                 "rules), and amenities (EV charging, season tickets, pre-booking, payment). For the price of a specific " +
                 "stay for your vehicle use get_price.")]
    public Task<string> GetParkingDetailsAsync(
        [Description("Parking id (from find_parkings)")] string parkingId,
        CancellationToken ct = default)
        => ToolErrors.GuardAsync("get_parking_details", logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", "get_parking_details");
        var p = await pricing.DetailsWithCurveAsync(parkingId.Trim(), ct);
        var pin = await finder.PinAsync(p.Id, p.Coordinates, ct);
        logger.LogInformation("get_parking_details [{Id}] {Name}", p.Id, p.Name);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingId, p.Id);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ParkingName, p.Name);
        ApcoaTelemetry.Outcome("ok");

        var sb = new StringBuilder($"[{p.Id}] {p.Name}");
        if (!string.IsNullOrWhiteSpace(p.TariffCode)) sb.Append($" (location code {p.TariffCode})");
        sb.AppendLine();

        sb.AppendLine("Location:");
        if (!string.IsNullOrWhiteSpace(p.Address?.DisplayValue)) sb.AppendLine($"  Address: {p.Address.DisplayValue.Trim().TrimStart(',').Trim()}");
        if (p.Coordinates is { } c) sb.AppendLine(FormattableString.Invariant($"  Coordinates: {c.Latitude:0.######}, {c.Longitude:0.######}"));
        if (pin is not null)
            sb.AppendLine(Geo.Ring(pin.GeoBorder) is { Count: >= 3 } ring
                ? $"  Outline on the map: yes ({ring.Count} points; distances are measured to its edge)"
                : "  Outline on the map: no (distances are measured to its pin)");

        sb.AppendLine("Sessions:");
        var status = string.Join(", ", p.ParkingOverview.Select(o => o.Title).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (status.Length > 0) sb.AppendLine($"  Status: {status}");
        sb.AppendLine(p.CtaButtonType is null or "startParking" // same rule as the start tools
            ? "  You start and end a session (start_session / start_free_session, or in the APCOA Flow app)."
            : "  Sessions can't be started here (not in the APCOA Flow app either).");
        if (p.SessionsStartAutomatically || pin?.ParkingType == "anpr")
            sb.AppendLine("  Plate recognition: sessions start and end automatically when the car enters and leaves.");
        if (p.DefaultLengthOfStayInMinutes is > 0) sb.AppendLine($"  Usual stay: {Text.Duration(p.DefaultLengthOfStayInMinutes.Value)}");
        if (p.MaxDuration is > 0) sb.AppendLine($"  Longest stay: {Text.Duration(p.MaxDuration.Value)}");

        sb.AppendLine("Pricing:");
        var curve = TariffTable.Read(p);
        if (curve.Marks.Count > 0)
            sb.AppendLine($"  Price if you start now (any vehicle; get_price checks yours): {Curve(curve.Marks)}");
        if (curve.DayMax is { } dayMax)
            sb.AppendLine($"  {DayMaxNote(dayMax)} (a stay of 24 h or more is priced by get_price)");
        foreach (var section in p.Prices)
        {
            if (!string.IsNullOrWhiteSpace(section.Description))
                sb.AppendLine($"  Rules: {section.Description.Trim()}");
            else if (section.Prices.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Price) && !string.IsNullOrWhiteSpace(i.Label)) is { } note)
                sb.AppendLine($"  {section.SectionName}: {note.Label!.Trim()}"); // e.g. "Prices include convenience fees"
        }

        var amenities = new List<string>();
        if (p.CanStartCharging) amenities.Add("EV charging");
        if (p.HasAvailableContracts) amenities.Add("season tickets");
        if (p.HasPrebooking) amenities.Add("pre-booking");
        if (p.HasFlexiPermits) amenities.Add("flexi permits");
        if (p.IsMunicipality || pin?.IsMunicipality == true) amenities.Add("municipal parking");
        sb.AppendLine($"Amenities: {(amenities.Count > 0 ? string.Join(", ", amenities) : "none listed")}");
        if (p.AllowedPSPs.Count > 0)
            sb.AppendLine($"Payment: card ({string.Join(", ", p.AllowedPSPs.Select(psp => psp.Length > 0 ? char.ToUpperInvariant(psp[0]) + psp[1..] : psp))})");
        if (!string.IsNullOrWhiteSpace(p.Disclaimer)) sb.AppendLine($"Note: {p.Disclaimer.Trim()}");
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

        var parking = await pricing.DetailsWithCurveAsync(parkingId.Trim(), ct);
        var quote = await pricing.QuoteAsync(parking.Id, vehicle.Id, durationMinutes, ct);
        var total = quote.Total;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.CostPln, (double)total.Amount);

        var sb = new StringBuilder($"{parking.Name}, {vehicle.LicensePlate}, {Text.Duration(durationMinutes)} from now: {total}");
        sb.Append(quote.PrepaymentMode == "autoCharge" ? " (charged at start)" : " (charged at the end)");
        sb.AppendLine(".");
        var window = await pricing.FreeWindowAsync(parking, vehicle.Id, ct);
        if (window.Minutes > 0)
            sb.AppendLine(window switch
            {
                { AtLeast: false } => $"Free for up to {Text.Duration(window.Minutes)} for this vehicle right now; longer stays cost money.",
                { Minutes: > 1 } => $"Free for at least {Text.Duration(window.Minutes)} for this vehicle right now (it may be free for longer).",
                _ => "Free right now for this vehicle; the parking doesn't say for how long (ask get_price for a specific length).",
            });
        if (parking.MaxDuration is > 0 && durationMinutes > parking.MaxDuration)
            sb.AppendLine($"Note: the longest allowed stay here is {Text.Duration(parking.MaxDuration.Value)}.");

        logger.LogInformation("get_price [{Id}] {Name}: {Plate}, {Minutes} min → {Total}; free window {Free} min",
            parking.Id, parking.Name, vehicle.LicensePlate, durationMinutes, total, window.Minutes);
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
            var ended = h.EndDate is { } end ? $"ended {Text.LocalTime(end, time)}" : "end time unknown";
            var state = h.State is null or "billed" ? "" : $", {h.State}";
            sb.AppendLine($"{h.SpaceName} - {h.LicensePlate}, {ended}, {Pln(h.TotalPrice)}{state}");
        }
        return sb.ToString().TrimEnd();
    });

    // ── Starting, extending, ending ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "start_session", Title = "Start parking session", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Start parking at a parking (from find_parkings) for one of the account's vehicles. MAY COST MONEY. " +
                 "Free (as APCOA quotes it, free time included) → starts at once. Paid → the first call only returns the " +
                 "price; after the user agrees, call again with the same durationMinutes and confirmedCostPln. Paid stays " +
                 "are charged to the account's default saved card, up to this server's cost limit. Always confirm the " +
                 "parking and plate with the user first.")]
    public Task<string> StartSessionAsync(
        [Description("Parking id (from find_parkings)")] string parkingId,
        [Description(ParkingNameDescription)] string parkingName,
        [Description(LicensePlateDescription)] string licensePlate,
        [Description("Length of the stay in minutes. Omit to use the whole free time when the parking is free, else the parking's usual duration.")] int? durationMinutes = null,
        [Description(ConfirmedCostDescription)] decimal? confirmedCostPln = null,
        CancellationToken ct = default)
        => RunAsync("start_session", parkingName, () =>
            sessions.StartAsync(parkingId, licensePlate, durationMinutes, confirmedCostPln, StartMode.Normal, _options.MaxSessionCostPln, ct), confirmedCostPln);

    [McpServerTool(Name = "start_free_session", Title = "Start free parking session", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Start parking at a parking only if it's free for the vehicle right now (as APCOA quotes it: a free tariff, " +
                 "a shop's free time, any free period). Never charges: if it would cost anything, nothing starts and the " +
                 "price is returned. Without a duration it uses the whole free time.")]
    public Task<string> StartFreeSessionAsync(
        [Description("Parking id (from find_parkings)")] string parkingId,
        [Description(ParkingNameDescription)] string parkingName,
        [Description(LicensePlateDescription)] string licensePlate,
        [Description("Length of the stay in minutes. Omit to use the whole free time.")] int? durationMinutes = null,
        CancellationToken ct = default)
        => RunAsync("start_free_session", parkingName, () =>
            sessions.StartAsync(parkingId, licensePlate, durationMinutes, null, StartMode.FreeOnly, _options.MaxSessionCostPln, ct), null);

    [McpServerTool(Name = "start_expensive_session", Title = "Start parking session above the cost limit", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Does the same as start_session but without this server's cost limit. Use it only when the user explicitly " +
                 "accepts a cost above the limit.")]
    public Task<string> StartExpensiveSessionAsync(
        [Description("Parking id (from find_parkings)")] string parkingId,
        [Description(ParkingNameDescription)] string parkingName,
        [Description(LicensePlateDescription)] string licensePlate,
        [Description("Length of the stay in minutes. Omit to use the parking's usual duration.")] int? durationMinutes = null,
        [Description(ConfirmedCostDescription)] decimal? confirmedCostPln = null,
        CancellationToken ct = default)
        => RunAsync("start_expensive_session", parkingName, () =>
            sessions.StartAsync(parkingId, licensePlate, durationMinutes, confirmedCostPln, StartMode.BypassCap, _options.MaxSessionCostPln, ct), confirmedCostPln);

    [McpServerTool(Name = "start_session_here", Title = "Start parking session here", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Like start_session (MAY COST MONEY), at the parking you're at: the nearest within the radius, measured to " +
                 "the parking's outline when it has one. If two parkings are too close to tell apart by GPS, nothing starts " +
                 "and both are listed: ask the user which, then use start_session.")]
    public Task<string> StartSessionHereAsync(
        [Description("Your current latitude (decimal degrees)")] double latitude,
        [Description("Your current longitude (decimal degrees)")] double longitude,
        [Description(LicensePlateDescription)] string licensePlate,
        [Description("Length of the stay in minutes. Omit to use the whole free time when free, else the parking's usual duration.")] int? durationMinutes = null,
        [Description(ConfirmedCostDescription)] decimal? confirmedCostPln = null,
        [Description("Search radius in metres. Omit to use the server's default.")] double? radiusMeters = null,
        CancellationToken ct = default)
        => HereAsync("start_session_here", latitude, longitude, radiusMeters, (id, name) => RunAsync("start_session_here", name, () =>
            sessions.StartAsync(id, licensePlate, durationMinutes, confirmedCostPln, StartMode.Normal, _options.MaxSessionCostPln, ct), confirmedCostPln), ct);

    [McpServerTool(Name = "start_free_session_here", Title = "Start free parking session here", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Like start_free_session (never charges), at the parking you're at: the nearest within the radius, " +
                 "measured to the parking's outline when it has one. Too close to call → lists the candidates instead.")]
    public Task<string> StartFreeSessionHereAsync(
        [Description("Your current latitude (decimal degrees)")] double latitude,
        [Description("Your current longitude (decimal degrees)")] double longitude,
        [Description(LicensePlateDescription)] string licensePlate,
        [Description("Length of the stay in minutes. Omit to use the whole free time.")] int? durationMinutes = null,
        [Description("Search radius in metres. Omit to use the server's default.")] double? radiusMeters = null,
        CancellationToken ct = default)
        => HereAsync("start_free_session_here", latitude, longitude, radiusMeters, (id, name) => RunAsync("start_free_session_here", name, () =>
            sessions.StartAsync(id, licensePlate, durationMinutes, null, StartMode.FreeOnly, _options.MaxSessionCostPln, ct), null), ct);

    [McpServerTool(Name = "extend_session", Title = "Extend parking session", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Make a running session longer (session id from get_active_sessions). MAY COST MONEY: free extensions " +
                 "happen at once; a paid one first returns the price, then call again with confirmedCostPln after the " +
                 "user agrees. Charged to the default saved card, up to this server's cost limit.")]
    public Task<string> ExtendSessionAsync(
        [Description("The session id (from get_active_sessions)")] string sessionId,
        [Description("The session's parking name as get_active_sessions shows it. Shown in the permission prompt; not sent to the API.")] string parkingName,
        [Description("How many minutes to add.")] int minutes,
        [Description(ConfirmedCostDescription)] decimal? confirmedCostPln = null,
        CancellationToken ct = default)
        => RunAsync("extend_session", parkingName, () =>
            sessions.ExtendAsync(sessionId, minutes, confirmedCostPln, _options.MaxSessionCostPln, ct), confirmedCostPln);

    [McpServerTool(Name = "end_session", Title = "End parking session", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("End a running session (session id from get_active_sessions) and report the final cost. Always confirm " +
                 "with the user first.")]
    public Task<string> EndSessionAsync(
        [Description("The session id (from get_active_sessions)")] string sessionId,
        [Description("The session's parking name as get_active_sessions shows it. Shown in the permission prompt; not sent to the API.")] string parkingName,
        CancellationToken ct = default)
        => RunAsync("end_session", parkingName, () => sessions.EndAsync(sessionId, ct), null);

    /// <summary>One log line and the span outcome per write call; soft failures mark the span Error.</summary>
    private Task<string> RunAsync(string tool, string? parkingName, Func<Task<SessionResult>> body, decimal? confirmedCostPln)
        => ToolErrors.GuardAsync(tool, logger, async () =>
    {
        using var _ = logger.BeginScope("{Tool}", tool);
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ConfirmedCostPln, confirmedCostPln is null ? null : (double)confirmedCostPln.Value);
        var result = await body();
        if (result.Error) ApcoaTelemetry.Fail(result.Outcome, result.Text);
        else ApcoaTelemetry.Outcome(result.Outcome);
        logger.LogInformation("{Tool} {ParkingName}: {Outcome}; session {SessionId}, {Minutes} min, {Cost} PLN (confirmed {Confirmed})",
            tool, parkingName, result.Outcome, result.SessionId ?? "-", result.DurationMinutes?.ToString() ?? "-",
            result.CostPln?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-",
            confirmedCostPln?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-");
        return result.Text;
    });

    /// <summary>
    /// The parking at a position: the nearest within the radius; when two are within <see cref="GpsToleranceMeters"/>
    /// of each other (and the position isn't inside exactly one of them), the candidates are listed instead.
    /// </summary>
    private Task<string> HereAsync(string tool, double latitude, double longitude, double? radiusMeters,
        Func<string, string, Task<string>> start, CancellationToken ct)
        => ToolErrors.GuardAsync(tool, logger, async () =>
    {
        var radius = radiusMeters is > 0 ? radiusMeters.Value : _options.CurrentLocationRadiusMeters;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.RadiusMeters, radius);
        var near = await finder.NearAsync(latitude, longitude, radius, ct);
        if (near.Count == 0)
        {
            ApcoaTelemetry.Fail("no_parking_nearby", "nothing within the radius");
            logger.LogInformation("{Tool}: no parking within {Radius} m of {Lat},{Lon}", tool, radius, latitude, longitude);
            return FormattableString.Invariant($"No APCOA Flow parking within {radius:0} m of that position. Use find_parkings with a larger radius or a name.");
        }

        var first = near[0];
        var insideCount = near.Count(p => p.Inside);
        var clear = near.Count == 1
                    || (first.Inside && insideCount == 1)
                    || (!first.Inside && near[1].DistanceMeters - first.DistanceMeters >= GpsToleranceMeters);
        if (!clear)
        {
            var candidates = near.TakeWhile(p => p.Inside || p.DistanceMeters - first.DistanceMeters < GpsToleranceMeters).ToList();
            ApcoaTelemetry.Fail("ambiguous_location", $"{candidates.Count} parkings within GPS tolerance");
            logger.LogInformation("{Tool}: {Count} parkings too close to call at {Lat},{Lon}", tool, candidates.Count, latitude, longitude);
            return "More than one parking is right here, too close to tell apart by GPS. Nothing was started. Ask the user " +
                   "which one, then use start_session (or start_free_session) with its id:\n" +
                   string.Join("\n", candidates.Select(p => $"[{p.Id}] {p.Name} - {(p.Inside ? "you are inside it" : Text.Meters(p.DistanceMeters!.Value))}"));
        }

        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.DistanceMeters, first.DistanceMeters);
        var note = first.Inside ? "" : $" ({Text.Meters(first.DistanceMeters!.Value)} away)";
        return $"Nearest parking: {first.Name}{note}.\n" + await start(first.Id, first.Name);
    });

    // ── Formatting ─────────────────────────────────────────────────────────────────────────────

    internal string DescribeSession(Session s, DateTimeOffset now)
    {
        var sb = new StringBuilder($"[{s.Id}] {s.Space?.Name ?? "unknown parking"} - {s.Vehicle?.LicensePlate}, from {Text.LocalTime(s.StartDate, time)}");
        if (s.EndDate is { } end)
        {
            var left = end - now;
            sb.Append($" until {Text.LocalTime(end, time)}");
            sb.Append(left > TimeSpan.Zero ? $" ({Text.Duration((int)Math.Ceiling(left.TotalMinutes))} left)" : " (overdue)");
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

    /// <summary>"up to 5 h: 0.00 PLN, 6 h: 3.00 PLN, 7–24 h: 5.00 PLN" — runs of hours at the same price.</summary>
    internal static string Curve(List<TariffTable.Mark> marks)
    {
        var parts = new List<string>();
        for (var i = 0; i < marks.Count;)
        {
            var j = i;
            while (j + 1 < marks.Count && marks[j + 1].Price.Amount == marks[i].Price.Amount) j++;
            string Hours(TariffTable.Mark m) => Text.Duration(m.Minutes);
            var span = i == 0 ? $"up to {Hours(marks[j])}" : i == j ? Hours(marks[i]) : $"{Text.Duration(marks[i].Minutes)}–{Hours(marks[j])}";
            parts.Add($"{span}: {marks[i].Price}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static string Pln(string? apiPrice) => Money.TryParse(apiPrice, out var money) ? money.ToString() : apiPrice ?? "?";
}
