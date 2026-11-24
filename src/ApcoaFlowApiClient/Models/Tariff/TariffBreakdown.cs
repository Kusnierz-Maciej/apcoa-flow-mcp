namespace ApcoaFlowApiClient.Models.Tariff;

public class TariffBreakdown
{
    public int TariffId { get; set; }
    public int ParkingLotId { get; set; }
    public int Duration { get; set; }
    public decimal TotalAmount { get; set; }
    public string? CurrencyCode { get; set; }
    public decimal? VatAmount { get; set; }
    public decimal? NetAmount { get; set; }
    public List<TariffBreakdownItem> Items { get; set; } = [];
}

public class TariffBreakdownItem
{
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public int? Minutes { get; set; }
}
