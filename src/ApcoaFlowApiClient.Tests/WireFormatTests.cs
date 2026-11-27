using System.Net;
using System.Text.Json;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Http;
using ApcoaFlowApiClient.Models;
using Microsoft.Extensions.DependencyInjection;
using static ApcoaFlowApiClient.Tests.FakeNetwork;

namespace ApcoaFlowApiClient.Tests;

/// <summary>Request/response shapes as captured on the wire (observed 2026-11-27).</summary>
public class WireFormatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 14, 33, 0, TimeSpan.Zero);

    [Test]
    [Arguments("PLN 0.00", "PLN", 0.00)]
    [Arguments("PLN 5.00", "PLN", 5.00)]
    [Arguments("PLN 1234.56", "PLN", 1234.56)]
    [Arguments("PLN 2,698.00", "PLN", 2698.00)] // thousands separator on large amounts (30-day quote, 2026-11-27)
    public async Task Prices_parse_as_currency_and_amount(string text, string currency, double amount)
    {
        var money = Money.Parse(text);
        await Assert.That(money.Currency).IsEqualTo(currency);
        await Assert.That(money.Amount).IsEqualTo((decimal)amount);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("5.00")]
    [Arguments("PLN five")]
    [Arguments("PLN 5,00")]   // comma decimal: must not be read as 500
    [Arguments("5.00 PLN")]
    [Arguments("PLN 12,34,567.00")]
    public async Task Odd_prices_dont_parse(string? text) => await Assert.That(Money.TryParse(text, out _)).IsFalse();

    [Test]
    [Arguments("""{"code":"SessionNotFound","message":"Requested Session Not Found."}""", "SessionNotFound")]
    [Arguments("""{"type":"x","title":"One or more validation errors occurred.","status":400,"errors":{"PhoneNumber":["required"]}}""", "ProblemDetails")]
    public async Task Error_bodies_are_read(string json, string code) =>
        await Assert.That(ApcoaFlowApiError.TryParse(json)?.Code).IsEqualTo(code);

    [Test]
    [Arguments("")]
    [Arguments("<html>")]
    [Arguments("[]")]
    public async Task Non_error_bodies_are_not_errors(string json) => await Assert.That(ApcoaFlowApiError.TryParse(json)).IsNull();

    [Test]
    public async Task TariffCode_reads_as_a_string_from_a_number_or_a_string()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var fromNumber = JsonSerializer.Deserialize<SessionSpace>("""{"id":"s","name":"n","tariffCode":22941}""", options)!;
        var fromString = JsonSerializer.Deserialize<SearchSpace>("""{"id":"s","name":"n","tariffCode":"22941"}""", options)!;
        await Assert.That(fromNumber.TariffCode).IsEqualTo("22941");
        await Assert.That(fromString.TariffCode).IsEqualTo("22941");
    }

    [Test]
    public async Task StartParking_sends_the_app_body_dates_and_an_idempotency_key()
    {
        var net = new FakeNetwork().On("POST /connect/token", Token("A1", "R1"))
            .On("POST /api/v1/sessions/startParking", Reply(HttpStatusCode.OK,
                """{"id":"35da0b7f","status":null,"pendingId":null,"clientSecret":null,"prepaymentMode":null,"publishableKey":null}"""));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        var started = await sp.GetRequiredService<IApcoaFlowApi>().StartParkingAsync(new StartParkingRequest
        {
            SpaceId = "space", LicensePlateId = "plate", PaymentMethodId = "card",
            StartDate = T0, EndDate = T0.AddMinutes(30),
        }, "key-1");

        var call = net.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/v1/sessions/startParking");
        using var body = JsonDocument.Parse(call.Body!);
        await Assert.That(body.RootElement.GetProperty("startDate").GetString()).IsEqualTo("2026-11-27T14:33:00.000Z");
        await Assert.That(body.RootElement.GetProperty("endDate").GetString()).IsEqualTo("2026-11-27T15:03:00.000Z");
        await Assert.That(body.RootElement.GetProperty("spaceId").GetString()).IsEqualTo("space");
        await Assert.That(body.RootElement.GetProperty("licensePlateId").GetString()).IsEqualTo("plate");
        await Assert.That(body.RootElement.GetProperty("isPreBooked").GetBoolean()).IsFalse();
        await Assert.That(call.Request.Headers.GetValues("idempotency-key").Single()).IsEqualTo("key-1");
        await Assert.That(started.Id).IsEqualTo("35da0b7f");
        await Assert.That(started.NeedsPaymentAction).IsFalse();
    }

    [Test]
    public async Task Price_query_uses_the_app_parameter_names()
    {
        var net = new FakeNetwork().On("POST /connect/token", Token("A1", "R1"))
            .On("GET /api/v1/prices", Reply(HttpStatusCode.OK, """{"priceItems":[],"priceDetailItems":[],"totalPrice":"PLN 0.00","prepaymentMode":"autoCharge"}"""));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        var quote = await sp.GetRequiredService<IApcoaFlowApi>().GetPriceAsync(new PriceQuery
        {
            SpaceId = "space", LicensePlateId = "plate", Duration = 1800, StartDate = UtcMillisecondsConverter.ToWire(T0),
        });

        var query = net.Requests.Last().Request.RequestUri!.Query;
        await Assert.That(query).Contains("SpaceId=space");
        await Assert.That(query).Contains("LicensePlateId=plate");
        await Assert.That(query).Contains("Duration=1800");
        await Assert.That(query).Contains("StartDate=2026-11-27T14%3A33%3A00.000Z");
        await Assert.That(quote.Total.IsZero).IsTrue();
    }

    [Test]
    public async Task Bounding_box_pins_carry_the_polygon()
    {
        var net = new FakeNetwork().On("POST /connect/token", Token("A1", "R1"))
            .On("GET /api/v1/spaces/getByBoundingBox", Reply(HttpStatusCode.OK, """
                [{"id":"a","name":"SPP Czarny Dunajec","coordinates":{"latitude":49.441,"longitude":19.855},"parkingType":"startStop",
                  "geoBorder":{"coordinates":[{"latitude":49.44119,"longitude":19.85535},{"latitude":49.44114,"longitude":19.85526},
                  {"latitude":49.441,"longitude":19.85543},{"latitude":49.44119,"longitude":19.85535}],"color":null},"isMunicipality":false},
                 {"id":"b","name":"No polygon","coordinates":{"latitude":50,"longitude":18},"geoBorder":null}]
                """));
        using var sp = TestClient.Build(net, new ManualTimeProvider(T0));

        var pins = await sp.GetRequiredService<IApcoaFlowApi>().GetSpacesByBoundingBoxAsync(new BoundingBoxQuery
        {
            NeLatitude = 49.45, NeLongitude = 19.86, SwLatitude = 49.43, SwLongitude = 19.85, Zoom = 16,
        });

        await Assert.That(pins[0].GeoBorder!.Coordinates.Count).IsEqualTo(4);
        await Assert.That(pins[1].GeoBorder).IsNull();
        var query = net.Requests.Last().Request.RequestUri!.Query;
        await Assert.That(query).Contains("NELatitude=49.45");
        await Assert.That(query).Contains("SWLongitude=19.85");
        await Assert.That(query).Contains("Zoom=16");
    }
}
