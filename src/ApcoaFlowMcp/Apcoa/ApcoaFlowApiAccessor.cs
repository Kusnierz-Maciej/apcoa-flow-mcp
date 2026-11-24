using ApcoaFlowApiClient;

namespace ApcoaFlowMcp.Apcoa;

public class ApcoaFlowApiAccessor(IApcoaFlowApi api)
{
    public IApcoaFlowApi Api => api;
}
