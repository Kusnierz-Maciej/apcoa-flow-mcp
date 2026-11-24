namespace ApcoaFlowMcp;

public static class McpInstructions
{
    public const string Text =
        """
        APCOA Flow parking sessions. Typical flow: get_parking_zones → start_session → end_session.
        - Use get_active_session to check whether a session is already running before starting a new one.
        - Always confirm zone and license plate with the user before starting a session.
        - Use session_history to look up past sessions.
        """;
}
