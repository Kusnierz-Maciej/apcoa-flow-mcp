using ApcoaFlowApiClient.Models.Auth;
using ApcoaFlowApiClient.Models.Config;
using ApcoaFlowApiClient.Models.Consumer;
using ApcoaFlowApiClient.Models.Ev;
using ApcoaFlowApiClient.Models.Favourite;
using ApcoaFlowApiClient.Models.Parking;
using ApcoaFlowApiClient.Models.Permit;
using ApcoaFlowApiClient.Models.Pass;
using ApcoaFlowApiClient.Models.Promotion;
using ApcoaFlowApiClient.Models.Session;
using ApcoaFlowApiClient.Models.SubCorporate;
using ApcoaFlowApiClient.Models.Tariff;
using Refit;
using System.Net.Http;

namespace ApcoaFlowApiClient;

public interface IApcoaFlowApi
{
    // -------------------------------------------------------------------------
    // Auth
    // -------------------------------------------------------------------------

    /// <summary>
    /// Authenticates with email/password. Returns <see cref="LoginResponse.AuthToken"/> which is the bearer token.
    /// Token TTL is 90 days (7776000000 ms). Also returns a <see cref="LoginResponse.RefreshToken"/> for silent renewal.
    /// <see cref="ApcoaFlowAuthHandler"/> handles token storage and injection.
    /// </summary>
    [Post("/authentication/v2/consumer/login")]
    Task<LoginResponse> LoginAsync([Body] LoginRequest request, CancellationToken ct = default);

