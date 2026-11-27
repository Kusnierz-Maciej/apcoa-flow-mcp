using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApcoaFlowMcp.Tests;

/// <summary>Every write tool writes ONE information line after the call: tool, parking, outcome, session, minutes, cost.</summary>
public class ToolLogTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }

    private static (ApcoaFlowTools Tools, ListLogger<ApcoaFlowTools> Log) Build(ApcoaFlowApiClient.Api.IApcoaFlowApi api)
    {
        var time = new FakeApi.FixedTime(FakeApi.Now);
        var log = new ListLogger<ApcoaFlowTools>();
        var pricing = new Pricing(api, time);
        return (new ApcoaFlowTools(api, new ParkingFinder(api), pricing, new Sessions(api, pricing, time, NullLogger<Sessions>.Instance),
            Options.Create(new ApcoaFlowToolsOptions()), time, log), log);
    }

    [Test]
    public async Task A_started_session_logs_one_line_with_its_outcome_and_ids()
    {
        var (tools, log) = Build(FakeApi.ForStarts(_ => 0m));

        await tools.StartFreeSessionAsync(FakeApi.ParkingId, FakeApi.ParkingName, "ABC12345", 30);

        var line = log.Lines.Single(l => l.Level == LogLevel.Information).Message;
        await Assert.That(line).IsEqualTo($"start_free_session {FakeApi.ParkingName}: started; session new-session, 30 min, 0.00 PLN (confirmed -)");
    }

    [Test]
    public async Task A_paid_quote_logs_the_cost_and_the_quote_outcome()
    {
        var (tools, log) = Build(FakeApi.ForStarts(_ => 5m));

        await tools.StartSessionAsync(FakeApi.ParkingId, FakeApi.ParkingName, "ABC12345", 60);

        var line = log.Lines.Single(l => l.Level == LogLevel.Information).Message;
        await Assert.That(line).IsEqualTo($"start_session {FakeApi.ParkingName}: quote; session -, 60 min, 5.00 PLN (confirmed -)");
    }
}
