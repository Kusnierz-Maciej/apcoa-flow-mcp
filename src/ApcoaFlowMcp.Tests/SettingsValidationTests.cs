using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Security;
using Microsoft.Extensions.Options;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Startup settings validation: the per-section <see cref="IValidateOptions{T}"/> validators and the
/// DataAnnotations bounds that <c>ValidateOnStart</c> enforces. Plain unit tests — no host.
/// </summary>
public class SettingsValidationTests
{
    private const string GoodSecret = "test-token-secret-at-least-32-chars-long";

    private static ValidateOptionsResult Annotations<T>(T options) where T : class =>
        new DataAnnotationValidateOptions<T>(Options.DefaultName).Validate(Options.DefaultName, options);

    // ─── Security ──────────────────────────────────────────────────────────────────────────────────

    private static ValidateOptionsResult Security(string? secret, bool requireKey = true, params string[] keys) =>
        new SecurityOptionsValidator().Validate(Options.DefaultName,
            new SecurityOptions { TokenSecret = secret, RequireMcpApiKey = requireKey, AllowedApiKeys = keys });

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("too-short")]
    [Arguments(SecurityOptionsValidator.PlaceholderTokenSecret)]
    public async Task Security_BadTokenSecret_Fails(string? secret) =>
        await Assert.That(Security(secret, true, "key-1").Failed).IsTrue();

    [Test]
    public async Task Security_RequireKey_WithEmptyAllowlist_Fails()
    {
        await Assert.That(Security(GoodSecret, true).Failed).IsTrue();
        await Assert.That(Security(GoodSecret, true, "", " ").Failed).IsTrue();
    }

    [Test]
    public async Task Security_NoKeyRequired_WithEmptyAllowlist_Succeeds() =>
        await Assert.That(Security(GoodSecret, false).Succeeded).IsTrue();

    [Test]
    public async Task Security_Valid_Succeeds() =>
        await Assert.That(Security(GoodSecret, true, "key-1").Succeeded).IsTrue();

    // ─── Telemetry ─────────────────────────────────────────────────────────────────────────────────

    private static ValidateOptionsResult Telemetry(bool enabled, string? endpoint) =>
        new TelemetryOptionsValidator().Validate(Options.DefaultName,
            new TelemetryOptions { Enabled = enabled, OtlpEndpoint = endpoint });

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("alloy:4317/relative")]
    [Arguments("/v1/traces")]
    [Arguments("alloy:4317")]
    [Arguments("ftp://alloy:4317")]
    public async Task Telemetry_Enabled_WithMissingOrRelativeEndpoint_Fails(string? endpoint) =>
        await Assert.That(Telemetry(true, endpoint).Failed).IsTrue();

    [Test]
    public async Task Telemetry_Disabled_Succeeds_RegardlessOfEndpoint() =>
        await Assert.That(Telemetry(false, null).Succeeded).IsTrue();

    [Test]
    [Arguments("http://alloy:4317")]
    [Arguments("https://otel.example.com:4318")]
    public async Task Telemetry_Enabled_WithAbsoluteHttpEndpoint_Succeeds(string endpoint) =>
        await Assert.That(Telemetry(true, endpoint).Succeeded).IsTrue();

    // ─── ParkingData ───────────────────────────────────────────────────────────────────────────────

    private static ValidateOptionsResult Parking(ParkingDataSourceOptions o) =>
        new ParkingDataSourceOptionsValidator().Validate(Options.DefaultName, o);

    [Test]
    public async Task Parking_Defaults_AreValid()
    {
        await Assert.That(Parking(new()).Succeeded).IsTrue();
        await Assert.That(Annotations(new ParkingDataSourceOptions()).Succeeded).IsTrue();
    }

    [Test]
    public async Task Parking_SoftGreaterThanHard_Fails() =>
        await Assert.That(Parking(new() { SoftRefreshAge = TimeSpan.FromHours(50), HardRefreshAge = TimeSpan.FromHours(48) })
            .Failed).IsTrue();

    [Test]
    public async Task Parking_NonPositiveAge_Fails()
    {
        await Assert.That(Parking(new() { SoftRefreshAge = TimeSpan.Zero }).Failed).IsTrue();
        await Assert.That(Parking(new() { HardRefreshAge = TimeSpan.FromHours(-1), SoftRefreshAge = TimeSpan.FromHours(-2) })
            .Failed).IsTrue();
        await Assert.That(Parking(new() { BackgroundRefreshInterval = TimeSpan.Zero }).Failed).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(13)]
    [Arguments(99)]
    public async Task Parking_GeohashPrecisionOutOfRange_Fails(int precision) =>
        await Assert.That(Annotations(new ParkingDataSourceOptions { GeohashPrecision = precision }).Failed).IsTrue();

    [Test]
    public async Task Parking_BadStorePathOrCompanyId_Fails()
    {
        await Assert.That(Annotations(new ParkingDataSourceOptions { StorePath = "" }).Failed).IsTrue();
        await Assert.That(Annotations(new ParkingDataSourceOptions { CompanyId = 0 }).Failed).IsTrue();
    }

    // ─── ApcoaFlow tools ───────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Tools_Defaults_AreValid() =>
        await Assert.That(Annotations(new ApcoaFlowToolsOptions()).Succeeded).IsTrue();

    [Test]
    [Arguments(0)]
    [Arguments(-5)]
    public async Task Tools_NonPositiveCostOrRadius_Fails(int value)
    {
        await Assert.That(Annotations(new ApcoaFlowToolsOptions { MaxSessionCostPln = value }).Failed).IsTrue();
        await Assert.That(Annotations(new ApcoaFlowToolsOptions { CurrentLocationRadiusMeters = value }).Failed).IsTrue();
    }
}
