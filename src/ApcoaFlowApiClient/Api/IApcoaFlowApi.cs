using ApcoaFlowApiClient.Models;
using Refit;

namespace ApcoaFlowApiClient.Api;

/// <summary>
/// The APCOA Flow API (<see cref="ApcoaFlowEndpoints.ApiBaseUrl"/>), only the calls the MCP needs. Shapes and quirks:
/// docs/API-NOTES.md. Errors surface as Refit <see cref="ApiException"/>; read the body
/// with <see cref="ApcoaFlowApiError.TryRead"/>. Every date in a query is UTC with milliseconds
/// (<see cref="Http.UtcMillisecondsConverter.ToWire"/>).
/// </summary>
public interface IApcoaFlowApi
{
    // ── Account ────────────────────────────────────────────────────────────────────────────────

    [Get("/api/v1/users/profile")]
    Task<UserProfile> GetUserProfileAsync(CancellationToken ct = default);

    [Get("/api/v1/vehicles")]
    Task<List<Vehicle>> GetVehiclesAsync(CancellationToken ct = default);

    /// <param name="spaceId">Only the methods this space accepts; null = all.</param>
    [Get("/api/v1/paymentMethods")]
    Task<List<PaymentMethod>> GetPaymentMethodsAsync([AliasAs("spaceId")] string? spaceId = null, CancellationToken ct = default);

    // ── Spaces ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Text search (name, brand, town, postcode, location code), ranked around the position when given.</summary>
    [Get("/api/v1/search")]
    Task<SearchResponse> SearchAsync([Query] SearchQuery query, CancellationToken ct = default);

    /// <summary>The map pins in a box. Returns <c>[]</c> for an empty view; 500 without parameters.</summary>
    [Get("/api/v1/spaces/getByBoundingBox")]
    Task<List<MapSpace>> GetSpacesByBoundingBoxAsync([Query] BoundingBoxQuery query, CancellationToken ct = default);

    [Get("/api/v1/spaces/{spaceId}")]
    Task<SpaceDetails> GetSpaceAsync(string spaceId, CancellationToken ct = default);

    // ── Prices ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Quote a start. <see cref="PriceQuery.Duration"/> is in seconds.</summary>
    [Get("/api/v1/prices")]
    Task<PriceQuote> GetPriceAsync([Query] PriceQuery query, CancellationToken ct = default);

    /// <summary>Quote an extension of a running session by <paramref name="duration"/> seconds.</summary>
    [Get("/api/v1/prices/updatedPrice")]
    Task<PriceQuote> GetUpdatedPriceAsync([AliasAs("sessionId")] string sessionId, [AliasAs("duration")] int duration, CancellationToken ct = default);

    // ── Sessions ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Running sessions (<c>[]</c> when none).</summary>
    [Get("/api/v1/sessions")]
    Task<List<Session>> GetSessionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Start. Errors: <c>400 ParkingSessionInvalidArgument</c> "The session for the same license plate has already
    /// been started." while one runs for the plate.
    /// </summary>
    [Post("/api/v1/sessions/startParking")]
    Task<StartParkingResponse> StartParkingAsync([Body] StartParkingRequest request,
        [Header("idempotency-key")] string idempotencyKey, CancellationToken ct = default);

    /// <summary>Extend → 200, empty body. Error: <c>400 ParkingSessionInvalidArgument</c> on a stopped session.</summary>
    [Post("/api/v1/sessions/updateParkingDuration")]
    Task UpdateParkingDurationAsync([Body] UpdateParkingDurationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Stop → 204. <c>404 SessionNotFound</c> for an unknown id; an already stopped session is 204 again (silent).
    /// </summary>
    [Post("/api/v1/sessions/stopParking/{sessionId}")]
    Task StopParkingAsync(string sessionId, CancellationToken ct = default);

    // ── History ────────────────────────────────────────────────────────────────────────────────

    [Get("/api/v1/sessionsHistory")]
    Task<List<SessionsHistoryItem>> GetSessionsHistoryAsync([AliasAs("page")] int page = 1, [AliasAs("size")] int size = 8, CancellationToken ct = default);

    [Get("/api/v1/sessionsHistory/{sessionId}")]
    Task<SessionsHistoryDetails> GetSessionsHistoryDetailsAsync(string sessionId, CancellationToken ct = default);
}

/// <summary><c>search</c> query (names as the app sends them).</summary>
public sealed class SearchQuery
{
    [AliasAs("query")] public string? Query { get; set; }
    [AliasAs("latitude")] public double? Latitude { get; set; }
    [AliasAs("longitude")] public double? Longitude { get; set; }
    [AliasAs("size")] public int? Size { get; set; }
    [AliasAs("isParking")] public bool? IsParking { get; set; }
    [AliasAs("isCharging")] public bool? IsCharging { get; set; }
}

/// <summary><c>getByBoundingBox</c> query: north-east and south-west corners plus the map zoom.</summary>
public sealed class BoundingBoxQuery
{
    [AliasAs("NELatitude")] public double NeLatitude { get; set; }
    [AliasAs("NELongitude")] public double NeLongitude { get; set; }
    [AliasAs("SWLatitude")] public double SwLatitude { get; set; }
    [AliasAs("SWLongitude")] public double SwLongitude { get; set; }
    [AliasAs("Zoom")] public int Zoom { get; set; }
}

/// <summary><c>prices</c> query. <see cref="Duration"/> in seconds; <see cref="StartDate"/> in wire format.</summary>
public sealed class PriceQuery
{
    [AliasAs("SpaceId")] public string SpaceId { get; set; } = string.Empty;
    [AliasAs("LicensePlateId")] public string LicensePlateId { get; set; } = string.Empty;
    [AliasAs("Duration")] public int? Duration { get; set; }
    [AliasAs("StartDate")] public string? StartDate { get; set; }
    [AliasAs("EndDate")] public string? EndDate { get; set; }
}
