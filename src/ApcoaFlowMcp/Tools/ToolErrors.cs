using System.Net;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Auth;
using ModelContextProtocol;
using Refit;

namespace ApcoaFlowMcp.Tools;

/// <summary>
/// Turns APCOA failures into plain-language tool errors. The MCP SDK reports any other exception as an
/// opaque "An error occurred invoking '&lt;tool&gt;'", so each tool body runs through <see cref="GuardAsync"/>:
/// known failures are logged, marked on the span as an error, and rethrown as <see cref="McpException"/>,
/// whose message the SDK passes to the model as the tool result.
/// This is also how rejected credentials surface — the SDK catches tool exceptions inside an already-started
/// 200 SSE response, so an HTTP 401 can't be sent at that point (no middleware ever sees the exception).
/// Business refusals (cost cap, unknown plate, already active…) are not exceptions — the tools return them as
/// normal text and they never reach this class.
/// </summary>
internal static class ToolErrors
{
    internal sealed record Mapped(string Outcome, string Message);

    public const string CredentialsRejected =
        "APCOA rejected the account's sign-in (the password may have changed). Reconnect this APCOA connector " +
        "in the AI client's settings (disconnect and connect it again), or update the credentials it uses, " +
        "then retry.";

    public static async Task<string> GuardAsync(string tool, ILogger logger, Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            logger.LogWarning(ex, "{Tool} failed: {Outcome}", tool, mapped.Outcome);
            ApcoaTelemetry.Fail(mapped.Outcome, mapped.Message);
            throw new McpException(mapped.Message);
        }
    }

    /// <summary>Maps an exception (or any inner exception) to an outcome + message; null leaves it unhandled.</summary>
    internal static Mapped? Map(Exception ex)
    {
        if (ex is OperationCanceledException)
            return null;

        // Thrown inside the handler chain, so Refit wraps them (ApiRequestException): look through the inner chain.
        if (ApcoaFlowAuthErrors.Find<ApcoaFlowCredentialsRejectedException>(ex) is not null)
            return new("credentials_rejected", CredentialsRejected);
        if (ApcoaFlowAuthErrors.Find<ApcoaFlowAuthUnavailableException>(ex) is not null)
            return new("unavailable", "APCOA sign-in is not available right now. Try again in a moment.");

        if (ApcoaFlowAuthErrors.Find<ApiException>(ex) is { } api)
            return FromBody(api) ?? FromStatus(api.StatusCode);

        // A price this server can't read (Money.Parse): say so instead of the SDK's empty error.
        if (ApcoaFlowAuthErrors.Find<FormatException>(ex) is not null)
            return new("unreadable_price", "APCOA returned a price in a format this server can't read, so nothing was " +
                "started or changed. Try again later; if it keeps happening the server needs an update.");

        if (ApcoaFlowAuthErrors.Find<HttpRequestException>(ex) is not null)
            return new("unavailable", "APCOA could not be reached right now. Try again in a moment.");

        return null;
    }

    /// <summary>The API's error code in the body takes precedence over the bare status.</summary>
    private static Mapped? FromBody(ApiException api) => ApcoaFlowApiError.TryRead(api) switch
    {
        { Code: ApcoaFlowApiError.Codes.SessionNotFound } => new("session_not_found",
            "APCOA has no parking session with that id — it may have ended already, or the id is wrong. " +
            "Check the running sessions for the current id."),
        { Code: ApcoaFlowApiError.Codes.ParkingSessionInvalidArgument, Message: { } message } =>
            new("session_refused", $"APCOA refused the session change: {message}"),
        { Code: ApcoaFlowApiError.Codes.Unauthenticated } => new("credentials_rejected", CredentialsRejected),
        _ => null,
    };

    private static Mapped FromStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.NotFound => new("not_found",
                $"APCOA couldn't find that parking or session (HTTP {code}). Search again for current ids."),
            HttpStatusCode.Unauthorized => new("credentials_rejected", CredentialsRejected),
            HttpStatusCode.Forbidden => new("forbidden",
                $"APCOA refused the request (HTTP {code}). Wait a few minutes before trying again."),
            HttpStatusCode.TooManyRequests => new("rate_limited",
                $"APCOA is rate-limiting requests (HTTP {code}). Wait a moment and try again."),
            _ when code >= 500 => new("unavailable",
                $"APCOA is temporarily unavailable (HTTP {code}). Try again in a moment."),
            _ => new("rejected_request",
                $"APCOA rejected the request (HTTP {code}). Check the parking, vehicle and session ids and try again."),
        };
    }
}
