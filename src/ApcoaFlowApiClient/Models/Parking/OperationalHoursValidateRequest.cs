namespace ApcoaFlowApiClient.Models.Parking;

public class OperationalHoursValidateRequest
{
    public int ParkingId { get; set; }
    public string CheckInTime { get; set; } = string.Empty;
    /// <summary>Optional — omitted on the first "is it open now" check, included for duration validation.</summary>
    public string? CheckOutTime { get; set; }
}