    /// <summary>Logs out by invalidating the refresh token. Sends as form-urlencoded.</summary>
    [Post("/authentication/v1/consumer/logout")]
    Task LogoutAsync([Body(BodySerializationMethod.UrlEncoded)] LogoutRequest request, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Consumer profile
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the authenticated consumer's profile including their registered vehicles.
    /// Pass <paramref name="isPaymentErrorCodeRequired"/> = true to include payment error details
    /// (used by the home screen to show payment warnings).
    /// </summary>
    [Get("/authentication/v1/consumer")]
    Task<ConsumerProfile> GetConsumerAsync([Query] bool isPaymentErrorCodeRequired = false, CancellationToken ct = default);

    /// <summary>Updates account identification fields (name, phone).</summary>
    [Put("/authentication/v1/accountIdentification")]
    Task UpdateAccountIdentificationAsync([Body] UpdateAccountIdentificationRequest request, CancellationToken ct = default);

    /// <summary>Registers or updates the device push token and platform info.</summary>
    [Post("/authentication/v1/consumer/device")]
    Task RegisterDeviceAsync([Body] RegisterDeviceRequest request, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Parking sessions (STP)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lists parking sessions. The app always polls this with <c>status=ACTIVE</c> and <c>pageSize=1</c>
    /// to detect whether a session is running, and separately with <c>pageSize=10</c> for the history list.
    /// Supported statuses: ACTIVE | COMPLETED | IN_REVIEW.
    /// </summary>
    [Get("/authentication/v1/consumer/parkingSessionV2")]
    Task<List<ParkingSessionV2>> GetParkingSessionsAsync([Query] ParkingSessionV2Query query, CancellationToken ct = default);

    /// <summary>
    /// Starts a new parking session. Called after the pre-flight sequence (validate hours → tariff
    /// breakdown → apply promotion). Returns a payment envelope with the session nested under
    /// <see cref="StartSessionResponse.ParkingSession"/>.
    /// </summary>
    [Post("/authentication/v1/consumer/parkingSession")]
    Task<StartSessionResponse> StartParkingSessionAsync([Body] StartParkingSessionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Extends an active parking session. Body shape TBD — not yet captured.
    /// See docs/API-NOTES.md for details.
    /// </summary>
    [Post("/authentication/v1/consumer/parkingSession/extendParkingSession")]
    Task<ParkingSessionV2> ExtendParkingSessionAsync([Body] ExtendParkingSessionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Stops (ends) the active parking session. Body shape TBD — not yet captured.
    /// See docs/API-NOTES.md for details.
    /// </summary>
    [Put("/authentication/v1/consumer/parkingSession")]
    Task<ParkingSessionV2> StopParkingSessionAsync([Body] StopParkingSessionRequest request, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // EV sessions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lists EV charging sessions. The trailing slash in the path is real and required — the server
    /// returns 404 without it. Typical usage: <c>statusList=ACTIVE,RELEASED</c> to check for active session.
    /// </summary>
    [Get("/authentication/v1/evSession/")]
    Task<List<EvSession>> GetEvSessionsAsync([Query] EvSessionQuery query, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Parking details (no auth required)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns full details for a single parking location, including address, coordinates, lots, and
    /// the human-readable price description. PUBLIC endpoint — must be called without a bearer token
    /// (the <c>X-Apcoa-NoAuth</c> header makes <see cref="ApcoaFlowAuthHandler"/> omit Authorization).
    /// </summary>
    [Get("/authentication/v1/parking/{parkingId}")]
    [Headers("X-Apcoa-NoAuth: true")]
    Task<ParkingDetail> GetParkingAsync(int parkingId, CancellationToken ct = default);

    /// <summary>
    /// Returns the parking lots (bays/areas) within a parking, including each lot's tariff IDs.
    /// PUBLIC endpoint — must be called without a bearer token (see <c>X-Apcoa-NoAuth</c>).
    /// </summary>
    [Get("/authentication/v1/parking/{parkingId}/parkingLots")]
    [Headers("X-Apcoa-NoAuth: true")]
    Task<List<ParkingLot>> GetParkingLotsAsync(int parkingId, [Query] string parkingSessionType = "STP_BOOKING", CancellationToken ct = default);

    /// <summary>Returns a parking image by its UUID. PUBLIC — no bearer token (see <c>X-Apcoa-NoAuth</c>).</summary>
    [Get("/authentication/v1/parking/parkingImages/{imageId}")]
    [Headers("X-Apcoa-NoAuth: true")]
    Task<HttpContent> GetParkingImageAsync(string imageId, CancellationToken ct = default);

    /// <summary>Returns the server's current UTC time. Used immediately before starting a session.</summary>
    [Get("/authentication/v1/parking/utcDateTime")]
    Task<string> GetUtcDateTimeAsync(CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Sponsor ads (no auth required)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns in-app sponsor ads for the given company and parking IDs.
    /// Pass comma-separated IDs when querying multiple. PUBLIC — no bearer token.
    /// </summary>
    [Get("/authentication/v1/dashboard/inAppSponsorAds/getInAppSponsorAdsDetails")]
    [Headers("X-Apcoa-NoAuth: true")]
    Task<List<SponsorAd>> GetSponsorAdsAsync([Query] string companyIdList, [Query] string parkingIdList, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Favourites
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the user's favourite parkings. Returns 204 (empty list) when none are saved — Refit will
    /// return null; treat as empty.
    /// </summary>
    [Get("/authentication/v1/consumers/favourite/parking")]
    Task<List<FavouriteParking>?> GetFavouriteParkingsAsync(CancellationToken ct = default);

    /// <summary>Adds a parking to the user's favourites.</summary>
    [Post("/authentication/v1/consumers/favourite/add/parking")]
    Task AddFavouriteParkingAsync([Body] AddFavouriteParkingRequest request, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Tariff & pricing
    // -------------------------------------------------------------------------

    /// <summary>Returns tariff info for a parking lot.</summary>
    [Get("/authentication/v1/tariff")]
    Task<TariffInfo> GetTariffAsync([Query] int parkingLotId, CancellationToken ct = default);

    /// <summary>
    /// Returns an itemised cost breakdown for the given duration/window.
    /// <para>IMPORTANT: The <c>vehicleNumbwe</c> query param is a server-side typo (not "vehicleNumber").
    /// <see cref="TariffBreakdownQuery.VehicleNumbwe"/> maps to it correctly.</para>
    /// <para>When extending an existing session, also pass <see cref="TariffBreakdownQuery.ParkingSessionId"/>.</para>
    /// </summary>
    [Get("/authentication/v1/tariff/tariffBreakDownV2")]
    Task<TariffBreakdown> GetTariffBreakdownAsync([Query] TariffBreakdownQuery query, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Operational hours
    // -------------------------------------------------------------------------

    /// <summary>Validates whether a parking lot is open for the requested entry/exit window.</summary>
    [Post("/authentication/v1/parking/operational-hours/validate")]
    Task<OperationalHoursValidateResponse> ValidateOperationalHoursAsync([Body] OperationalHoursValidateRequest request, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Promotions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns applicable promotions/offers for a parking lot (v1 variant). Response shape varies; kept
    /// for completeness. Prefer <see cref="GetApplicableOffersV2Async"/> which returns a typed offer list.
    /// </summary>
    [Post("/authentication/v2/promotion/applicableOffersWithPromotion")]
    Task<List<Offer>> GetApplicableOffersAsync([Body] ApplicableOffersRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns active promotions/coupons for a parking lot and user. Free-parking coupons have
    /// <c>concessionType=="free_time"</c>; their free window is in <c>concessionValue</c> (minutes).
    /// </summary>
    [Post("/authentication/v1/promotion/applicableOffersWithPromotionV2")]
    Task<OffersResponse> GetApplicableOffersV2Async([Body] ApplicableOffersV2Request request, CancellationToken ct = default);

    /// <summary>
    /// Validates a chosen promotion before applying it to a session. The 200 response body is opaque
    /// (merged/consumer blob); only success matters, so the result is read as a raw JSON element.
    /// </summary>
    [Post("/authentication/v1/promotion/validate")]
    Task<System.Text.Json.JsonElement> ValidatePromotionAsync([Body] ValidatePromotionRequest request, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Permits
    // -------------------------------------------------------------------------

    /// <summary>Returns permit card details (unpaid amounts) for a consumer.</summary>
    [Get("/authentication/v1/permit/allocation/consumer/card-details")]
    Task<PermitCardDetail> GetPermitCardDetailsAsync([Query] int consumerUserId, [Query] string paymentStatus = "UNPAID", CancellationToken ct = default);

    /// <summary>Returns the consumer's permit allocations (v2 paginated list).</summary>
    [Get("/authentication/v2/permitAllocation/consumer/list/{consumerId}")]
    Task<List<PermitAllocation>> GetPermitAllocationsAsync(int consumerId, [Query] PermitAllocationQuery query, CancellationToken ct = default);

    /// <summary>Returns count of active permit allocations for a consumer at a given parking.</summary>
    [Get("/authentication/v1/permit/permit-allocation/consumer/parking/{parkingId}/active/count")]
    Task<int> GetActivePermitCountAsync(int parkingId, [Query] ActivePermitCountQuery query, CancellationToken ct = default);

    /// <summary>Maps permit master data to parking lots.</summary>
    [Post("/authentication/v1/permit/master/parking-lot/map")]
    Task<object> MapPermitMasterParkingLotsAsync([Body] PermitMasterParkingLotMapRequest request, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Passes
    // -------------------------------------------------------------------------

    /// <summary>Returns pass allocations for a consumer. Pass <c>status=ACTIVE</c> for current passes.</summary>
    [Get("/authentication/v1/pass/passAllocations")]
    Task<List<PassAllocation>> GetPassAllocationsAsync([Query] int consumerId, [Query] string profileNames = "", [Query] string status = "ACTIVE", CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Sub-corporate
    // -------------------------------------------------------------------------

    /// <summary>Returns sub-corporate organisations associated with a parking location.</summary>
    [Get("/authentication/v1/subCorporate/subCorporatesByParking/{parkingId}")]
    Task<List<SubCorporate>> GetSubCorporatesByParkingAsync(int parkingId, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Contracted parking
    // -------------------------------------------------------------------------

    /// <summary>Returns contracted parkings for a consumer.</summary>
    [Get("/authentication/v1/consumers/contractedParking")]
    Task<List<ContractedParking>> GetConsumerContractedParkingsAsync([Query] int consumerId, CancellationToken ct = default);

    /// <summary>Returns contracted parkings for a company (paginated).</summary>
    [Get("/authentication/v1/parking/contractedParkings")]
    Task<List<ContractedParking>> GetCompanyContractedParkingsAsync([Query] int companyId, [Query] int pageNumber = 1, [Query] int pageSize = 10, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // App configuration (no auth required)
    // -------------------------------------------------------------------------

    /// <summary>Returns company config attributes. Use <c>attributeType=COMPANY_CONFIG</c>. PUBLIC — no bearer token.</summary>
    [Get("/authentication/v1/company/{companyId}/companyAttribute")]
    [Headers("X-Apcoa-NoAuth: true")]
    Task<List<CompanyAttribute>> GetCompanyAttributesAsync(int companyId, [Query] string attributeType, [Query] string entityId, [Query] string entityType, CancellationToken ct = default);

    /// <summary>
    /// Returns the app config (feature flags, settings, and the parking-data decryption key) for a company.
    /// Use <c>version=v3</c>. PUBLIC — must be called without a bearer token (see <c>X-Apcoa-NoAuth</c>);
    /// sending one yields HTTP 419.
    /// </summary>
    [Get("/authentication/v1/dashboard/tenant/appConfigByCompanyId/{companyId}")]
    [Headers("X-Apcoa-NoAuth: true")]
    Task<object> GetAppConfigAsync(int companyId, [Query] string version = "v3", CancellationToken ct = default);

    /// <summary>Returns localisation strings for the given ID and epoch timestamp. PUBLIC — no bearer token.</summary>
    [Get("/authentication/v1/translation/{id}")]
    [Headers("X-Apcoa-NoAuth: true")]
    Task<object> GetTranslationsAsync(int id, [Query] long epochTime, CancellationToken ct = default);
}
