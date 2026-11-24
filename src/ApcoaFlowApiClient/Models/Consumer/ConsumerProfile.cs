namespace ApcoaFlowApiClient.Models.Consumer;

using System.Text.Json.Serialization;
using ApcoaFlowApiClient.Converters;

public class ConsumerProfile
{
    public int Id { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? EmailAddress { get; set; }
    public string? PhoneNumberCountryCode { get; set; }
    public string? PhoneNumber { get; set; }
    public string? ImageUrl { get; set; }
    public int IsPhoneVerified { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? CreatedAt { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? UpdatedAt { get; set; }
    public int IsEmailAddressVerified { get; set; }
    public int PromotionsEnabled { get; set; }
    public string? SignupMethod { get; set; }
    public string? Status { get; set; }
    public string? CountryCode { get; set; }
    public List<object> Addresses { get; set; } = [];
    public List<Vehicle> VehicleInformationResponses { get; set; } = [];
    public List<object> RfidOrders { get; set; } = [];
    public List<PaymentMethod> ConsumerUserPaymentMethods { get; set; } = [];
    public List<ConsumerIdentification> ConsumerUserIdentifications { get; set; } = [];

    /// <summary>
    /// Consumer profiles (e.g. PERSONAL/BUSINESS). The default profile's <see cref="ConsumerUserProfile.Id"/>
    /// is the <c>consumerProfileId</c> required when starting a session — it is NOT the top-level <see cref="Id"/>.
    /// </summary>
    public List<ConsumerUserProfile> ConsumerUserProfileList { get; set; } = [];

    /// <summary>The default profile id to send as <c>consumerProfileId</c> when starting a session.</summary>
    [JsonIgnore]
    public int? DefaultProfileId =>
        (ConsumerUserProfileList.FirstOrDefault(p => p.IsDefault == 1)
         ?? ConsumerUserProfileList.FirstOrDefault())?.Id;
}

public class ConsumerUserProfile
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int IsDefault { get; set; }
    public string? ProfileType { get; set; }
    public string? Status { get; set; }
}

public class Vehicle
{
    public int Id { get; set; }
    public string? CarMake { get; set; }
    public string? DisplayName { get; set; }
    public string VehicleNumber { get; set; } = string.Empty;
    public int IsDefault { get; set; }
    public string? VehicleType { get; set; }
    public int IsShared { get; set; }
    public int Shareable { get; set; }
    public string? IdentificationStatus { get; set; }
    public string? CountryLabel { get; set; }
    public string? Color { get; set; }
    public string? State { get; set; }
    public string? Model { get; set; }
    public string? FuelType { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? RentalExpiryTime { get; set; }
}

public class PaymentMethod
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? PaymentType { get; set; }
    public string? PaymentMode { get; set; }
    public string? PaymentStatus { get; set; }
    public int IsDefault { get; set; }
    public int IsAuthorized { get; set; }
    public string? DisplayItemOne { get; set; }
    public string? DisplayItemTwo { get; set; }
    public int PaymentGatewayId { get; set; }
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class ConsumerIdentification
{
    public int Id { get; set; }
    public string? Identifier { get; set; }
    public string? ExternalIdentifier { get; set; }
    public string? Status { get; set; }
    public int AccessTypeMasterId { get; set; }
    public int ConsumerUserId { get; set; }
}
