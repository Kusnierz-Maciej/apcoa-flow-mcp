using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using ApcoaFlow.Sandbox.Dumping;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlow.Sandbox.Scenarios;

/// <summary>
/// WHAT: how spaces are found: <c>search</c> (brand, code, town; with and without a position: ordering, distance
/// unit, result size), <c>getByBoundingBox</c> (small/medium/large boxes: limits), and <c>spaces/{id}</c>. For every
/// space seen it notes whether it has a polygon (<c>geoBorder</c>) and which fields aren't modelled yet. Read-only.
/// WHY: the space search and current-location tools are built on these (nearest = polygon distance, else point).
/// FOUND (2026-11-27): bbox items carry <c>geoBorder</c> (78 of 525 PL spaces), as
/// <c>{coordinates: [{latitude, longitude}…] (closed ring), color: null}</c>.
/// FOUND (2026-11-27): <c>search</c> with a position is sorted nearest first, <c>distance</c> in km; default 20 hits,
/// <c>size=50</c> gave 30 (a cap?). Without a position the order looks arbitrary and the SAME names come with
/// DIFFERENT ids (Jelenia Góra Sobieskiego 58: c78e0ce2… with a position, 9edcfda3… without): check which id
/// quotes. Search hits have no polygon; <c>spaces/{id}</c> neither (only bbox pins). "Carrefour" → 1 space in PL
/// (Warszawa). Location code search ("22941") → exactly that space. bbox ±0.005° at Czarny Dunajec: 8 pins, 7 with
/// a polygon (6–21 pts, closed); ±0.5° zoom 10 around Katowice: 25 pins. <c>spaces/{id}</c> of 4 Biedronkas:
/// <c>maxDuration 43200</c>, <c>defaultLengthOfStayInMinutes 90</c>, <c>parkingOverview</c> is a list
/// <c>[{title, imageUrl}]</c>.
/// </summary>
public sealed class FindSpacesScenario : IScenario
{
    public string Name => "find-spaces";
    public string Description => "Space search: search / getByBoundingBox / spaces/{id}, polygons (geoBorder). Read-only.";

    private static readonly (string Name, double Lat, double Lon) Katowice = ("Katowice", 50.2599, 19.0216);
    private static readonly (string Name, double Lat, double Lon) Ostrow = ("Ostrów Wlkp. (test space)", 51.651796, 17.836953);
    private static readonly (string Name, double Lat, double Lon) CzarnyDunajec = ("Czarny Dunajec (has a polygon)", 49.4410, 19.8555);

    public async Task RunAsync(IServiceProvider services, DumpRun run, CancellationToken ct)
    {
        var api = services.GetRequiredService<IApcoaFlowApi>();
        var seenIds = new List<string>();

        async Task Search(string? text, (string Name, double Lat, double Lon)? at, int? size = null)
        {
            var response = await api.SearchAsync(new SearchQuery
            {
                Query = text, Latitude = at?.Lat, Longitude = at?.Lon, Size = size, IsParking = true, IsCharging = false,
            }, ct);
            run.Log($"search '{text}' at {at?.Name ?? "(no position)"} size={size?.ToString() ?? "default"}: " +
                    $"{response.Spaces.Count} spaces, {response.Places.Count} places, {response.Chargers.Count} chargers");
            foreach (var s in response.Spaces)
            {
                run.Log($"   space {s.Id} '{s.Name}' code={s.TariffCode} distance={s.Distance} " +
                        $"" +
                        $"address='{s.Address?.DisplayValue}' extra=[{Keys(s.Extra)}]");
                seenIds.Add(s.Id);
            }
            foreach (var p in response.Places.Take(3))
                run.Log($"   place '{p.Name}' distance={p.Distance} '{p.Address?.DisplayValue}'");
        }

        await Search("Biedronka", Katowice);
        await Search("Biedronka", null);
        await Search("Biedronka", Katowice, size: 50);
        await Search("Carrefour", Katowice);
        await Search("22941", null);       // Ostrów's location code
        await Search("Ostrów", null);
        await Search("Czarny Dunajec", CzarnyDunajec);

        async Task Box((string Name, double Lat, double Lon) at, double halfDeg, int zoom)
        {
            var spaces = await api.GetSpacesByBoundingBoxAsync(new BoundingBoxQuery
            {
                NeLatitude = at.Lat + halfDeg, NeLongitude = at.Lon + halfDeg,
                SwLatitude = at.Lat - halfDeg, SwLongitude = at.Lon - halfDeg, Zoom = zoom,
            }, ct);
            var withPolygon = spaces.Count(s => s.GeoBorder is { Coordinates.Count: > 0 });
            run.Log($"bbox {at.Name} ±{halfDeg}° zoom {zoom}: {spaces.Count} spaces, {withPolygon} with a polygon");
            foreach (var s in spaces.Take(15))
            {
                run.Log($"   {s.Id} '{s.Name}' {s.ParkingType} polygon={Polygon(s.GeoBorder)} extra=[{Keys(s.Extra)}]");
                seenIds.Add(s.Id);
            }
        }

        await Box(CzarnyDunajec, 0.005, 16);
        await Box(Ostrow, 0.01, 15);
        await Box(Katowice, 0.05, 13);
        await Box(Katowice, 0.5, 10);

        foreach (var id in seenIds.Distinct().Take(4))
        {
            var space = await api.GetSpaceAsync(id, ct);
            run.Log($"space {space.Id} '{space.Name}': code={space.TariffCode} maxDuration={space.MaxDuration?.ToString() ?? "none"} min, " +
                    $"cta={space.CtaButtonType}, autoStart={space.SessionsStartAutomatically}, defaultStay={space.DefaultLengthOfStayInMinutes} min, " +
                    $"price sections={space.Prices.Count}, address={space.Address?.DisplayValue}, extra=[{Keys(space.Extra)}]");
        }
    }

    private static string Polygon(GeoBorder? border) =>
        border is null ? "no" : $"{border.Coordinates.Count} pts, closed={border.Coordinates.FirstOrDefault() == border.Coordinates.LastOrDefault()}";

    private static string Keys(Dictionary<string, System.Text.Json.JsonElement>? extra) =>
        extra is null ? "" : string.Join(", ", extra.Keys);
}
