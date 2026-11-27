using ApcoaFlowApiClient.Models.Session;
using Refit;

namespace ApcoaFlowApiClient.Tests;

/// <summary>
/// Shared constants and housekeeping for live tests. Every <c>[Category("LiveApi")]</c> class must also carry
/// <c>[NotInParallel(LiveApi.Serial)]</c> (one real account, one client-side rate limit, a 403 lockout if we
/// hammer it, and a single active session per account), call <see cref="SweepOnceAtStartAsync"/> from a
/// <c>[Before(HookType.Class)]</c> hook and <see cref="EndActiveSessionsAsync"/> from an
/// <c>[After(HookType.Class)]</c> hook. Classes that start sessions also end them after every test.
/// </summary>
internal static class LiveApi
{
    public const string Serial = "LiveApi";

    /// <summary>
    /// Parking with unlimited free start/stop (Tarnowskie Góry, lot 41905, tariff 108014, 0.00 PLN base
    /// tariff). The only parking live tests may start a session on.
    /// </summary>
    public const int FreeParkingId = 616319;

    /// <summary>
    /// Starts a session, retrying while APCOA still holds the account's one-session slot for a session that was
    /// just ended: right after a stop, a start can get 424 LIMIT_EXCEEDED for a moment although no session is
    /// listed ACTIVE any more (observed 2026-11-27). Retries only in that case — a 424 with a session really
    /// ACTIVE is returned to the caller.
    /// </summary>
    public static async Task<T> StartWhenSlotFreeAsync<T>(IApcoaFlowApi api, Func<Task<T>> start)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await start();
            }
            catch (ApiException ex) when ((int)ex.StatusCode == 424 && attempt < 6)
            {
                var active = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 5 });
                if (active.Count > 0)
                    throw;
                Console.Error.WriteLine($"[live] start got 424 with nothing ACTIVE (slot not released yet); retry {attempt}…");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static readonly Lazy<Task> StartSweep = new(() => EndActiveSessionsAsync("start-of-run"));

    /// <summary>
    /// Crash recovery: ends sessions left ACTIVE (billable!) by a crashed or cancelled PREVIOUS run, before
    /// the first live test. Runs once per test run, from whichever live class starts first — later classes
    /// run after earlier ones have cleaned up (classes are serial), so sweeping again would be redundant.
    /// </summary>
    public static Task SweepOnceAtStartAsync() => StartSweep.Value;

    /// <summary>
    /// Lists ACTIVE sessions and stops each one — ANY active session on the test account, since a leftover
    /// keeps billing and blocks the next start (424 LIMIT_EXCEEDED). A 422 on stop means the session is
    /// already completing/finalized server-side — that counts as ended. Best-effort: failures are logged,
    /// never thrown, so cleanup can't mask the real test result.
    /// </summary>
    public static async Task EndActiveSessionsAsync(string phase)
    {
        try
        {
            var api = ApiClientFactory.Api;
            var active = await api.GetParkingSessionsAsync(new ParkingSessionV2Query { Status = "ACTIVE", PageSize = 20 });
            foreach (var s in active)
            {
                try
                {
                    await api.StopParkingSessionAsync(new StopParkingSessionRequest
                    {
                        ParkingSessionId = s.Id,
                        SessionCheckOutTime = DateTimeOffset.UtcNow,
                    });
                    Console.Error.WriteLine($"[live] {phase}: ended lingering ACTIVE session {s.Id} at {s.ParkingName}");
                }
                catch (ApiException ex) when ((int)ex.StatusCode == 422)
                {
                    Console.Error.WriteLine($"[live] {phase}: session {s.Id} already finalizing (HTTP 422), treated as ended.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[live] {phase}: FAILED to end session {s.Id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[live] {phase}: could not sweep active sessions: {ex.Message}");
        }
    }
}
