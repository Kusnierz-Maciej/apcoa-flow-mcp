using ApcoaFlowApiClient.Models.Auth;
using ApcoaFlowApiClient.Models.Parking;
using Refit;

namespace ApcoaFlowApiClient;

public interface IApcoaFlowApi
{
    /// <summary>Authenticates with email/password. Returns a bearer token.</summary>
    [Post("/auth/login")] // placeholder — update from API dumps
    Task<LoginResponse> LoginAsync([Body] LoginRequest request, CancellationToken ct = default);

    /// <summary>Returns all parking zones/locations available to the user.</summary>
    [Get("/parking/zones")] // placeholder — update from API dumps
    Task<List<ParkingZone>> GetParkingZonesAsync(CancellationToken ct = default);

    /// <summary>Starts a parking session at the given zone.</summary>
    [Post("/parking/sessions")] // placeholder — update from API dumps
    Task<ParkingSession> StartSessionAsync([Body] StartSessionRequest request, CancellationToken ct = default);

    /// <summary>Returns the currently active parking session, or null if none is active.</summary>
    [Get("/parking/sessions/active")] // placeholder — update from API dumps
    Task<ParkingSession?> GetActiveSessionAsync(CancellationToken ct = default);

    /// <summary>Ends the active parking session.</summary>
    [Delete("/parking/sessions/{sessionId}")] // placeholder — update from API dumps
    Task EndSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Returns parking session history.</summary>
    [Get("/parking/sessions")] // placeholder — update from API dumps
    Task<List<ParkingSession>> GetSessionHistoryAsync(CancellationToken ct = default);
}
