# MCP tools

The human-facing reference for the server's tools: names, inputs, example outputs. Update it in the same change
whenever a tool is added, renamed, or changes its parameters, behaviour or output. Tool names follow the APCOA Flow
API's naming (vehicles, spaces, sessions).

The 2.0.0 tool set is being rebuilt around the current APCOA Flow API; tools are added here as they land.

All tool errors come back as `"isError": true` with a plain message, e.g.:

- `APCOA rejected the account's sign-in (the password may have changed). Reconnect this APCOA connector in the AI
  client's settings (disconnect and connect it again), or update the credentials it uses, then retry.`
- `APCOA is temporarily unavailable (HTTP 503). Try again in a moment.`

## `get_vehicles`

The vehicles (license plates) on the account. Read-only, idempotent. No inputs.

Output: one line per vehicle, `plate [default] - description` (the description is the user's own label from the
APCOA Flow app, left out when empty):

```
ABC12345 [default] - Test Car
```

With no vehicles: `No vehicles on the account.`
