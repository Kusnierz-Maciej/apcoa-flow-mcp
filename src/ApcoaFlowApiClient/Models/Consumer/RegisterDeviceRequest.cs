namespace ApcoaFlowApiClient.Models.Consumer;

public class RegisterDeviceRequest
{
    public string OperatingSystem { get; set; } = string.Empty;
    public string DeviceMake { get; set; } = string.Empty;
    public string DeviceModel { get; set; } = string.Empty;
    public int PushEnabled { get; set; }
    public string? PushToken { get; set; }
}
