using ApcoaFlowApiClient.Api;
using ApcoaFlowApiClient.Models;
using ApcoaFlowMcp.Apcoa;
using NSubstitute;
using static ApcoaFlowMcp.Tests.FakeApi;

namespace ApcoaFlowMcp.Tests;

/// <summary>"until" — an end time instead of a duration. The fake clock is 2026-11-27 14:00 UTC = 15:00 in Warsaw.</summary>
public class UntilTests
{
    private static readonly TimeProvider Clock = new FixedTime(Now);

    [Test]
    [Arguments("18:00", "2026-11-27T17:00:00Z")]     // later today
    [Arguments("8:00", "2026-11-28T07:00:00Z")]      // already past today → tomorrow morning
    [Arguments("15.30", "2026-11-27T14:30:00Z")]
    [Arguments("2026-11-28 09:30", "2026-11-28T08:30:00Z")]
    [Arguments("2026-11-28T09:30", "2026-11-28T08:30:00Z")]
    [Arguments("2026-11-28T09:30:00+01:00", "2026-11-28T08:30:00Z")]
    public async Task End_times_read_in_Polish_time(string text, string expectedUtc)
    {
        await Assert.That(EndTime.TryParse(text, Clock, out var end, out _)).IsTrue();
        await Assert.That(end).IsEqualTo(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    [Arguments("2026-11-27 10:00", "already past")]
    [Arguments("soon", "isn't a time")]
    public async Task Past_or_unreadable_end_times_are_refused(string text, string reason)
    {
        await Assert.That(EndTime.TryParse(text, Clock, out _, out var error)).IsFalse();
        await Assert.That(error).Contains(reason);
    }

    [Test]
    public async Task GetPrice_until_quotes_the_minutes_to_that_time()
    {
        var api = Create();
        api.GetSpaceAsync("p", Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>()).Returns(ReadToolTests.Curve("p", "Chorzów", [3m, 7m, 11m]));
        Quotes(api, minutes => minutes <= 60 ? 3m : 7m);

        var text = await FakeApi.Tools(api).GetPriceAsync("p", LicensePlate, until: "16:30");

        await Assert.That(text).StartsWith($"Chorzów, {LicensePlate}, 1 h 30 min from now (until 16:30): 7.00 PLN");
        await api.Received().GetPriceAsync(Arg.Is<PriceQuery>(q => q != null && q.Duration == 90 * 60), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetPrice_needs_minutes_or_until_but_not_both()
    {
        var tools = FakeApi.Tools(Create());

        await Assert.That(await tools.GetPriceAsync("p", LicensePlate)).Contains("Give the length of the stay");
        await Assert.That(await tools.GetPriceAsync("p", LicensePlate, 30, "18:00")).Contains("not both");
        await Assert.That(await tools.GetPriceAsync("p", LicensePlate, until: "lunch")).Contains("isn't a time");
    }

    [Test]
    public async Task A_paid_start_until_a_time_quotes_it_and_asks_to_repeat_the_same_until()
    {
        var api = ForStarts(minutes => minutes / 60m);

        var text = await FakeApi.Tools(api).StartSessionAsync(ParkingId, ParkingName, LicensePlate, until: "17:00");

        await Assert.That(text).Contains("2 h (until 17:00) at");
        await Assert.That(text).Contains("call again with until=\"17:00\"");
        await api.DidNotReceive().StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await api.Received().GetPriceAsync(Arg.Is<PriceQuery>(q => q != null && q.Duration == 120 * 60), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task A_start_with_both_minutes_and_until_starts_nothing()
    {
        var api = ForStarts(_ => 0m);

        var text = await FakeApi.Tools(api).StartFreeSessionAsync(ParkingId, ParkingName, LicensePlate, 30, "18:00");

        await Assert.That(text).Contains("not both");
        await api.DidNotReceive().StartParkingAsync(Arg.Any<StartParkingRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_until_adds_the_minutes_from_the_current_end()
    {
        var api = ForStarts(_ => 0m);
        var end = Now.AddMinutes(30); // 15:30 in Warsaw
        Session S(DateTimeOffset until) => new() { Id = "s1", StartDate = Now, EndDate = until, SessionCanBeExtended = true,
            Space = new SessionSpace { Id = ParkingId, Name = ParkingName }, Vehicle = new SessionVehicle { LicensePlate = LicensePlate } };
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([S(end)], [S(end.AddMinutes(30))]);
        api.GetUpdatedPriceAsync("s1", 1800, Arg.Any<CancellationToken>())
            .Returns(new PriceQuote { TotalPrice = "PLN 0.00", EndDate = end.AddMinutes(30) });

        var text = await FakeApi.Tools(api).ExtendSessionAsync("s1", ParkingName, until: "16:00");

        await Assert.That(text).StartsWith("Extended:");
        await api.Received(1).GetUpdatedPriceAsync("s1", 1800, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Extend_until_a_time_the_session_already_reaches_changes_nothing()
    {
        var api = ForStarts(_ => 0m);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([new Session { Id = "s1", StartDate = Now, EndDate = Now.AddHours(3),
            SessionCanBeExtended = true, Space = new SessionSpace { Id = ParkingId, Name = ParkingName } }]);

        var text = await FakeApi.Tools(api).ExtendSessionAsync("s1", ParkingName, until: "16:00");

        await Assert.That(text).Contains("already runs until 18:00");
        await api.DidNotReceive().GetUpdatedPriceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Until_is_read_in_the_parkings_time_zone()
    {
        // A UK parking: 18:00 there is 18:00 UTC in November, 4 h after the fake clock (not 3 h as in Warsaw).
        var api = Create();
        var london = ReadToolTests.Curve("uk", "London Bridge", [3m]);
        london.CountryCode = "GB";
        api.GetSpaceAsync("uk", Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>()).Returns(london);
        Quotes(api, _ => 3m);

        var text = await FakeApi.Tools(api).GetPriceAsync("uk", LicensePlate, until: "18:00");

        await Assert.That(text).Contains("4 h from now (until 18:00 (Europe/London time))");
        await Assert.That(ParkingTime.Zone("PL").Id).IsEqualTo("Europe/Warsaw");
        await Assert.That(ParkingTime.Zone(null).Id).IsEqualTo("Europe/Warsaw");
    }

    [Test]
    public async Task Running_sessions_show_their_parkings_local_time()
    {
        // The API gives session times in UTC; a UK parking's session reads in London time (UTC in November).
        var api = Create();
        var details = new SpaceDetails { Id = "uk-park", Name = "London Bridge", CountryCode = "GB" };
        api.GetSpaceAsync("uk-park", Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>()).Returns(details);
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([new Session
        {
            Id = "s1", StartDate = Now, EndDate = Now.AddHours(2), Space = new SessionSpace { Id = "uk-park", Name = "London Bridge" },
            Vehicle = new SessionVehicle { LicensePlate = LicensePlate },
        }]);

        var text = await FakeApi.Tools(api).GetActiveSessionsAsync();

        await Assert.That(text).Contains("from 14:00 (Europe/London time) until 16:00 (Europe/London time)");
    }

    [Test]
    public async Task A_parking_seen_in_a_search_needs_no_lookup_for_its_time_zone()
    {
        var api = Create();
        api.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>()).Returns(new SearchResponse
        {
            Spaces = [new SearchSpace { Id = "dk-park", Name = "Copenhagen Central", CountryCode = "DK" }],
        });
        api.GetSessionsAsync(Arg.Any<CancellationToken>()).Returns([new Session
        {
            Id = "s1", StartDate = Now, EndDate = Now.AddHours(1), Space = new SessionSpace { Id = "dk-park", Name = "Copenhagen Central" },
            Vehicle = new SessionVehicle { LicensePlate = LicensePlate },
        }]);

        await FakeApi.Tools(api).FindParkingsAsync("Copenhagen");
        api.ClearReceivedCalls();
        var text = await FakeApi.Tools(api).GetActiveSessionsAsync();

        await Assert.That(text).Contains("from 15:00 (Europe/Copenhagen time) until 16:00 (Europe/Copenhagen time)");
        await api.DidNotReceive().GetSpaceAsync("dk-park", Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>());
    }
}
