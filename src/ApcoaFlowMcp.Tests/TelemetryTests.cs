using System.Collections.Concurrent;
using System.Diagnostics;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Tools;
using ApcoaFlowMcp.Apcoa;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Span tagging: the session service writes business tags + outcome onto the current span (the MCP
/// tools/call span in production) and marks soft failures as Error. A recorded parent activity stands
/// in for that span; Activity.Current is async-local, so parallel tests don't interfere.
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

    /// <summary>Names of the child spans recorded directly under <paramref name="parent"/>, in start order.</summary>
    private static List<string> Phases(Activity parent) =>
        Stopped.Where(a => a.ParentSpanId == parent.SpanId && a.TraceId == parent.TraceId)
            .OrderBy(a => a.StartTimeUtc).Select(a => a.OperationName).ToList();

    [Test]
    public async Task FreeStart_RecordsPhaseSpans_InOrder()
    {
        var span = await RunStart(Fake.Api(quote: Fake.Quote(0m)));

        await Assert.That(Phases(span)).IsEquivalentTo(new[]
        {
            "apcoa.resolve_lot", "apcoa.resolve_consumer", "apcoa.offers", "apcoa.cancel_note", "apcoa.quote", "apcoa.start",
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_RecordsStopThenRereadPhases()
    {
        var api = Fake.Api();
        api.GetParkingSessionsAsync(Arg.Any<ApcoaFlowApiClient.Models.Session.ParkingSessionV2Query>(), Arg.Any<CancellationToken>())
            .Returns([]);
        using var span = TestSource.StartActivity("tools/call end_session")!;
        await new ApcoaFlowSessionService(api, Substitute.For<IApcoaParkingDataSource>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ApcoaFlowSessionService>.Instance)
            .StopAsync(123, CancellationToken.None);
        span.Stop();

        await Assert.That(Phases(span)).IsEquivalentTo(new[] { "apcoa.stop", "apcoa.reread_completed" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("stopped");
    }

    [Test]
    public async Task FindParking_TagsCappedParkingIds_AndCount()
    {
        var data = Substitute.For<IApcoaParkingDataSource>();
        var many = Enumerable.Range(1, 30)
            .Select(i => (new ParkingEntry { Id = i, DisplayName = $"P{i}" }, i / 1000.0))
            .ToList();
        data.FindNearbyAsync(Arg.Any<double>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(many);
        var tools = new ApcoaFlowTools(
            new ApcoaFlowSessionService(Fake.Api(), data, Microsoft.Extensions.Logging.Abstractions.NullLogger<ApcoaFlowSessionService>.Instance),
            data, Microsoft.Extensions.Options.Options.Create(new ApcoaFlowToolsOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ApcoaFlowTools>.Instance);

        using var span = TestSource.StartActivity("tools/call find_parking")!;
        await tools.FindParkingAsync(50.4, 18.8, 2000, "Carrefour");

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.ResultsCount)).IsEqualTo(30);
        await Assert.That(((string[])span.GetTagItem(ApcoaTelemetry.Tags.ParkingIds)!).Length).IsEqualTo(ApcoaTelemetry.MaxListedValues);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.NameFilter)).IsEqualTo("Carrefour");
    }

    private static async Task<Activity> RunStart(
        IApcoaFlowApi api, ApcoaFlowSessionService.StartMode mode = ApcoaFlowSessionService.StartMode.Normal,
        decimal? confirmed = null)
    {
        using var span = TestSource.StartActivity("tools/call start_session")!;
        var service = new ApcoaFlowSessionService(api, Substitute.For<IApcoaParkingDataSource>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ApcoaFlowSessionService>.Instance);
        await service.StartAsync(Fake.ParkingId, Fake.Plate, null, mode, confirmed, 20m, false, CancellationToken.None);
        return span;
    }

    [Test]
    public async Task FreeStart_TagsBusinessAttributes_AndOutcomeStarted()
    {
        var span = await RunStart(Fake.Api(quote: Fake.Quote(0m)));

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("started");
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Plate)).IsEqualTo(Fake.Plate);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.ParkingId)).IsEqualTo(Fake.ParkingId);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.LotId)).IsEqualTo(Fake.LotId);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.TariffId)).IsEqualTo(Fake.TariffId);
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.SessionId)).IsNotNull();
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task PaidFirstCall_IsQuote_NotError()
    {
        var span = await RunStart(Fake.Api(quote: Fake.Quote(5m)));

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("quote");
        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.CostPln)).IsEqualTo(5.0);
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task OverCap_IsError()
    {
        var span = await RunStart(Fake.Api(quote: Fake.Quote(50m)), confirmed: 50m);

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("cost_cap_exceeded");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task UnknownPlate_IsError()
    {
        var span = await RunStart(Fake.Api(consumer: Fake.Consumer(plate: "XYZ 999")));

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("plate_not_registered");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task OnlyFree_AtPaidParking_IsNotFree_NotError()
    {
        var span = await RunStart(Fake.Api(quote: Fake.Quote(3m)), ApcoaFlowSessionService.StartMode.OnlyFree);

        await Assert.That(span.GetTagItem(ApcoaTelemetry.Tags.Outcome)).IsEqualTo("not_free");
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Unset);
    }

    [Test]
    public async Task NoCurrentSpan_HelpersAreNoOps()
    {
        // Telemetry off: no current span — helpers must not throw.
        Activity.Current = null;
        ApcoaTelemetry.Tag(ApcoaTelemetry.Tags.Plate, "X");
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
