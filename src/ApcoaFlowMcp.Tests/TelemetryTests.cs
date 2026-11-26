using System.Diagnostics;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.ParkingData;
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

    static TelemetryTests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = s => s.Name == TestSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        });
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
