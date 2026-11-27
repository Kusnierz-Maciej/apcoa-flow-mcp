namespace ApcoaFlowMcp;

public static class McpInstructions
{
    /// <summary>Server instructions for the model; rewritten with the tools (port plan M2).</summary>
    public const string Text =
        """
        APCOA Flow parking. A parking is the place (find_parkings, get_parking, get_price); a session is parking
        there (get_active_sessions, get_session_history). Plates are the account's vehicles (get_vehicles).
        """;
}
