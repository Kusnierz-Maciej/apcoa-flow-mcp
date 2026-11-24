namespace ApcoaFlowApiClient;

/// <summary>Supplies the APCOA Flow credentials for the current request scope.</summary>
public interface IApcoaFlowCredentialProvider
{
    ApcoaFlowClientOptions GetCredentials();
}
