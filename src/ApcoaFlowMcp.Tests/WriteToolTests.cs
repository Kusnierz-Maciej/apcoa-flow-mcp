using System.Net;
using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Refit;
using static ApcoaFlowMcp.Tests.FakeApi;

namespace ApcoaFlowMcp.Tests;

public class WriteToolTests
{
    private static Task NoStart(IApcoaFlowApi api) =>
        api.DidNotReceive().StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

    [Test]
    public async Task Free_start_uses_the_default_card_the_minute_and_the_whole_free_window()
    {
        var api = ForStarts(minutes => minutes <= 90 ? 0m : 2m);
        api.GetSpaceAsync(ParkingId, Arg.Any<CancellationToken>()).Returns(new SpaceDetails { Id = ParkingId, Name = ParkingName, DefaultLengthOfStayInMinutes = 90 });

        var text = await FakeApi.Tools(api).StartFreeSessionAsync(ParkingId, ParkingName, "abc 12345");

        await Assert.That(text).StartsWith("Started:");
        await Assert.That(text).Contains("(free)");
        await api.Received(1).StartParkingAsync(Arg.Is<StartParkingRequest>(r =>
                r!.SpaceId == ParkingId && r.LicensePlateId == VehicleId && r.PaymentMethodId == CardId &&
                r.StartDate == Now && r.EndDate == Now.AddMinutes(90)),
            Arg.Is<string>(k => k!.Length == 36), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task A_plate_recognition_parking_that_cant_be_started_says_so()
    {
        var api = ForStarts(_ => 0m);
        api.GetSpaceAsync(ParkingId, Arg.Any<CancellationToken>()).Returns(new SpaceDetails
        {
            Id = ParkingId, Name = "Lidl Poznań ul. Fabryczna 23", CtaButtonType = "none", SessionsStartAutomatically = true,
        });

        var text = await FakeApi.Tools(api).StartFreeSessionAsync(ParkingId, "Lidl Poznań ul. Fabryczna 23", "ABC12345");

        await Assert.That(text).Contains("recognises plates and starts and ends sessions by itself");
        await NoStart(api);
    }

    [Test]
    public async Task Paid_start_first_returns_the_price_and_starts_nothing()
    {
        var api = ForStarts(_ => 5m, prepaymentMode: "none");

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 60);

        await Assert.That(text).Contains("1 h at Targowiska Miejskie Ostrów Wlkp. III for ABC12345 costs 5.00 PLN, charged at the end to visa ****4412.");
        await Assert.That(text).Contains("durationMinutes=60 and confirmedCostPln=5.00");
        await NoStart(api);
    }

    [Test]
    public async Task Paid_start_with_the_confirmed_cost_starts()
    {
        var api = ForStarts(_ => 5m);

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 60, confirmedCostPln: 5.00m);

        await Assert.That(text).StartsWith("Started:");
        await Assert.That(text).Contains("5.00 PLN, charged at start");
    }

    [Test]
    public async Task Paid_start_with_an_outdated_cost_starts_nothing()
    {
        var api = ForStarts(_ => 6m);

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 60, confirmedCostPln: 5m);

