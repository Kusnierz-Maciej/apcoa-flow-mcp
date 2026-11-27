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

    // ─── Stdio credentials ─────────────────────────────────────────────────────────────────────────

    private static ValidateOptionsResult Stdio(string? email, string? password) =>
        new StdioCredentialsOptionsValidator().Validate(Options.DefaultName,
            new StdioCredentialsOptions { Email = email, Password = password });

    [Test]
    public async Task Stdio_MissingEmailOrPassword_Fails()
    {
        await Assert.That(Stdio(null, "pw").Failed).IsTrue();
        await Assert.That(Stdio("a@b.c", " ").Failed).IsTrue();
        await Assert.That(Stdio(null, null).Failures!.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task Stdio_BothSet_Succeeds() =>
        await Assert.That(Stdio("a@b.c", "pw").Succeeded).IsTrue();

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

public class OAuthSettingsValidationTests
{
    private static bool Valid(Action<ApcoaFlowMcp.Security.OAuth.OAuthOptions> change)
    {
        var o = new ApcoaFlowMcp.Security.OAuth.OAuthOptions();
        change(o);
        return new ApcoaFlowMcp.Security.OAuth.OAuthOptionsValidator().Validate(null, o).Succeeded;
    }

    [Test]
    public async Task Defaults_AreValid_1Min_1Hour_365Days()
    {
        var o = new ApcoaFlowMcp.Security.OAuth.OAuthOptions();
        await Assert.That(Valid(_ => { })).IsTrue();
        await Assert.That(o.AuthorizationCodeLifetime).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(o.AccessTokenLifetime).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(o.RefreshTokenLifetime).IsEqualTo(TimeSpan.FromDays(365));
    }

    [Test]
    public async Task Rejects_NonPositive_LongCode_AndAccessLongerThanRefresh()
    {
        await Assert.That(Valid(o => o.AuthorizationCodeLifetime = TimeSpan.Zero)).IsFalse();
        await Assert.That(Valid(o => o.AuthorizationCodeLifetime = TimeSpan.FromMinutes(11))).IsFalse();
        await Assert.That(Valid(o => o.AccessTokenLifetime = TimeSpan.FromSeconds(-1))).IsFalse();
        await Assert.That(Valid(o => o.RefreshTokenLifetime = TimeSpan.Zero)).IsFalse();
        await Assert.That(Valid(o => { o.AccessTokenLifetime = TimeSpan.FromDays(2); o.RefreshTokenLifetime = TimeSpan.FromDays(1); })).IsFalse();
    }
}
