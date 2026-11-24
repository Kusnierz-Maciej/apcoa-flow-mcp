using System.Net;
using ApcoaFlowApiClient;
using ApcoaFlowApiClient.Models.Consumer;
using ApcoaFlowApiClient.Models.Parking;
using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowApiClient.Models.Tariff;
using NSubstitute;
using Refit;

namespace ApcoaFlowMcp.Tests;

/// <summary>
/// Shared fakes/builders for the MCP unit tests. The real <see cref="IApcoaFlowApi"/> is mocked with
/// NSubstitute — these tests exercise the orchestration in <c>ApcoaFlowSessionService</c>, never the
/// network. Defaults describe a happy-path account: one registered plate, one default payment method,
/// one default profile, a resolvable lot/tariff, and no promotions.
/// </summary>
internal static class Fake
{
    public const int ParkingId = 1000;
    public const int LotId = 2000;
    public const int TariffId = 3000;
    public const string Plate = "ABC 123DE";
    public const string ParkingName = "Test Parking";
    public const int ConsumerId = 42;
    public const int ProfileId = 99;
    public const int PaymentId = 7;

    /// <summary>A consumer with one plate, one default payment method, one default profile.</summary>
    public static ConsumerProfile Consumer(string plate = Plate) => new()
    {
        Id = ConsumerId,
        VehicleInformationResponses = [new Vehicle { VehicleNumber = plate, IsDefault = 1 }],
        ConsumerUserPaymentMethods = [new PaymentMethod { Id = PaymentId, PaymentType = "BLIK", IsDefault = 1 }],
        ConsumerUserProfileList = [new ConsumerUserProfile { Id = ProfileId, IsDefault = 1 }],
    };

    /// <summary>A single bookable lot with the given optional max duration (minutes).</summary>
    public static List<ParkingLot> Lots(int? maxMinutes = null) =>
    [
        new ParkingLot
        {
            NearbyParkingLot = new NearbyParkingLot
            {
                Id = LotId,
                Name = ParkingName,
                TariffBasicInfoSet = [new TariffBasicInfo { TariffId = TariffId }],
            },
            ParkingLotConfigurationResponse = maxMinutes is { } max
                ? new ParkingLotConfiguration
                {
                    ParkingLotConfigurationList =
                        [new ParkingLotConfigItem { MaxDuration = TimeSpan.FromMinutes(max) }],
                }
                : null,
        },
    ];

    /// <summary>An offers response, optionally carrying one active free-time promo.</summary>
    public static OffersResponse Offers(Offer? promo = null) =>
        new() { Entities = promo is null ? [] : [promo] };

    /// <summary>An active free-time promo with the given free window (minutes).</summary>
    public static Offer FreeTimePromo(int freeMinutes = 90, string name = "PROMO1", int id = 1) => new()
    {
        Id = id,
        Name = name,
        IsActive = 1,
        AutoApply = 1,
        ConcessionType = "free_time",
        ConcessionValue = TimeSpan.FromMinutes(freeMinutes),
        OfferDiscountType = "PROMOTION",
    };

    /// <summary>A tariff breakdown quoting the given PLN total (0 = free).</summary>
    public static TariffBreakdown Quote(decimal pln) => new()
    {
        TotalPayableAmountInCents = (int)(pln * 100),
        CurrencyCode = "PLN",
    };

    /// <summary>A successful start response (ACTIVE) with the given session id and charged amount.</summary>
    public static StartSessionResponse Started(long id = 55555, decimal charged = 0m) => new()
    {
        Id = id,
        ParkingSessionStatus = "ACTIVE",
        TransactionStatus = "SUCCESSFUL",
        TransactionAmount = charged,
        CurrencyCode = "PLN",
    };

    /// <summary>An active session row for the "already active" (424) path.</summary>
    public static ParkingSessionV2 ActiveSession(
        long id, int parkingId, string? name, string plate = Plate, int minutesLeft = 30) => new()
    {
        Id = id,
        ParkingId = parkingId,
        ParkingName = name,
        ParkingSessionStatus = "ACTIVE",
        EntryConsumerIdentificationExternalIdentifier = plate,
        CheckinTime = DateTimeOffset.UtcNow.AddMinutes(-5),
        ScheduledCheckoutTime = DateTimeOffset.UtcNow.AddMinutes(minutesLeft),
    };

    /// <summary>Builds an <see cref="IApiResponse{T}"/> with the given status and typed body.</summary>
    public static IApiResponse<ValidatePromotionResponse> ValidateResponse(
        HttpStatusCode status, ValidatePromotionResponse? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://x/promotion/validate");
        var http = new HttpResponseMessage(status) { RequestMessage = request };
        ApiException? error = status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices
            ? null
            : ApiException.Create(request, HttpMethod.Post, http, new RefitSettings()).Result;
        return new ApiResponse<ValidatePromotionResponse>(http, body, new RefitSettings(), error);
    }

    /// <summary>A validate-promotion response that says the promo is usable (200, no error body).</summary>
    public static IApiResponse<ValidatePromotionResponse> ValidOk() =>
        ValidateResponse(HttpStatusCode.OK, new ValidatePromotionResponse());

    /// <summary>A validate-promotion response that rejects the promo (422 INVALID_PROMOTION_CODE).</summary>
    public static IApiResponse<ValidatePromotionResponse> ValidRejected() =>
        ValidateResponse(HttpStatusCode.UnprocessableEntity, null);

    /// <summary>
    /// A mocked API wired for the happy path: resolvable lot/tariff, the given consumer, the given
    /// offers, a free quote, and a successful start. Individual tests override specific calls.
    /// </summary>
    public static IApcoaFlowApi Api(
        ConsumerProfile? consumer = null,
        List<ParkingLot>? lots = null,
        OffersResponse? offers = null,
        TariffBreakdown? quote = null,
        StartSessionResponse? started = null)
    {
        var api = Substitute.For<IApcoaFlowApi>();
        api.GetParkingLotsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(lots ?? Lots());
        api.GetConsumerAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(consumer ?? Consumer());
        api.GetApplicableOffersV2Async(Arg.Any<ApplicableOffersV2Request>(), Arg.Any<CancellationToken>())
            .Returns(offers ?? Offers());
        api.GetTariffBreakdownAsync(Arg.Any<TariffBreakdownQuery>(), Arg.Any<CancellationToken>())
            .Returns(quote ?? Quote(0m));
        api.StartParkingSessionAsync(Arg.Any<StartParkingSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(started ?? Started());
        api.ValidatePromotionAsync(Arg.Any<ValidatePromotionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ValidOk());
        return api;
    }
}
