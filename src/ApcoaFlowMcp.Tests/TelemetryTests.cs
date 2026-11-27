using System.Collections.Concurrent;
using System.Diagnostics;
using ApcoaFlowApiClient;
using ApcoaFlowMcp.Tools;
using Microsoft.AspNetCore.Http;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Span tagging: tools write business tags + outcome onto the current span (the MCP tools/call span in production).
/// A recorded parent activity stands in for that span; Activity.Current is async-local, so parallel tests don't
/// interfere.
/// </summary>
public class TelemetryTests
{
    private static readonly ActivitySource TestSource = new("ApcoaFlowMcp.Tests.Telemetry");
    private static readonly ConcurrentBag<Activity> Stopped = [];

    static TelemetryTests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            // The test span stands in for the MCP tools/call span; the ApcoaFlow source records the phase
            // spans — so these tests also prove business tags land on the tool span, not on an open phase.
            ShouldListenTo = s => s.Name == TestSource.Name || s.Name == ApcoaTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => Stopped.Add(a),
        });
    }

    [Test]
    public async Task GetVehicles_TagsCountPlatesAndOutcome()
    {
        var tools = new ApcoaFlowTools(FakeApi.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ApcoaFlowTools>.Instance);

        using var span = TestSource.StartActivity("tools/call get_vehicles")!;
        await tools.GetVehiclesAsync();

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.ResultsCount)).IsEqualTo(1);
        await Assert.That((string[])span.GetTagItem(ApcoaTelemetry.Tags.LicensePlates)!).IsEquivalentTo(new[] { FakeApi.LicensePlate });
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("ok");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task NoCurrentSpan_HelpersAreNoOps()
    {
        // Telemetry off: no current span — helpers must not throw.
        Activity.Current = null;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.LicensePlates, "X");
        ApcoaTelemetry.Fail("x", "y");
        ApcoaTelemetry.Event("e", ("k", 1));
        await Assert.That(Activity.Current).IsNull();
    }

    [Test]
    [Arguments("/.well-known/oauth-authorization-server", true)]
    [Arguments("/favicon.ico", true)]
    [Arguments("/apple-touch-icon.png", true)]
    [Arguments("/icon-192.png", true)]
    [Arguments("/mcp", false)]
    [Arguments("/token", false)]
    [Arguments("/authorize", false)]
    public async Task Noise_Filter(string path, bool isNoise) =>
        await Assert.That(TelemetryNoise.IsNoise(new PathString(path))).IsEqualTo(isNoise);
}
