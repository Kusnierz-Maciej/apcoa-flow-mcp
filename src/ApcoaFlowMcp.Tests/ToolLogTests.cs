using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Every tool writes one structured line per call with ids, counts and the outcome (framework §7), and every
/// start path reports its outcome on the result — so logs say what happened even with telemetry off.
/// </summary>
public class ToolLogTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private static (ApcoaFlowTools Tools, CapturingLogger<ApcoaFlowTools> Log) Tools(ApcoaFlowApiClient.IApcoaFlowApi api)
    {
        var log = new CapturingLogger<ApcoaFlowTools>();
        var data = Substitute.For<IApcoaParkingDataSource>();
        var tools = new ApcoaFlowTools(
            new ApcoaFlowSessionService(api, data, NullLogger<ApcoaFlowSessionService>.Instance),
            data, Options.Create(new ApcoaFlowToolsOptions { MaxSessionCostPln = 20m }), log);
        return (tools, log);
    }

    [Test]
    public async Task ReadTool_LogsOneLine_WithTheResultCount()
    {
        var (tools, log) = Tools(Fake.Api());

        await tools.GetRegisteredCarsAsync();

        await Assert.That(log.Lines).IsEquivalentTo(new[] { $"get_registered_cars: 1 car(s) [{Fake.Plate}]" });
    }

    [Test]
    public async Task Start_LogsOneLine_WithParkingOutcomeCostAndRequestedName()
    {
        var (tools, log) = Tools(Fake.Api(quote: Fake.Quote(0m)));

        await tools.StartFreeSessionAsync(Fake.ParkingId, "Test Parking (as named)", Fake.Plate);

        var line = log.Lines.Single();
        await Assert.That(line).StartsWith($"start_free_session [{Fake.ParkingId}] {Fake.ParkingName}: started;");
        await Assert.That(line).Contains($"plate {Fake.Plate}");
        await Assert.That(line).Contains("requested as Test Parking (as named)");
    }

    [Test]
    [Arguments(0, null, false, "started")]
    [Arguments(5, null, false, "quote")]
    [Arguments(5, 4.0, false, "cost_mismatch")]
    [Arguments(50, 50.0, false, "cost_cap_exceeded")]
    [Arguments(5, null, true, "not_free")]
    public async Task StartResult_CarriesTheOutcome(int quotePln, double? confirmed, bool onlyFree, string outcome)
    {
        var service = new ApcoaFlowSessionService(Fake.Api(quote: Fake.Quote(quotePln)),
            Substitute.For<IApcoaParkingDataSource>(), NullLogger<ApcoaFlowSessionService>.Instance);

        var result = await service.StartAsync(Fake.ParkingId, Fake.Plate, null,
            onlyFree ? ApcoaFlowSessionService.StartMode.OnlyFree : ApcoaFlowSessionService.StartMode.Normal,
            (decimal?)confirmed, 20m, bypassCap: false, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(outcome);
    }

    [Test]
    public async Task StartResult_UnknownPlate_IsPlateNotRegistered()
    {
        var service = new ApcoaFlowSessionService(Fake.Api(),
            Substitute.For<IApcoaParkingDataSource>(), NullLogger<ApcoaFlowSessionService>.Instance);

        var result = await service.StartAsync(Fake.ParkingId, "XX 99999", null,
            ApcoaFlowSessionService.StartMode.Normal, null, 20m, false, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo("plate_not_registered");
    }
}
