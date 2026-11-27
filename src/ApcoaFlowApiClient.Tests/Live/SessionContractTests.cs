using System.Net;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using Refit;

namespace ApcoaFlowApiClient.Tests.Live;

/// <summary>
/// The session contract, in ONE free cycle per run (a Biedronka's free time is once per 4 h per parking, so each start
/// counts): start → listed as running → a second start for the plate is refused → free extend → stop (204) → a second
/// stop is 204 too → extending a stopped session is refused → history has it, PLN 0.00. Starts only where a dated
/// quote for the vehicle says PLN 0.00 right before the start: Ostrów Wlkp. III, else the first Biedronka that's free
/// now; skipped (not failed) when there's none.
/// </summary>
[Category("LiveApi")]
[NotInParallel(LiveApi.Serial)]
public class SessionContractTests
{
    private const int Minutes = 15;

    [Before(HookType.Class)]
    public static Task SweepAtStart() => LiveApi.SweepOnceAtStartAsync();

    [After(HookType.Test)]
    public Task EndAfterEachTest() => LiveApi.EndActiveSessionsAsync("after a SessionContractTests test");

    [After(HookType.Class)]
    public static Task Sweep() => LiveApi.EndActiveSessionsAsync("after SessionContractTests");

    [Test]
    public async Task Free_session_lifecycle()
    {
        var api = LiveApi.Api;
        var vehicle = LiveApi.FirstRegisteredCar(await api.GetVehiclesAsync());
        var spaceId = await LiveApi.FindFreeParkingAsync(api, vehicle, 2 * Minutes); // covers the extend too
        if (spaceId is null)
            Skip.Test("No free parking right now (Ostrów is paid on Saturday 06–15 and no Biedronka quotes 0.00).");

        var card = (await api.GetPaymentMethodsAsync(spaceId)).Single(m => m.IsDefault);
        var start = DateTimeOffset.UtcNow;
        var request = new StartParkingRequest
        {
            SpaceId = spaceId!, LicensePlateId = vehicle.Id, PaymentMethodId = card.Id,
            StartDate = start, EndDate = start.AddMinutes(Minutes),
        };

        // Start: running at once, nothing pending.
        var started = await api.StartParkingAsync(request, Guid.NewGuid().ToString());
        await Assert.That(started.Id).IsNotNull();
        await Assert.That(started.NeedsPaymentAction).IsFalse().Because("a free start with a saved card needs no payment step");

        var running = (await api.GetSessionsAsync()).SingleOrDefault(s => s.Id == started.Id);
        await Assert.That(running).IsNotNull().Because("a started session is listed by GET sessions right away");
        await Assert.That(running!.Status).IsEqualTo("active");
        await Assert.That(running.Vehicle!.LicensePlate).IsEqualTo(vehicle.LicensePlate);
        await Assert.That(running.Space!.Id).IsEqualTo(spaceId);

        // A second start for the same plate: 400 ParkingSessionInvalidArgument.
        var duplicate = await Assert.That(async () => await api.StartParkingAsync(request, Guid.NewGuid().ToString())).Throws<ApiException>();
        await Assert.That(duplicate!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(ApcoaFlowApiError.TryRead(duplicate)?.Code).IsEqualTo(ApcoaFlowApiError.Codes.ParkingSessionInvalidArgument);

        // Free extend: the quote's endDate, then the same session runs longer.
        var extension = await api.GetUpdatedPriceAsync(started.Id!, Minutes * 60);
        if (extension.Total.IsZero)
        {
            await Assert.That(extension.EndDate).IsNotNull().Because("updatedPrice returns the new end");
            var answer = await api.UpdateParkingDurationAsync(new UpdateParkingDurationRequest
            {
                SessionId = started.Id!, NewEnd = extension.EndDate!.Value, PaymentMethodId = card.Id,
            });
            await Assert.That(answer.IsSuccessStatusCode).IsTrue();
            await Assert.That(answer.Content?.FailureReason).IsNull();
            var extended = (await api.GetSessionsAsync()).Single(s => s.Id == started.Id);
            await Assert.That(extended.EndDate!.Value).IsGreaterThan(running.EndDate!.Value).Because("the same session continues with the new end");
        }

        // Stop: 204; stopping again is 204 as well (silent success).
        await api.StopParkingAsync(started.Id!);
        await Assert.That((await api.GetSessionsAsync()).Any(s => s.Id == started.Id)).IsFalse();
        await api.StopParkingAsync(started.Id!);

        // Extending a stopped session: 400 ParkingSessionInvalidArgument.
        var lateExtend = await api.UpdateParkingDurationAsync(new UpdateParkingDurationRequest
        {
            SessionId = started.Id!, NewEnd = DateTimeOffset.UtcNow.AddMinutes(Minutes), PaymentMethodId = card.Id,
        });
        await Assert.That(lateExtend.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(ApcoaFlowApiError.TryRead((ApiException)lateExtend.Error!)?.Code).IsEqualTo(ApcoaFlowApiError.Codes.ParkingSessionInvalidArgument);

        // History: the session, billed at PLN 0.00 (may take a moment to settle).
        SessionsHistoryDetails? details = null;
        for (var attempt = 0; attempt < 5 && details is not { IsSessionActive: false }; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2));
            details = await api.GetSessionsHistoryDetailsAsync(started.Id!);
        }
        await Assert.That(details!.IsSessionActive).IsFalse();
        await Assert.That(Money.Parse(details.TotalPrice).IsZero).IsTrue().Because("the session was quoted free");
    }
}
