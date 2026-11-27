namespace ApcoaFlowApiClient.Models;

/// <summary>
/// <c>GET /api/v1/users/profile</c>. <c>email</c> is <c>null</c> on an account without one, else
/// <c>{address, status}</c> (<c>"approved"</c> once verified). <c>termsToAccept</c> can list a required general-terms
/// entry although the token said <c>termsNeedToBeAccepted: false</c> (new account, 2026-11-27).
/// </summary>
public sealed class UserProfile
{
    public string UserId { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? PhoneNumber { get; set; }
    public UserEmail? Email { get; set; }
    public List<string> LoginMethods { get; set; } = [];
    public string? PreferredLanguage { get; set; }
    public List<TermsToAccept> TermsToAccept { get; set; } = [];
}

public sealed class UserEmail
{
    public string? Address { get; set; }

    /// <summary><c>approved</c> when verified; other values not seen yet.</summary>
    public string? Status { get; set; }
}

public sealed class TermsToAccept
{
    public string TermsId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? VersionId { get; set; }
    public bool Required { get; set; }

    /// <summary><c>GeneralTerms</c>, or null for the optional marketing opt-in.</summary>
    public string? Category { get; set; }
}
