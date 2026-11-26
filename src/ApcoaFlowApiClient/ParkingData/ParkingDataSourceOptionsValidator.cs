using Microsoft.Extensions.Options;

namespace ApcoaFlowApiClient.ParkingData;

/// <summary>
/// Cross-field rules for <see cref="ParkingDataSourceOptions"/> (single-field bounds live on the
/// options as DataAnnotations). Runs at host start via <c>ValidateOnStart</c>.
/// </summary>
public sealed class ParkingDataSourceOptionsValidator : IValidateOptions<ParkingDataSourceOptions>
{
    public ValidateOptionsResult Validate(string? name, ParkingDataSourceOptions options)
    {
        var failures = new List<string>();

        RequirePositive(failures, nameof(options.SoftRefreshAge), options.SoftRefreshAge);
        RequirePositive(failures, nameof(options.HardRefreshAge), options.HardRefreshAge);
        RequirePositive(failures, nameof(options.BackgroundRefreshInterval), options.BackgroundRefreshInterval);

        if (options.SoftRefreshAge > options.HardRefreshAge)
            failures.Add(
                $"ParkingData:SoftRefreshAge ({options.SoftRefreshAge}) must be <= " +
                $"ParkingData:HardRefreshAge ({options.HardRefreshAge}).");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(List<string> failures, string key, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            failures.Add($"ParkingData:{key} must be a positive duration (got {value}).");
    }
}
