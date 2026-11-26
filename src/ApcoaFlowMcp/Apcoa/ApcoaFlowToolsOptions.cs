using System.ComponentModel.DataAnnotations;

namespace ApcoaFlowMcp.Apcoa;

/// <summary>Options for the parking MCP tools. Bound from the "ApcoaFlow" configuration section.</summary>
public class ApcoaFlowToolsOptions
{
    /// <summary>
    /// Maximum cost (in PLN) a single session may reach via <c>start_session</c>. A quote above this is
    /// refused and the caller is directed to <c>start_expensive_session</c>. Default 100 zł.
    /// </summary>
    [Range(0d, double.MaxValue, MinimumIsExclusive = true)]
    public decimal MaxSessionCostPln { get; set; } = 100m;

    /// <summary>
    /// Default search radius (metres) used by <c>start_session_current_location</c> when finding the
    /// closest parking. Default 300 m. The tool may override per call.
    /// </summary>
    [Range(0d, double.MaxValue, MinimumIsExclusive = true)]
    public double CurrentLocationRadiusMeters { get; set; } = 300d;
}
