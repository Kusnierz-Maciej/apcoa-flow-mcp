using ApcoaFlowApiClient.Models;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>
/// License plates as users type them ("abc 123 45") vs as the API stores them ("ABC12345", no spaces): every plate
/// argument is matched with all whitespace removed, case-insensitively.
/// </summary>
public static class Plates
{
    public static string Normalize(string? plate) =>
        new string((plate ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    /// <summary>The account vehicle with this plate, or null.</summary>
    public static Vehicle? Find(IEnumerable<Vehicle> vehicles, string? plate)
    {
        var wanted = Normalize(plate);
        return wanted.Length == 0 ? null : vehicles.FirstOrDefault(v => Normalize(v.LicensePlate) == wanted);
    }
}
