using System.Globalization;
using System.Runtime.CompilerServices;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Forces invariant culture for the whole test assembly so culture-sensitive formatting (e.g. the
/// decimal separator in "5.00 PLN") is deterministic regardless of the machine locale. Without this,
/// a run under a comma-decimal locale (e.g. pl-PL) formats "5.00" as "5,00" and substring assertions
/// on formatted numbers fail. Runs once, before any test, via a module initializer.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    public static void ForceInvariant()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
