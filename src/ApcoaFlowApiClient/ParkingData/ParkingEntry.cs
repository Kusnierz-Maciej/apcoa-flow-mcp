namespace ApcoaFlowApiClient.ParkingData;

/// <summary>Slim parking record extracted from the S3 offline dataset.</summary>
public class ParkingEntry
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? ZipCode { get; set; }
    public string? CountryCode { get; set; }
    public string? CurrencyCode { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool IsParkingEnabled { get; set; }
    public bool IsEvOnly { get; set; }
    public bool IsPassAvailable { get; set; }
    public bool IsPermitAvailable { get; set; }
    public bool IsPreBookingAvailable { get; set; }
    public List<string> AccessTypes { get; set; } = [];
    public List<string> Services { get; set; } = [];
    public List<string> ParkingLotTypes { get; set; } = [];
    public List<OperationalHoursEntry> OperationalHours { get; set; } = [];
}

public class OperationalHoursEntry
{
    /// <summary>Day of week: 1=Monday … 7=Sunday.</summary>
    public int Day { get; set; }

    /// <summary>Format: "HH:mm:ss.fff". "00:00:00.000" on both open and close means 24h.</summary>
    public string OpenTime { get; set; } = string.Empty;
    public string CloseTime { get; set; } = string.Empty;
}
