using System.Net;
using ApcoaFlowApiClient.Auth;
using ApcoaFlowMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Refit;

namespace ApcoaFlowMcp.Tests;

public class ToolErrorTests
{
    private static async Task<ApiException> Api(HttpStatusCode status, string body = "{}") =>
        await ApiException.Create(
            new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/api/v1/sessions/stopParking/x"),
            HttpMethod.Post,
            new HttpResponseMessage(status) { Content = new StringContent(body) },
            new RefitSettings());

    [Test]
    [Arguments(HttpStatusCode.NotFound, "not_found")]
    [Arguments(HttpStatusCode.Forbidden, "forbidden")]
    [Arguments(HttpStatusCode.Unauthorized, "credentials_rejected")]
    [Arguments(HttpStatusCode.TooManyRequests, "rate_limited")]
    [Arguments(HttpStatusCode.BadGateway, "unavailable")]
    [Arguments(HttpStatusCode.BadRequest, "rejected_request")]
    public async Task ApiStatus_MapsToOutcome(HttpStatusCode status, string outcome) =>
        await Assert.That(ToolErrors.Map(await Api(status))!.Outcome).IsEqualTo(outcome);

    [Test]
    [Arguments(HttpStatusCode.NotFound, """{"code":"SessionNotFound","message":"Requested Session Not Found."}""", "session_not_found")]
    [Arguments(HttpStatusCode.BadRequest, """{"code":"ParkingSessionInvalidArgument","message":"Only planned or active sessions can be extended."}""", "session_refused")]
    [Arguments(HttpStatusCode.Forbidden, """{"code":"Unauthenticated","message":"An error has occurred."}""", "credentials_rejected")]
    public async Task ApiErrorCode_TakesPrecedenceOverStatus(HttpStatusCode status, string body, string outcome) =>
        await Assert.That(ToolErrors.Map(await Api(status, body))!.Outcome).IsEqualTo(outcome);

    [Test]
    public async Task SessionRefused_PassesTheApiMessageOn()
    {
        var mapped = ToolErrors.Map(await Api(HttpStatusCode.BadRequest,
            """{"code":"ParkingSessionInvalidArgument","message":"The session for the same license plate has already been started."}"""));

        await Assert.That(mapped!.Message).Contains("The session for the same license plate has already been started.");
    }

    [Test]
    public async Task AuthFailures_AreFoundInsideRefitsWrapper()
    {
        var rejected = new InvalidOperationException("outer", new ApcoaFlowCredentialsRejectedException("invalid_grant"));
        var unavailable = new InvalidOperationException("outer", new ApcoaFlowAuthUnavailableException("HTTP 503"));

        await Assert.That(ToolErrors.Map(rejected)!.Outcome).IsEqualTo("credentials_rejected");
        await Assert.That(ToolErrors.Map(unavailable)!.Outcome).IsEqualTo("unavailable");
    }

    [Test]
    public async Task Transport_IsUnavailable_Cancellation_And_Unknown_AreNotMapped()
    {
        await Assert.That(ToolErrors.Map(new HttpRequestException("dns"))!.Outcome).IsEqualTo("unavailable");
        await Assert.That(ToolErrors.Map(new OperationCanceledException())).IsNull();
        await Assert.That(ToolErrors.Map(new InvalidOperationException("bug"))).IsNull();
    }

    [Test]
    public async Task Tool_5xx_ThrowsMcpException_WithPlainMessage()
    {
        var api = FakeApi.Create();
        api.GetVehiclesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(await Api(HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<McpException>(() => new ApcoaFlowTools(api, NullLogger<ApcoaFlowTools>.Instance).GetVehiclesAsync());

        await Assert.That(ex!.Message).Contains("temporarily unavailable");
    }

    [Test]
    public async Task Tool_RejectedCredentials_AsksToReconnect()
    {
        var api = FakeApi.Create();
        api.GetVehiclesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Refit wrapper", new ApcoaFlowCredentialsRejectedException("invalid_grant")));

        var ex = await Assert.ThrowsAsync<McpException>(() => new ApcoaFlowTools(api, NullLogger<ApcoaFlowTools>.Instance).GetVehiclesAsync());

        await Assert.That(ex!.Message).IsEqualTo(ToolErrors.CredentialsRejected);
    }
}
