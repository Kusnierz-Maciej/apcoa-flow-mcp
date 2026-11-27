using System.Collections.Concurrent;
using System.Diagnostics;
using ApcoaFlowApiClient;
using ApcoaFlowMcp.Tools;
using NSubstitute;
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
        var tools = FakeApi.Tools(FakeApi.Create());

        using var span = TestSource.StartActivity("tools/call get_vehicles")!;
        await tools.GetVehiclesAsync();

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.ResultsCount)).IsEqualTo(1);
        await Assert.That((string[])span.GetTagItem(ApcoaTelemetry.Tags.LicensePlates)!).IsEquivalentTo(new[] { FakeApi.LicensePlate });
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("ok");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    /// <summary>Names of the child spans recorded directly under <paramref name="parent"/>, in start order.</summary>
    private static List<string> Phases(Activity parent) =>
        Stopped.Where(a => a.ParentSpanId == parent.SpanId && a.TraceId == parent.TraceId)
            .OrderBy(a => a.StartTimeUtc).Select(a => a.OperationName).ToList();

    [Test]
    public async Task FreeStart_records_its_phases_and_puts_the_business_tags_on_the_tool_span()
    {
        var api = FakeApi.ForStarts(_ => 0m);
        using var span = TestSource.StartActivity("tools/call start_free_session")!;

        await FakeApi.Tools(api).StartFreeSessionAsync(FakeApi.ParkingId, FakeApi.ParkingName, "ABC12345", 30);
        span.Stop();

        await Assert.That(Phases(span)).IsEquivalentTo(new[]
        {
            "apcoa.vehicles", "apcoa.running_sessions", "apcoa.parking", "apcoa.payment_method", "apcoa.free_window",
            "apcoa.quote", "apcoa.start", "apcoa.readback",
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("started");
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.ParkingId)).IsEqualTo(FakeApi.ParkingId);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.LicensePlate)).IsEqualTo("ABC12345");
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.SessionId)).IsEqualTo("new-session");
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.CostPln)).IsEqualTo(0.0);
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task PaidFirstCall_is_a_quote_not_an_error()
    {
        using var span = TestSource.StartActivity("tools/call start_session")!;

        await FakeApi.Tools(FakeApi.ForStarts(_ => 5m)).StartSessionAsync(FakeApi.ParkingId, FakeApi.ParkingName, "ABC12345", 60);

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("quote");
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.CostPln)).IsEqualTo(5.0);
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task A_refusal_marks_the_tool_span_as_an_error()
    {
        using var span = TestSource.StartActivity("tools/call start_session")!;

        await FakeApi.Tools(FakeApi.ForStarts(_ => 150m)).StartSessionAsync(FakeApi.ParkingId, FakeApi.ParkingName, "ABC12345", 60, confirmedCostPln: 150m);

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("cost_cap_exceeded");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task End_records_stop_then_bill()
    {
        var api = FakeApi.ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([FakeApi.StartedSession("s1")]);
        api.GetSessionsHistoryDetailsAsync("s1", Arg.Any<CancellationToken>())
            .Returns(new ApcoaFlowApiClient.Models.SessionsHistoryDetails { Id = "s1", TotalPrice = "PLN 0.00" });
        using var span = TestSource.StartActivity("tools/call end_session")!;

        await FakeApi.Tools(api).EndSessionAsync("s1", FakeApi.ParkingName);
        span.Stop();

        await Assert.That(Phases(span)).IsEquivalentTo(new[] { "apcoa.running_sessions", "apcoa.stop", "apcoa.bill" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("stopped");
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
