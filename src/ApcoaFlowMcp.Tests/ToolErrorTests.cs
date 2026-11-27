using System.Net;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.ParkingData;
using ApcoaFlowMcp.Apcoa;
using ApcoaFlowMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Refit;

namespace ApcoaFlowMcp.Tests;

public class ToolErrorTests
{
    private static async Task<ApiException> Api(HttpStatusCode status, string body = "{}") =>
        await ApiException.Create(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/authentication/v1/consumer"),
            HttpMethod.Get,
            new HttpResponseMessage(status) { Content = new StringContent(body) },
            new RefitSettings());

    private const string SessionNotFound =
        """{"transactionId":"x","message":"PARKING_SESSION_NOT_FOUND","statusCode":5001,"description":null}""";

    private static ApcoaFlowTools Tools(IApcoaFlowApi api) =>
        new(new ApcoaFlowSessionService(api, Substitute.For<IApcoaParkingDataSource>(),
                NullLogger<ApcoaFlowSessionService>.Instance),
            Substitute.For<IApcoaParkingDataSource>(),
            Options.Create(new ApcoaFlowToolsOptions()),
            NullLogger<ApcoaFlowTools>.Instance);

    [Test]
    [Arguments(HttpStatusCode.NotFound, "not_found")]
    [Arguments(HttpStatusCode.Forbidden, "forbidden")]
    [Arguments(HttpStatusCode.Unauthorized, "credentials_rejected")]
    [Arguments((HttpStatusCode)419, "credentials_rejected")]
    [Arguments(HttpStatusCode.TooManyRequests, "rate_limited")]
    [Arguments(HttpStatusCode.BadGateway, "unavailable")]
    [Arguments(HttpStatusCode.BadRequest, "rejected_request")]
    public async Task ApiStatus_MapsToOutcome(HttpStatusCode status, string outcome) =>
        await Assert.That(ToolErrors.Map(await Api(status))!.Outcome).IsEqualTo(outcome);

    [Test]
    public async Task RejectedCredentials_FoundInsideWrapper()
    {
        // Refit wraps handler exceptions (e.g. ApiRequestException → inner).
        var wrapped = new InvalidOperationException("outer", new ApcoaFlowCredentialsRejectedException("HTTP 401"));
        await Assert.That(ToolErrors.Map(wrapped)!.Outcome).IsEqualTo("credentials_rejected");
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
        var api = Fake.Api();
        api.GetConsumerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(await Api(HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<McpException>(() => Tools(api).GetRegisteredCarsAsync());

        await Assert.That(ex!.Message).Contains("temporarily unavailable");
    }

    [Test]
    public async Task Tool_RejectedCredentials_AsksToReconnect()
    {
        var api = Fake.Api();
        api.GetParkingSessionsAsync(Arg.Any<ApcoaFlowApiClient.Models.Session.ParkingSessionV2Query>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApcoaFlowCredentialsRejectedException("HTTP 401 after re-login"));

        var ex = await Assert.ThrowsAsync<McpException>(() => Tools(api).GetActiveSessionsAsync());

        await Assert.That(ex!.Message).IsEqualTo(ToolErrors.CredentialsRejected);
    }

    [Test]
    public async Task SessionNotFound_422_MapsToPlainMessage() =>
        await Assert.That(ToolErrors.Map(await Api(HttpStatusCode.UnprocessableEntity, SessionNotFound))!.Outcome)
            .IsEqualTo("session_not_found");

    [Test]
    public async Task EndSession_UnknownOrEndedId_SaysSoInsteadOfEnded()
    {
        var api = Fake.Api();
        api.StopParkingSessionAsync(Arg.Any<ApcoaFlowApiClient.Models.Session.StopParkingSessionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(await Api(HttpStatusCode.UnprocessableEntity, SessionNotFound));

        var ex = await Assert.ThrowsAsync<McpException>(() => Tools(api).EndSessionAsync(84420857));

        await Assert.That(ex!.Message).Contains("no active parking session with that id");
    }
}
