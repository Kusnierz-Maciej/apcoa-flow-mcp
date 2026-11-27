using Microsoft.Extensions.Options;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// Binds the credentials in the <c>ApcoaFlow</c> section — the single account used in stdio mode. (The same
/// section also carries <see cref="ApcoaFlowToolsOptions"/>.)
/// </summary>
public sealed class StdioCredentialsOptions
{
    public const string Section = "ApcoaFlow";

    public string? Email { get; set; }
    public string? Password { get; set; }
}

/// <summary>Stdio mode needs both credentials up front — fail at start, not on the first tool call.</summary>
public sealed class StdioCredentialsOptionsValidator : IValidateOptions<StdioCredentialsOptions>
{
    public ValidateOptionsResult Validate(string? name, StdioCredentialsOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Email))
            failures.Add("ApcoaFlow:Email is required in stdio mode. Set the ApcoaFlow__Email environment variable.");
        if (string.IsNullOrWhiteSpace(options.Password))
            failures.Add("ApcoaFlow:Password is required in stdio mode. Set the ApcoaFlow__Password environment variable.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
