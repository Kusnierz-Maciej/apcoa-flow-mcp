namespace ApcoaFlowApiClient.Models.Session;

/// <summary>
/// Response of <c>POST /consumer/parkingSession</c>. It is a payment-transaction envelope whose
/// top-level <see cref="Id"/> IS the created parking session's id (used for stop/extend). The
/// <see cref="ParkingSessionStatus"/> is ACTIVE on a successful start. (<see cref="ParkingSession"/>
/// is typically null — the session id lives at the top level.)
/// </summary>
public class StartSessionResponse
{
    /// <summary>The created parking session's id (this is the parkingSessionId for stop/extend).</summary>
    public long Id { get; set; }

    public string? TransactionStatus { get; set; }
    public decimal? TransactionAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public string? ParkingSessionStatus { get; set; }
    public string? PaymentErrorCode { get; set; }
    public string? GatewayResponseMessage { get; set; }

    /// <summary>Usually null; the session id is <see cref="Id"/>.</summary>
    public ParkingSessionV2? ParkingSession { get; set; }

    /// <summary>The started session's id.</summary>
    public long SessionId => Id;

    public bool IsActive =>
        string.Equals(ParkingSessionStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);
}
