namespace ApcoaFlowApiClient.Models.Parking;

public class ParkingDetail
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? TotalSpaces { get; set; }
    public int? AvailableSpaces { get; set; }
    public int CompanyId { get; set; }
    public List<string> Images { get; set; } = [];
}
