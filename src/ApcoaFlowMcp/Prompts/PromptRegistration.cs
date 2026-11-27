using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Api;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Prompts;

/// <summary>
/// Registers <see cref="ApcoaFlowPrompts"/> and completions for their arguments — <c>plate</c> from the account's
/// vehicles, <c>parking</c> from the parkings of recent sessions — unless <c>Prompts:Enabled</c> is <c>false</c> (then
/// the server advertises no prompts at all).
/// </summary>
internal static class PromptRegistration
{
    public const string EnabledKey = "Prompts:Enabled";
    private const int MaxCompletions = 100;

    public static IMcpServerBuilder WithApcoaFlowPrompts(this IMcpServerBuilder builder, IConfiguration configuration)
    {
        if (!configuration.GetValue(EnabledKey, true))
            return builder;

        return builder
            .WithPrompts<ApcoaFlowPrompts>()
            .WithCompleteHandler(CompleteAsync);
    }

    /// <summary>Completes a prompt's <c>plate</c> (vehicles) or <c>parking</c> (recent sessions) argument.</summary>
    internal static async ValueTask<CompleteResult> CompleteAsync(
        RequestContext<CompleteRequestParams> request, CancellationToken ct)
    {
        if (request.Params?.Ref is not PromptReference || request.Params.Argument.Name is not ("plate" or "parking"))
            return Empty();

        try
        {
            var api = request.Services!.GetRequiredService<IApcoaFlowApi>();
            var candidates = request.Params.Argument.Name == "plate"
                ? (await api.GetVehiclesAsync(ct)).Select(v => v.LicensePlate)
                : (await api.GetSessionsHistoryAsync(page: 1, size: 20, ct: ct)).Select(s => s.SpaceName);
            var result = Complete(candidates, request.Params.Argument.Value);
            ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.ResultsCount, result.Completion.Total);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Completions are a convenience: never fail the request (e.g. credentials not available yet).
            request.Services?.GetService<ILogger<ApcoaFlowPrompts>>()
                ?.LogWarning(ex, "Prompt-argument completion failed; returning no suggestions.");
            return Empty();
        }
    }

    /// <summary>Values starting with the typed text first, then values containing it; case-insensitive, distinct.</summary>
    internal static CompleteResult Complete(IEnumerable<string?> values, string? typed)
    {
        var text = typed?.Trim() ?? "";
        var matches = values
            .OfType<string>()
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(v => v.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(v => v, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new CompleteResult
        {
            Completion = new Completion
            {
                Values = matches.Take(MaxCompletions).ToList(),
                Total = matches.Count,
                HasMore = matches.Count > MaxCompletions,
            },
        };
    }

    private static CompleteResult Empty() => new() { Completion = new Completion { Values = [] } };
}
