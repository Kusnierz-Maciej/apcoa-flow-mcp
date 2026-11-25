using System.Text;
using System.Text.Json;
using ApcoaFlowApiClient.ParkingData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApcoaFlowApiClient.Tests;

// The capture harness ends with a trivial assertion; it exists for its console output, not a
// pass/fail condition. Suppress the constant-assertion analyzer warning.
#pragma warning disable TUnitAssertions0005

/// <summary>
/// READ-ONLY live capture harness. Dumps raw JSON from safe endpoints so the real response
/// shapes (tariff breakdown, active sessions, registered cars, tariff info) can be confirmed
/// before implementing cost-aware session tools.
///
/// SAFETY: these calls never start, extend, or stop a session. Everything targets the free
/// test parking (id 616319 / lot 41905) or is a pure GET. Run explicitly with the "LiveApi"
/// category; it is not part of the normal test run.
/// </summary>
[Category("LiveApi")]
[NotInParallel("LiveApi")]
public class LiveApiCaptureTests
{
    // Free test parking — unlimited free sessions, safe to query. See docs/API-NOTES.md.
    private const int TestParkingId = 616319;
    private const int TestParkingLotId = 41905;
    private const int TestTariffId = 108014;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static readonly StringBuilder Output = new();

    private static string OutputPath =>
        Environment.GetEnvironmentVariable("APCOA_CAPTURE_OUT") ?? "/tmp/apcoa-capture.txt";

    private static string PaidOutputPath =>
        Environment.GetEnvironmentVariable("APCOA_CAPTURE_PAID_OUT") ?? "/tmp/apcoa-capture-paid.txt";

    private static HttpClient BuildRawClient()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<LiveApiCaptureTests>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var email = config["ApcoaFlow:Email"] ?? throw new InvalidOperationException("ApcoaFlow:Email not set.");
        var password = config["ApcoaFlow:Password"] ?? throw new InvalidOperationException("ApcoaFlow:Password not set.");

        var options = new ApcoaFlowClientOptions { Email = email, Password = password };
        var services = new ServiceCollection();
        services.AddSingleton<IApcoaFlowCredentialProvider>(new StaticProvider(options));
        services.AddSingleton<ApcoaFlowTokenStore>();
        var sp = services.BuildServiceProvider();

        var authHandler = new ApcoaFlowAuthHandler(
            sp.GetRequiredService<IApcoaFlowCredentialProvider>(),
            sp.GetRequiredService<ApcoaFlowTokenStore>())
        {
            InnerHandler = new HttpClientHandler(),
        };

