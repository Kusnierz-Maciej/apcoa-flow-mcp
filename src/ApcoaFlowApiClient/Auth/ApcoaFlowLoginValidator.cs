namespace ApcoaFlowApiClient.Auth;

/// <summary>
/// Checks an e-mail + password with one password grant, for the MCP's OAuth sign-in page. Tells a wrong password
/// (<see cref="TokenGrantOutcomeKind.Rejected"/>) from an API problem (<see cref="TokenGrantOutcomeKind.ApiError"/>).
/// </summary>
public interface IApcoaFlowLoginValidator
{
    Task<TokenGrantOutcome> ValidateAsync(ApcoaFlowCredentials credentials, CancellationToken ct = default);
}

public sealed class ApcoaFlowLoginValidator(TokenClient tokenClient) : IApcoaFlowLoginValidator
{
    public Task<TokenGrantOutcome> ValidateAsync(ApcoaFlowCredentials credentials, CancellationToken ct = default) =>
        tokenClient.PasswordGrantAsync(credentials, ct);
}
