using System.Net;
using ApcoaFlowApiClient;
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
/// Business refusals (cost cap, unknown plate, already active…) are not exceptions — the session service
/// returns them as normal text and they never reach this class.
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

        for (var e = ex; e is not null; e = e.InnerException)
            if (e is ApcoaFlowCredentialsRejectedException)
                return new("credentials_rejected", CredentialsRejected);

        for (var e = ex; e is not null; e = e.InnerException)
            if (e is ApiException api)
                return FromStatus(api.StatusCode);

        for (var e = ex; e is not null; e = e.InnerException)
            if (e is HttpRequestException)
                return new("unavailable",
                    "APCOA could not be reached or failed to sign in right now. Try again in a moment.");

        return null;
    }

    private static Mapped FromStatus(HttpStatusCode status)
    {
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.NotFound => new("not_found",
                $"APCOA couldn't find that parking or session (HTTP {code}). Call find_parking or " +
                "get_active_sessions again for current ids."),
            // APCOA answers 403 when it locks the account out after too many requests.
            HttpStatusCode.Forbidden => new("forbidden",
                $"APCOA refused the request (HTTP {code}). The account may be temporarily locked after too many " +
                "requests — wait a few minutes before trying again."),
            // 401/419 normally trigger one re-login in the auth handler; reaching here means it didn't help.
            HttpStatusCode.Unauthorized or (HttpStatusCode)419 => new("credentials_rejected", CredentialsRejected),
            HttpStatusCode.TooManyRequests => new("rate_limited",
                $"APCOA is rate-limiting requests (HTTP {code}). Wait a moment and try again."),
            _ when code >= 500 => new("unavailable",
                $"APCOA is temporarily unavailable (HTTP {code}). Try again in a moment."),
            _ => new("rejected_request",
                $"APCOA rejected the request (HTTP {code}). Check the parking id, plate and session id " +
                "(find_parking / get_registered_cars / get_active_sessions) and try again."),
        };
    }
}
