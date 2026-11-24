namespace ApcoaFlowMcp;

public static class McpInstructions
{
    public const string Text =
        """
        APCOA Flow parking sessions. Typical flow: find_parking → start_session → end_session.
        - Only one session can be active per account. You do not need to check first: if one is already
          running, start_session refuses and reports the existing session (here or elsewhere) with the
          end_session id and time remaining, so the user can keep it or end it. Use get_active_sessions
          to inspect running sessions on demand.
        - Always confirm parking and license plate with the user before starting a session.
        - Use get_session_history to look up past sessions.
        """;
}
