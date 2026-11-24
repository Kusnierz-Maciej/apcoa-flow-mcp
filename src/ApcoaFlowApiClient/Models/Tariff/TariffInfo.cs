namespace ApcoaFlowApiClient.Models.Tariff;

public class TariffInfo
{
    public int Id { get; set; }
    public int ParkingLotId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? CurrencyCode { get; set; }
    public string? ProductType { get; set; }
}
