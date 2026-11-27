using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ApcoaFlowMcp.Prompts;

/// <summary>
/// User-invoked shortcuts for everyday parking flows. MCP prompts never run on their own — the user picks one (a slash
/// command in Claude Code, the "+" menu in claude.ai). Each returns a user message telling the model which tools to
/// call. They are defaults: the closing sentence asks the model to prefer the user's own instructions or skills (a hint
/// only — nothing enforces it), and <c>Prompts__Enabled=false</c> removes them. Every start goes through a free-only
/// tool, so a prompt can never charge the account.
/// </summary>
[McpServerPromptType]
public sealed class ApcoaFlowPrompts
{
    internal const string PlateArgumentDescription =
        "Registered license plate (see get_registered_cars). Leave empty to use the account's default car.";

    internal const string Defer =
        "These are default steps: if I have my own instructions or a skill for APCOA parking, follow those preferences instead.";

    private static string PickPlate(string? plate) =>
        string.IsNullOrWhiteSpace(plate)
            ? "Call get_registered_cars and use the car marked [default] (if there is none, ask me which plate)."
            : $"Use the plate \"{plate}\" (check it against get_registered_cars).";

    [McpServerPrompt(Name = "park_free_here", Title = "Park here for free")]
    [Description("Start a FREE parking session at the closest APCOA parking to where you are. Never starts a paid session.")]
    public static string ParkFreeHere([Description(PlateArgumentDescription)] string? plate = null) =>
        $"""
        Start a free APCOA parking session where I am now.
        1. {PickPlate(plate)}
        2. Use my current location (ask me for it if you don't have it). Tell me the closest parking and the plate, and
           ask me to confirm before starting.
        3. Start it with start_free_session_current_location. If it reports the parking isn't free, show me the price
           options and stop — don't start a paid session unless I explicitly ask for one.
        4. Report the parking, the session id and until when it's free.
        {Defer}
        """;

    [McpServerPrompt(Name = "park_free_at", Title = "Park for free at…")]
    [Description("Start a FREE parking session at a parking you name (e.g. one you used before). Never starts a paid session.")]
    public static string ParkFreeAt(
        [Description("The parking's name as APCOA shows it, e.g. \"Carrefour Tarnowskie Góry\" — your recent parkings are suggested.")] string parking,
        [Description(PlateArgumentDescription)] string? plate = null) =>
        $"""
        Start a free APCOA parking session at "{parking}".
        1. {PickPlate(plate)}
        2. Find the parking id: if get_session_history shows a recent session at "{parking}", use its parking; otherwise
           call find_parking with a name filter (ask me roughly where it is if you need a location). If more than one
           parking could match, ask me which.
        3. Tell me the parking and the plate, and ask me to confirm before starting.
        4. Start it with start_free_session. If it reports the parking isn't free, show me the price options and stop —
           don't start a paid session unless I explicitly ask for one.
        {Defer}
        """;

    [McpServerPrompt(Name = "end_parking", Title = "End my parking")]
    [Description("End the parking session that is running now, after confirmation, and report the final cost.")]
    public static string EndParking() =>
        $"""
        End my APCOA parking session.
        1. Call get_active_sessions. If nothing is running, just tell me so.
        2. Show me the running session (parking, plate, since when) and ask me to confirm before ending it.
        3. Only after I confirm, call end_session with its id and report the final cost.
        {Defer}
        """;

    [McpServerPrompt(Name = "parking_costs", Title = "What did parking cost")]
    [Description("Sum up what your APCOA parking cost over a period (default: this month).")]
    public static string ParkingCosts(
        [Description("The period, e.g. \"this month\", \"last month\", \"this week\". Default: this month.")] string? period = null) =>
        $"""
        Tell me what my APCOA parking cost {(string.IsNullOrWhiteSpace(period) ? "this month" : period)}.
        1. Call get_session_history. This is read-only — don't start or end anything.
        2. Keep only the sessions in that period, then reply with the total, the number of sessions, and a short line
           per session (date, parking, cost). Mention free sessions as free.
        3. The history only returns the most recent sessions: if the oldest one returned is still inside the period,
           say the total may be incomplete.
        {Defer}
        """;
}
