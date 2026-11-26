namespace ApcoaFlowApiClient;

/// <summary>
/// Classifies the outcome of an APCOA login attempt so callers can tell a genuine bad-credential
/// rejection apart from a transient API problem. A 419 (APCOA "invalid/expired token") or any 5xx/
/// network fault is <see cref="ApiError"/> — NOT <see cref="BadCredentials"/>; only an explicit
/// credential rejection (a failed login response) is <see cref="BadCredentials"/>.
/// </summary>
public enum LoginOutcomeKind
{
    Ok,
    BadCredentials,
    ApiError,
}

/// <summary>The result of <see cref="IApcoaFlowLoginValidator.ValidateAsync"/>.</summary>
/// <param name="Kind">The classification.</param>
/// <param name="Token">The bearer token when <see cref="LoginOutcomeKind.Ok"/>; otherwise null.</param>
/// <param name="Detail">Optional non-sensitive detail for logging (never contains credentials).</param>
public readonly record struct LoginOutcome(LoginOutcomeKind Kind, string? Token, string? Detail)
{
    public static LoginOutcome Success(string token) => new(LoginOutcomeKind.Ok, token, null);
    public static LoginOutcome Bad(string? detail = null) => new(LoginOutcomeKind.BadCredentials, null, detail);
    public static LoginOutcome Api(string? detail = null) => new(LoginOutcomeKind.ApiError, null, detail);
}

/// <summary>
/// Performs an APCOA login purely to validate credentials (used by the OAuth <c>/authorize</c> step),
/// returning a tri-state <see cref="LoginOutcome"/>. This is the single source of truth for
/// "what counts as a bad-credential login" — shared with <see cref="ApcoaFlowAuthHandler"/>.
/// </summary>
public interface IApcoaFlowLoginValidator
{
    Task<LoginOutcome> ValidateAsync(string email, string password, CancellationToken ct = default);
}
