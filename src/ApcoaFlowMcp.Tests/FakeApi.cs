using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using NSubstitute;

namespace ApcoaFlowMcp.Tests;

/// <summary>A substitute <see cref="IApcoaFlowApi"/> with one default vehicle on the account.</summary>
internal static class FakeApi
{
    public const string LicensePlate = "ABC12345";
    public const string VehicleDescription = "Test Car";

    public static IApcoaFlowApi Create()
    {
        var api = Substitute.For<IApcoaFlowApi>();
        api.GetVehiclesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Vehicle { Id = "b6e130b7-78d8-41b8-a4fe-667578fe3ff0", LicensePlate = LicensePlate, Description = VehicleDescription, IsDefault = true },
        ]);
        return api;
    }
}