        return new HttpClient(authHandler) { BaseAddress = new Uri(ApcoaFlowClientOptions.BaseUrl) };
    }

    private static (IApcoaFlowApi Api, IApcoaParkingDataSource Data) BuildClients()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<LiveApiCaptureTests>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = new ApcoaFlowClientOptions
        {
            Email = config["ApcoaFlow:Email"] ?? throw new InvalidOperationException("ApcoaFlow:Email not set."),
            Password = config["ApcoaFlow:Password"] ?? throw new InvalidOperationException("ApcoaFlow:Password not set."),
        };
        var services = new ServiceCollection();
        services.AddSingleton<IApcoaFlowCredentialProvider>(new StaticProvider(options));
        services.AddApcoaFlowApiClient();
        // Point the snapshot store at a writable temp dir (the default /data/parking is the Docker volume).
        var storeDir = Path.Combine(Path.GetTempPath(), "apcoa-capture-store");
        services.AddApcoaParkingDataSource(configure: o => o.StorePath = storeDir);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IApcoaFlowApi>(), sp.GetRequiredService<IApcoaParkingDataSource>());
    }

    // App config is a PUBLIC endpoint (419 if a bearer token is sent). The parking data source depends
    // on it for the decryption key, so verify it in isolation first — a failure here must be detected
    // as itself, not masked as a FindNearbyAsync failure in the parking test below.
    [Test]
    public async Task AppConfig_IsPublic_AndReturnsParkingDataKey()
    {
        var (api, _) = BuildClients();
        var config = await api.GetAppConfigAsync(9);
        await Assert.That(config).IsNotNull()
            .Because("app config is a public endpoint and must return without a bearer token (419 if one is sent)");
    }

    // Verifies the public parking-lookup fix without hardcoding an id: discover a real parking near
    // Tarnowskie Góry via the offline index, pick one at random, then call the public parkingLots /
    // detail endpoints on it. They must return 200 (no bearer token).
    // Depends on AppConfig test because FindNearbyAsync needs the app-config decryption key.
    [Test]
    [DependsOn(nameof(AppConfig_IsPublic_AndReturnsParkingDataKey))]
    public async Task PublicParkingEndpoints_SucceedWithoutBearer()
    {
        var (api, data) = BuildClients();

        // Precondition this test depends on: the parking data source needs the app-config key.
        // Assert it directly so a precondition failure is reported here, not masked inside FindNearbyAsync.
        var config = await api.GetAppConfigAsync(9);
        await Assert.That(config).IsNotNull()
            .Because("precondition: FindNearbyAsync needs the app-config decryption key, so app config must be reachable");

        // Tarnowskie Góry town centre; 5 km covers the local parkings.
        var nearby = await data.FindNearbyAsync(50.4453, 18.8617, radiusKm: 5.0);
        await Assert.That(nearby.Count).IsGreaterThan(0)
            .Because("the offline index should return parkings near Tarnowskie Góry");

        var picked = nearby[Random.Shared.Next(nearby.Count)].Parking;

        var lots = await api.GetParkingLotsAsync(picked.Id);
        await Assert.That(lots).IsNotNull()
            .Because($"parkingLots for parking {picked.Id} is public and must return without a bearer token");

        var detail = await api.GetParkingAsync(picked.Id);
        await Assert.That(detail).IsNotNull()
            .Because($"parking detail for {picked.Id} is public and must return without a bearer token");
        await Assert.That(detail.Id).IsEqualTo(picked.Id)
            .Because("the returned detail should be for the requested parking id");
    }

    private static async Task DumpAsync(string label, HttpClient http, string url)
    {
        Output.AppendLine($"\n===== {label} =====");
        Output.AppendLine($"GET {url}");
        try
        {
            var resp = await http.GetAsync(url);
            Output.AppendLine($"Status: {(int)resp.StatusCode} {resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                Output.AppendLine("(empty body)");
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(body);
                Output.AppendLine(JsonSerializer.Serialize(doc.RootElement, Pretty));
            }
            catch
            {
                Output.AppendLine(body);
            }
        }
        catch (Exception ex)
        {
            Output.AppendLine($"ERROR: {ex.Message}");
        }
    }

    [Test]
    public async Task Capture_ReadOnlyShapes()
    {
        var http = BuildRawClient();

        // Registered cars + payment methods (req 7)
        await DumpAsync("consumer (cars + payment methods)", http,
            "/authentication/v1/consumer?isPaymentErrorCodeRequired=false");

        // Active sessions (req 3) — empty if none, still reveals the shape when populated
        await DumpAsync("active sessions", http,
            "/authentication/v1/consumer/parkingSessionV2?status=ACTIVE&pageNumber=1&pageSize=5&displayVat=false&displayVatWithoutConvenienceFeeVat=false&parkingSessionStatuses=&profileNames=");

        // Session history — reveals the completed-session shape incl. cost (reqs 5, 6)
        await DumpAsync("session history (completed)", http,
            "/authentication/v1/consumer/parkingSessionV2?status=COMPLETED&pageNumber=1&pageSize=5&displayVat=true&displayVatWithoutConvenienceFeeVat=false&parkingSessionStatuses=&profileNames=");

        // Tariff info for the free test lot
        await DumpAsync("tariff info (lot 41905)", http,
            $"/authentication/v1/tariff?parkingLotId={TestParkingLotId}");

        // Tariff breakdown at several durations — THE key capture for free-period + cost logic (reqs 4, 5)
        var entry = DateTimeOffset.UtcNow.ToString("MM/dd/yyyy HH:mm");
        foreach (var minutes in new[] { 0, 30, 60, 180, 600, 1440 })
        {
            var exit = DateTimeOffset.UtcNow.AddMinutes(minutes).ToString("MM/dd/yyyy HH:mm");
            var url =
                $"/authentication/v1/tariff/tariffBreakDownV2?company=PL&duration={minutes}" +
                $"&entryTime={Uri.EscapeDataString(entry)}&exitTime={Uri.EscapeDataString(exit)}" +
                $"&parentTariffId={TestTariffId}&parkingLotId={TestParkingLotId}" +
                $"&productType=CONSUMER_APP&tariffId={TestTariffId}&vehicleNumbwe=STA%200521";
            await DumpAsync($"tariff breakdown ({minutes} min)", http, url);
        }

        await File.WriteAllTextAsync(OutputPath, Output.ToString());
        await Assert.That(File.Exists(OutputPath)).IsTrue();
    }

    // Paid + mixed parkings to see non-zero cost and populated free-duration fields.
    // Carrefour Tarnowskie Góry (9882) is reported as mixed free/paid; Aldi (8331) paid-only.
    // Lot 10584 / tariff 6967 is a known paid tariff from the traffic dumps.
    [Test]
    public async Task Capture_PaidAndMixedTariffs()
    {
        Output.Clear();
        var http = BuildRawClient();

        // Real (parkingLotId, tariffId) pairs the app actually priced in the traffic dumps.
        // Probe each across durations to find any with a free-duration window or free hours.
        var pairs = new (string Label, int Lot, int Tariff)[]
        {
            ("lot 10584", 10584, 6967),
            ("lot 9085", 9085, 7562),
            ("lot 11089", 11089, 86056),
        };

        var entry = DateTimeOffset.UtcNow.ToString("MM/dd/yyyy HH:mm");
        foreach (var (label, lot, tariff) in pairs)
        {
            foreach (var minutes in new[] { 0, 30, 60, 120, 240, 480 })
            {
                var exit = DateTimeOffset.UtcNow.AddMinutes(minutes).ToString("MM/dd/yyyy HH:mm");
                var url =
                    $"/authentication/v1/tariff/tariffBreakDownV2?company=PL&duration={minutes}" +
                    $"&entryTime={Uri.EscapeDataString(entry)}&exitTime={Uri.EscapeDataString(exit)}" +
                    $"&parentTariffId={tariff}&parkingLotId={lot}" +
                    $"&productType=CONSUMER_APP&tariffId={tariff}&vehicleNumbwe=STA%200521";
                await DumpAsync($"breakdown {label} ({minutes} min)", http, url);
            }
        }

        await File.WriteAllTextAsync(PaidOutputPath, Output.ToString());
        await Assert.That(File.Exists(PaidOutputPath)).IsTrue();
    }

    private sealed class StaticProvider(ApcoaFlowClientOptions options) : IApcoaFlowCredentialProvider
    {
        public ApcoaFlowClientOptions GetCredentials() => options;
    }
}
#pragma warning restore TUnitAssertions0005
