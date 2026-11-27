namespace ApcoaFlowMcp;

public static class McpInstructions
{
    public const string Text =
        """
        APCOA Flow parking. A parking is the place, a session is parking there. Typical flow:
        find_parkings (by name, brand like "Biedronka", town, location code and/or position) → get_parking_details
        or get_price when needed → start_session (or start_free_session) → end_session.
        - Always confirm the parking and the license plate with the user before starting a session.
        - Free-only tools (start_free_session*) never charge. Paid starts and extensions return the price first;
          call again with confirmedCostPln only after the user agreed to that price. Use start_expensive_session
          only when the user explicitly accepts a cost above the limit.
        - Pass the parking's name (parkingName) with every tool that takes a parking or session id, so the
          permission prompt shows which parking it is.
        - Paid sessions are charged to the account's default payment method, which must be a saved card.
        - A plate can have one running session; a start refuses and names it. Use get_active_sessions to check.
        - Not supported: booking ahead, favourites, adding vehicles or payment methods, promo codes, EV charging,
          permits/passes/subscriptions, receipts or invoices. Say so instead of searching for another tool.
        """;
}
