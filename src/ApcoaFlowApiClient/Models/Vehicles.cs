namespace ApcoaFlowApiClient.Models;

/// <summary>
/// <c>GET /api/v1/vehicles</c> item. <see cref="Id"/> is the <c>licensePlateId</c> that <c>prices</c> and
/// <c>sessions/startParking</c> take. Plates come without spaces (<c>"STA0521F"</c>).
/// </summary>
public sealed class Vehicle
{
    public string Id { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public string LicensePlate { get; set; } = string.Empty;

    /// <summary>The user's own label for the car ("Jeep", "Test Car").</summary>
    public string? Description { get; set; }

    public bool IsDefault { get; set; }
    public bool IsAnprAutoPaymentEnabled { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public string? CompanyName { get; set; }
}
