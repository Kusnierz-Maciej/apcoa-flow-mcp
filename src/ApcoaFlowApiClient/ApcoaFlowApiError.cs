using System.Net.Http.Json;
using System.Text.Json;
using Refit;

namespace ApcoaFlowApiClient;

/// <summary>
/// The API's business error body: <c>{"code": "...", "message": "..."}</c>. Validation errors are ASP.NET
/// ProblemDetails instead (<c>{type, title, status, errors}</c>); <see cref="TryRead"/> reads both into this shape.
/// </summary>
public sealed record ApcoaFlowApiError(string? Code, string? Message)
{
    /// <summary>Codes seen on the wire (2026-11-27).</summary>
    public static class Codes
    {
        /// <summary><c>403</c>: no or invalid bearer token.</summary>
        public const string Unauthenticated = "Unauthenticated";

        /// <summary>
        /// <c>400</c>: e.g. "The session for the same license plate has already been started." or
        /// "Only planned or active sessions can be extended."
        /// </summary>
        public const string ParkingSessionInvalidArgument = "ParkingSessionInvalidArgument";

        /// <summary><c>404</c>: stop of an unknown session id; <c>sessionsHistory/{id}</c> of an active postPay one.</summary>
        public const string SessionNotFound = "SessionNotFound";

        /// <summary><c>404</c>: generic, e.g. <c>users/features/{id}</c> with an unknown feature id.</summary>
        public const string NotFound = "NotFound";
    }

    /// <summary>The error body of a Refit <see cref="ApiException"/>, or null when it isn't one of ours.</summary>
    public static ApcoaFlowApiError? TryRead(ApiException ex) => TryParse(ex.Content);

    internal static async Task<ApcoaFlowApiError?> TryReadAsync(HttpContent content, CancellationToken ct) =>
        TryParse(await content.ReadAsStringAsync(ct));

    internal static ApcoaFlowApiError? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            string? Str(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Str("code") is { } code)
                return new ApcoaFlowApiError(code, Str("message"));
            if (Str("title") is { } title) // ProblemDetails
                return new ApcoaFlowApiError("ProblemDetails", root.TryGetProperty("errors", out var errors)
                    ? $"{title} {errors.GetRawText()}" : title);
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