        await Assert.That(text).StartsWith("The price changed");
        await NoStart(api);
    }

    [Test]
    public async Task Over_the_cap_only_the_expensive_tool_starts()
    {
        var api = ForStarts(_ => 150m);

        var normal = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 60, confirmedCostPln: 150m);
        await Assert.That(normal).Contains("more than this server's limit of 100.00 PLN");
        await NoStart(api);

        var expensive = await FakeApi.Tools(ForStarts(_ => 150m)).StartExpensiveSessionAsync(ParkingId, ParkingName, "ABC12345", 60, confirmedCostPln: 150m);
        await Assert.That(expensive).StartsWith("Started:");
    }

    [Test]
    public async Task Free_only_at_a_paid_parking_starts_nothing_and_gives_the_price()
    {
        var api = ForStarts(_ => 3m);

        var text = await FakeApi.Tools(api).StartFreeSessionAsync(ParkingId, ParkingName, "ABC12345", 30);

        await Assert.That(text).Contains("isn't free for ABC12345 right now: 30 min costs 3.00 PLN");
        await NoStart(api);
    }

    [Test]
    public async Task Free_only_longer_than_the_free_window_starts_nothing()
    {
        var api = ForStarts(minutes => minutes <= 90 ? 0m : 2m);

        var text = await FakeApi.Tools(api).StartFreeSessionAsync(ParkingId, ParkingName, "ABC12345", 120);

        await Assert.That(text).Contains("free for ABC12345 only up to 1 h 30 min right now");
        await NoStart(api);
    }

    [Test]
    [Arguments("applePay", "saved")]
    [Arguments("visa", "hostedCheckout")]
    public async Task A_default_that_isnt_a_saved_card_refuses_a_paid_start(string type, string flow)
    {
        var api = ForStarts(_ => 5m, defaultMethod: new PaymentMethod
        {
            Id = "x", Type = type, Identifier = "****1", IsDefault = true, Flow = flow, RequiredAction = "none", Availability = "available",
        });

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 30, confirmedCostPln: 5m);

        await Assert.That(text).Contains("Set a saved card as the default in the APCOA Flow app");
        await NoStart(api);
    }

    [Test]
    public async Task A_free_start_works_with_any_default_method_and_sends_its_id()
    {
        var api = ForStarts(_ => 0m, defaultMethod: new PaymentMethod
        {
            Id = "apple", Type = "applePay", Identifier = "****1", IsDefault = true, Flow = "saved", RequiredAction = "none", Availability = "available",
        });

        var text = await FakeApi.Tools(api).StartFreeSessionAsync(ParkingId, ParkingName, "ABC12345", 30);

        await Assert.That(text).StartsWith("Started:");
        await api.Received(1).StartParkingAsync(Arg.Is<StartParkingRequest>(r => r!.PaymentMethodId == "apple"), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task A_free_stay_longer_than_the_searched_day_starts_when_its_quote_is_free()
    {
        var api = ForStarts(_ => 0m);

        var text = await FakeApi.Tools(api).StartFreeSessionAsync(ParkingId, ParkingName, "ABC12345", 2000);

        await Assert.That(text).StartsWith("Started:");
    }

    [Test]
    public async Task A_start_accepted_but_not_confirmable_says_dont_start_again()
    {
        var api = ForStarts(_ => 5m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult(new List<Session>()),
            _ => throw new HttpRequestException("connection reset"));

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 60, confirmedCostPln: 5m);

        await Assert.That(text).Contains("couldn't confirm it afterwards. Don't start again");
        await api.Received(1).StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task An_unreadable_price_is_a_plain_tool_error()
    {
        var api = ForStarts(_ => 0m);
        api.GetPriceAsync(Arg.Any<PriceQuery>(), Arg.Any<CancellationToken>()).Returns(new PriceQuote { TotalPrice = "PLN 5,00" });

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 60));

        await Assert.That(ex!.Message).Contains("price in a format this server can't read");
        await NoStart(api);
    }

    [Test]
    public async Task A_running_session_for_the_plate_refuses_and_names_it()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Session { Id = "running-1", StartDate = Now.AddMinutes(-5), EndDate = Now.AddMinutes(25),
                Space = new SessionSpace { Name = "Biedronka Opole" }, Vehicle = new SessionVehicle { LicensePlate = "ABC12345" } },
        ]);

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 30);

        await Assert.That(text).Contains("already has a running session: [running-1] Biedronka Opole");
        await NoStart(api);
    }

    [Test]
    public async Task A_start_that_needs_a_payment_action_ends_whatever_began()
    {
        var api = ForStarts(_ => 5m);
        api.StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StartParkingResponse { ClientSecret = "secret", PendingId = "p" });

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 60, confirmedCostPln: 5m);

        await Assert.That(text).Contains("needs a confirmation step");
        await Assert.That(text).Contains("(new-session) was ended at once");
        await api.Received(1).StopParkingAsync("new-session", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task A_pending_paid_start_is_followed_until_it_completes()
    {
        // A paid pre-pay start answers {id: null, pendingId} and starts asynchronously (2026-11-27).
        var api = ForStarts(_ => 1m);
        api.StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StartParkingResponse { PendingId = "pend-1" });
        api.GetPendingStartAsync("pend-1", Arg.Any<CancellationToken>())
            .Returns(new PendingStartStatus { Status = "completed", SessionId = "new-session" });

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 15, confirmedCostPln: 1m);

        await Assert.That(text).StartsWith("Started: [new-session]");
        await Assert.That(text).Contains("1.00 PLN, charged at start");
        await api.DidNotReceive().StopParkingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task A_failed_pending_start_says_nothing_is_running()
    {
        var api = ForStarts(_ => 1m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        api.StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StartParkingResponse { PendingId = "pend-1" });
        api.GetPendingStartAsync("pend-1", Arg.Any<CancellationToken>())
            .Returns(new PendingStartStatus { Status = "failed", FailureReason = "card declined" });

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 15, confirmedCostPln: 1m);

        await Assert.That(text).Contains("didn't start the session");
        await Assert.That(text).Contains("card declined");
        await Assert.That(text).Contains("Nothing is running.");
    }

    [Test]
    public async Task A_start_answer_without_a_running_session_is_never_reported_as_started()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var text = await FakeApi.Tools(api).StartFreeSessionAsync(ParkingId, ParkingName, "ABC12345", 15);

        await Assert.That(text).Contains("isn't running yet");
    }

    [Test]
    public async Task Duplicate_start_error_from_the_API_comes_back_as_a_plain_message()
    {
        var api = ForStarts(_ => 0m);
        api.StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(await ApiException.Create(new HttpRequestMessage(HttpMethod.Post, "https://x/api/v1/sessions/startParking"), HttpMethod.Post,
                new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"code":"ParkingSessionInvalidArgument","message":"The session for the same license plate has already been started."}"""),
                }, new RefitSettings()));

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, "ABC12345", 30));

        await Assert.That(ex!.Message).Contains("The session for the same license plate has already been started.");
    }

    [Test]
    public async Task End_stops_then_reports_the_billed_cost()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Session { Id = "s1", StartDate = Now, SessionCanBeStopped = true, Space = new SessionSpace { Name = ParkingName },
                Vehicle = new SessionVehicle { LicensePlate = "ABC12345" } },
        ]);
        api.GetSessionsHistoryDetailsAsync("s1", Arg.Any<CancellationToken>())
            .Returns(new SessionsHistoryDetails { Id = "s1", TotalPrice = "PLN 1.00", IsSessionActive = false, State = "billed" });

        var text = await FakeApi.Tools(api).EndSessionAsync("s1", ParkingName);

        await api.Received(1).StopParkingAsync("s1", Arg.Any<CancellationToken>());
        await Assert.That(text).IsEqualTo($"Ended the session at {ParkingName} (ABC12345). Final cost: 1.00 PLN.");
    }

    [Test]
    public async Task End_waits_for_a_settled_bill_before_reporting_the_cost()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Session { Id = "s1", StartDate = Now, SessionCanBeStopped = true, Space = new SessionSpace { Name = ParkingName },
                Vehicle = new SessionVehicle { LicensePlate = "ABC12345" } },
        ]);
        // Right after a post-pay stop the total isn't there yet (2026-11-27), then it is.
        api.GetSessionsHistoryDetailsAsync("s1", Arg.Any<CancellationToken>()).Returns(
            new SessionsHistoryDetails { Id = "s1", TotalPrice = null, IsSessionActive = false },
            new SessionsHistoryDetails { Id = "s1", TotalPrice = "PLN 0.00", IsSessionActive = false, State = "billed" });

        var text = await FakeApi.Tools(api).EndSessionAsync("s1", ParkingName);

        await Assert.That(text).EndsWith("Final cost: 0.00 PLN.");
    }

    [Test]
    public async Task End_of_an_unknown_session_says_so_without_calling_stop()
    {
        var api = ForStarts(_ => 0m);

        var text = await FakeApi.Tools(api).EndSessionAsync("nope", ParkingName);

        await Assert.That(text).Contains("No running session has that id");
        await api.DidNotReceive().StopParkingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_free_sends_the_quoted_end_with_the_default_card()
    {
        var api = ForStarts(_ => 0m);
        var end = Now.AddMinutes(30);
        Session S(DateTimeOffset until) => new() { Id = "s1", StartDate = Now, EndDate = until, SessionCanBeExtended = true,
            Space = new SessionSpace { Id = ParkingId, Name = ParkingName }, Vehicle = new SessionVehicle { LicensePlate = "ABC12345" } };
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([S(end)], [S(end.AddMinutes(30))]);
        api.GetUpdatedPriceAsync("s1", 1800, Arg.Any<CancellationToken>())
            .Returns(new PriceQuote { TotalPrice = "PLN 0.00", EndDate = end.AddMinutes(30) });

        var text = await FakeApi.Tools(api).ExtendSessionAsync("s1", ParkingName, 30);

        await Assert.That(text).StartsWith("Extended:");
        await api.Received(1).UpdateParkingDurationAsync(Arg.Is<UpdateParkingDurationRequest>(r =>
            r!.SessionId == "s1" && r.NewEnd == end.AddMinutes(30) && r.PaymentMethodId == CardId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_counts_what_the_session_already_costs_against_the_cap()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Session { Id = "s1", StartDate = Now, EndDate = Now.AddMinutes(30), SessionCanBeExtended = true,
                Space = new SessionSpace { Id = ParkingId, Name = ParkingName },
                Details = [new SessionDetail { Title = "Total", SubTitle = "90", Footer = "PLN" }] },
        ]);
        api.GetUpdatedPriceAsync("s1", 3600, Arg.Any<CancellationToken>()).Returns(new PriceQuote { TotalPrice = "PLN 20.00" });

        var text = await FakeApi.Tools(api).ExtendSessionAsync("s1", ParkingName, 60, confirmedCostPln: 20m);

        await Assert.That(text).Contains("with the 90.00 PLN the session already costs");
        await api.DidNotReceive().UpdateParkingDurationAsync(Arg.Any<UpdateParkingDurationRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_done_but_not_rereadable_says_dont_extend_again()
    {
        var api = ForStarts(_ => 0m);
        var session = new Session { Id = "s1", StartDate = Now, EndDate = Now.AddMinutes(30), SessionCanBeExtended = true,
            Space = new SessionSpace { Id = ParkingId, Name = ParkingName } };
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult(new List<Session> { session }),
            _ => throw new HttpRequestException("502"));
        api.GetUpdatedPriceAsync("s1", 1800, Arg.Any<CancellationToken>()).Returns(new PriceQuote { TotalPrice = "PLN 0.00", EndDate = Now.AddMinutes(60) });

        var text = await FakeApi.Tools(api).ExtendSessionAsync("s1", ParkingName, 30);

        await Assert.That(text).Contains("Don't extend again");
        await Assert.That(text).Contains("isn't showing yet");
        await api.Received(1).UpdateParkingDurationAsync(Arg.Any<UpdateParkingDurationRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_accepted_but_the_new_end_never_shows_says_dont_extend_again()
    {
        var api = ForStarts(_ => 0m);
        var session = new Session { Id = "s1", StartDate = Now, EndDate = Now.AddMinutes(30), SessionCanBeExtended = true,
            Space = new SessionSpace { Id = ParkingId, Name = ParkingName } };
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([session]);
        api.GetUpdatedPriceAsync("s1", 1800, Arg.Any<CancellationToken>()).Returns(new PriceQuote { TotalPrice = "PLN 0.00", EndDate = Now.AddMinutes(60) });
        api.UpdateParkingDurationAsync(Arg.Any<UpdateParkingDurationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExtendAnswer(new UpdateParkingDurationResponse { Status = "pending", PendingId = "p1" }));

        var text = await FakeApi.Tools(api).ExtendSessionAsync("s1", ParkingName, 30);

        await Assert.That(text).Contains("accepted extending the session");
        await Assert.That(text).Contains("Don't extend again");
    }

    [Test]
    public async Task Extend_with_a_failure_reason_says_it_wasnt_extended()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Session { Id = "s1", StartDate = Now, EndDate = Now.AddMinutes(30), SessionCanBeExtended = true, Space = new SessionSpace { Id = ParkingId, Name = ParkingName } },
        ]);
        api.GetUpdatedPriceAsync("s1", 1800, Arg.Any<CancellationToken>()).Returns(new PriceQuote { TotalPrice = "PLN 0.00", EndDate = Now.AddMinutes(60) });
        api.UpdateParkingDurationAsync(Arg.Any<UpdateParkingDurationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExtendAnswer(new UpdateParkingDurationResponse { Status = "failed", FailureReason = "card declined" }));

        var text = await FakeApi.Tools(api).ExtendSessionAsync("s1", ParkingName, 30);

        await Assert.That(text).Contains("didn't extend the session");
        await Assert.That(text).Contains("card declined");
    }

    [Test]
    public async Task End_with_an_unreadable_bill_still_reports_the_session_ended()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Session { Id = "s1", StartDate = Now, SessionCanBeStopped = true, Space = new SessionSpace { Name = ParkingName },
                Vehicle = new SessionVehicle { LicensePlate = "ABC12345" } },
        ]);
        api.GetSessionsHistoryDetailsAsync("s1", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("500"));

        var text = await FakeApi.Tools(api).EndSessionAsync("s1", ParkingName);

        await Assert.That(text).Contains("Ended the session");
        await Assert.That(text).Contains("final cost isn't available yet");
    }

    [Test]
    public async Task Extend_paid_first_returns_the_price()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Session { Id = "s1", StartDate = Now, EndDate = Now.AddMinutes(30), SessionCanBeExtended = true, Space = new SessionSpace { Id = ParkingId, Name = ParkingName } },
        ]);
        api.GetUpdatedPriceAsync("s1", 3600, Arg.Any<CancellationToken>()).Returns(new PriceQuote { TotalPrice = "PLN 4.00" });

        var text = await FakeApi.Tools(api).ExtendSessionAsync("s1", ParkingName, 60);

        await Assert.That(text).Contains("costs 4.00 PLN");
        await Assert.That(text).Contains("confirmedCostPln=4.00");
        await api.DidNotReceive().UpdateParkingDurationAsync(Arg.Any<UpdateParkingDurationRequest>(), Arg.Any<CancellationToken>());
    }

    private static MapSpace Pin(string id, double lat, double lon, GeoBorder? outline = null) =>
        new() { Id = id, Name = $"Parking {id}", Coordinates = new Coordinates(lat, lon), GeoBorder = outline };

    [Test]
    public async Task Here_with_two_parkings_too_close_to_call_lists_them_and_starts_nothing()
    {
        var api = ForStarts(_ => 0m);
        api.GetSpacesByBoundingBoxAsync(Arg.Any<BoundingBoxQuery>(), Arg.Any<CancellationToken>())
            .Returns([Pin("a", 50.0001, 18.0), Pin("b", 50.0003, 18.0)]); // ~11 m and ~33 m

        var text = await FakeApi.Tools(api).StartFreeSessionHereAsync(50.0, 18.0, "ABC12345");

        await Assert.That(text).Contains("too close to tell apart by GPS");
        await Assert.That(text).Contains("[a] Parking a");
        await Assert.That(text).Contains("[b] Parking b");
        await NoStart(api);
    }

    [Test]
    public async Task Here_inside_one_outline_starts_there_even_with_a_closer_pin_nearby()
    {
        var api = ForStarts(_ => 0m);
        var square = new GeoBorder { Coordinates = [new(49.9999, 17.9999), new(49.9999, 18.0001), new(50.0001, 18.0001), new(50.0001, 17.9999), new(49.9999, 17.9999)] };
        api.GetSpacesByBoundingBoxAsync(Arg.Any<BoundingBoxQuery>(), Arg.Any<CancellationToken>())
            .Returns([Pin("pin", 50.00005, 18.0), Pin(ParkingId, 50.0008, 18.0, square)]);

        var text = await FakeApi.Tools(api).StartFreeSessionHereAsync(50.0, 18.0, "ABC12345");

        await Assert.That(text).StartsWith($"Nearest parking: Parking {ParkingId}.");
        await api.Received(1).StartParkingAsync(Arg.Is<StartParkingRequest>(r => r!.SpaceId == ParkingId), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Here_with_one_clearly_nearest_parking_starts_there()
    {
        var api = ForStarts(_ => 0m);
        api.GetSpacesByBoundingBoxAsync(Arg.Any<BoundingBoxQuery>(), Arg.Any<CancellationToken>())
            .Returns([Pin(ParkingId, 50.0002, 18.0), Pin("far", 50.0015, 18.0)]);

        var text = await FakeApi.Tools(api).StartFreeSessionHereAsync(50.0, 18.0, "ABC12345");

        await Assert.That(text).StartsWith($"Nearest parking: Parking {ParkingId} (22 m away).");
    }
}
