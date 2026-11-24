# APCOA Flow MCP — Tools Reference

Detailed reference for the MCP tools exposed by this server. Source of truth is
`src/ApcoaFlowMcp/Tools/ApcoaFlowTools.cs` — **keep this file in sync when a tool's name,
parameters, behaviour, or output format changes.**

All tools return a plain-text string (one or more lines). Units are user-facing: **PLN** for cost,
**minutes** for duration, **metres** for distance.

---

## `find_parking`

Find APCOA parkings near a coordinate, optionally filtered by name (e.g. a retail brand). Backed by
the offline parking dataset (no live search API exists). Nearest first.

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `latitude` | number | yes | — | Search-centre latitude (decimal degrees). |
| `longitude` | number | yes | — | Search-centre longitude (decimal degrees). |
| `radiusMeters` | number | no | `2000` | Search radius in metres. |
| `nameContains` | string | no | — | Name filter, case- and accent-insensitive (e.g. `Carrefour`). |

**Output** — one line per parking: `[id] name - address (distance m)`, or a not-found message.

```
[10395] CHU HALA Tarnowskie Góry ul. Nakielska 1 (86 m)
[8331] Aldi Tarnowskie Góry ul. Sobieskiego 3 (139 m)
[616319] Tarnowskie Góry ul. Oświęcimska (237 m)
[9882] Carrefour Tarnowskie Góry ul. Kościuszki 5 (600 m)
```
```
No parkings found for the given location/filter.
```

---

## `get_registered_cars`

List the licence plates registered on the account.

**Inputs** — none.

**Output** — one line per car: `plate [default] - status`.

```
ABC 123DE [default] - ACTIVE_ENABLED
```
```
No registered cars on the account.
```

---

## `get_active_sessions`

Return the currently running session(s). Call before starting to avoid duplicates.

**Inputs** — none.

**Output** — one line per active session, or a message.

```
[active] [84214483] Tarnowskie Góry ul. Oświęcimska plate:ABC 123DE 0.00 PLN since:2026-11-24 14:09:15Z
```
```
No active parking session.
```

---

## `get_session_history`

Return recent completed sessions, most recent first.

**Inputs** — none.

**Output** — one line per session (same format as active, with `[completed]`).

```
[completed] [84214483] Tarnowskie Góry ul. Oświęcimska plate:ABC 123DE 0.00 PLN since:2026-11-24 14:09:15Z
```
```
No past parking sessions.
```

---

## `start_session`

Start a session at a parking (by id, from `find_parking`) for a registered plate. **May cost money.**
Resolves the lot, tariff, vehicle, payment method and profile automatically.

- **Free** sessions start immediately.
- **Paid** sessions do **not** start on the first call — the tool returns the cost and requires a
  second call with `confirmedCostPln` set to **exactly** that cost (any mismatch is refused).
- A paid cost above `MaxSessionCostPln` (default 100 zł) is refused — use `start_expensive_session`.
- To guarantee a free-only start, use `start_free_session` instead.
- **Free-time promotions are auto-applied.** If the parking has an active free-time promotion, it is
  applied to the quote (so you are only charged for time beyond the free window), and the
  confirmation message says how many minutes are free. A one-use free promo is never partly wasted:
  if `durationMinutes` is shorter than the free window, it is rounded **up** to the full free window
  (capped by the lot's max duration).

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `parkingId` | int | yes | — | Parking id from `find_parking`. |
| `licensePlate` | string | yes | — | Registered plate, e.g. `ABC 123DE`. |
| `durationMinutes` | int | no | 60 | Desired duration in minutes. Rounded up to the full free window when a shorter value is given and a free-time promotion applies. |
| `confirmedCostPln` | decimal | no | — | Exact cost being confirmed (paid, second call only). |

**Output**

First call, free — starts immediately:
```
Parking: Tarnowskie Góry ul. Oświęcimska [616319]. Started a free session for 60 min on parking 616319. Session id: 84214483, status: ACTIVE.
```
First call, paid — returns the cost, does not start:
```
Parking: Carrefour Tarnowskie Góry ul. Kościuszki 5 [9882]. This is a PAID session: 6.00 PLN for 180 min. Re-invoke with confirmedCostPln=6.00 to start.
```
First call, paid but a free-time promotion applies — the free window is spelled out:
```
Parking: Carrefour Tarnowskie Góry ul. Kościuszki 5 [9882]. This is a PAID session: 3.00 PLN for 180 min. Re-invoke with confirmedCostPln=3.00 to start. A 120-min free promotion ('FREE120') is applied, so you are only charged for the time beyond it.
```
Second call with matching `confirmedCostPln=6.00` — starts:
```
Parking: Carrefour Tarnowskie Góry ul. Kościuszki 5 [9882]. Started a 6.00 PLN session for 180 min on parking 9882. Session id: 84215000, status: ACTIVE.
```
Over the cost cap:
```
Parking: … [10584]. Cost 150.00 PLN exceeds the limit of 100.00 PLN. Use start_expensive_session to override.
```

---

## `start_free_session`

Start a **free-only** session at a parking (by id). Auto-applies an active free-time promotion and
starts only if the session is actually free (cost 0); if it would cost anything, it is **refused** and
nothing is started. Never incurs a charge — the safe default for "park free at this parking". The
session always fills the whole free window: a `durationMinutes` shorter than the free window is
rounded up to it (capped by the lot's max duration), and a longer one is refused.

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `parkingId` | int | yes | — | Parking id from `find_parking`. |
| `licensePlate` | string | yes | — | Registered plate. |
| `durationMinutes` | int | no | full free window | Desired duration in minutes. A shorter value is rounded up to the full free window; longer than the window is refused. |

**Output**
```
Parking: Tarnowskie Góry ul. Oświęcimska [616319]. Started a free session for 120 min on parking 616319. Session id: 84214483, status: ACTIVE.
```
Not free (refused, nothing started):
```
Parking: Carrefour … [9882]. This parking is not free for 180 min (cost 6.00 PLN) even with promotions.
```

---

## `start_session_current_location`

Like `start_session` (**may cost money**), but finds the closest parking to a GPS location within a
radius and starts there. Use when the user says "park here / where I am". Same free/paid/confirm
behaviour. To guarantee a free-only start here, use `start_free_session_current_location`.

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `latitude` | number | yes | — | Current latitude (decimal degrees). |
| `longitude` | number | yes | — | Current longitude (decimal degrees). |
| `licensePlate` | string | yes | — | Registered plate. |
| `durationMinutes` | int | no | 60 | Desired duration in minutes. Rounded up to the full free window when a shorter value is given and a free-time promotion applies. |
| `radiusMeters` | number | no | `CurrentLocationRadiusMeters` (300) | Max distance to search. |
| `confirmedCostPln` | decimal | no | — | Exact cost being confirmed (paid, second call only). |

**Output** — same as `start_session`, and the chosen parking's distance is included:
```
Parking: Tarnowskie Góry ul. Oświęcimska [616319], 237 m away. Started a free session for 60 min on parking 616319. Session id: 84214483, status: ACTIVE.
```
No parking in range:
```
No parking found within 300 m of your location.
```

---

## `start_free_session_current_location`

Free-only variant of `start_session_current_location`: finds the closest parking within the radius and
starts only if the session is actually free; refused (nothing started) if it would cost anything. Never
incurs a charge.

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `latitude` | number | yes | — | Current latitude (decimal degrees). |
| `longitude` | number | yes | — | Current longitude (decimal degrees). |
| `licensePlate` | string | yes | — | Registered plate. |
| `durationMinutes` | int | no | full free window | Desired duration in minutes. A shorter value is rounded up to the full free window; longer than the window is refused. |
| `radiusMeters` | number | no | `CurrentLocationRadiusMeters` (300) | Max distance to search. |

**Output** — same shape as `start_free_session`, with the distance included.
```
Parking: Tarnowskie Góry ul. Oświęcimska [616319], 237 m away. Started a free session for 120 min on parking 616319. Session id: 84214483, status: ACTIVE.
```

---

## `start_expensive_session`

Identical to `start_session` but **bypasses the maximum-cost cap**. Exposed as a separate tool so a
host can allow/deny or require extra confirmation for over-cap sessions independently. Still uses the
two-step exact-cost confirmation for paid sessions, auto-applies free-time promotions, and fills the
free window the same way.

**Inputs** — `parkingId`, `licensePlate`, `durationMinutes?`, `confirmedCostPln?` (same types/defaults
as `start_session`; no `onlyFree`).

**Output** — same format as `start_session` (never blocked by the cap).

---

## `end_session`

End an active session by id (from `get_active_sessions`). Returns the final cost.

**Inputs**

| Name | Type | Required | Description |
|---|---|---|---|
| `sessionId` | long | yes | Session id from `get_active_sessions`. |

**Output**
```
Ended session 84214483: 0.00 PLN at Tarnowskie Góry ul. Oświęcimska.
```
```
Session 84214483 was ended (no completed record returned to confirm cost).
```

---

## Configuration affecting tools

| Setting (`ApcoaFlow` section) | Default | Effect |
|---|---|---|
| `MaxSessionCostPln` | `100` | Cap enforced by `start_session`; `start_expensive_session` bypasses it. |
| `CurrentLocationRadiusMeters` | `300` | Default search radius for `start_session_current_location`. |

See `README.md` for how to override these (env / appsettings) and `docs/API-NOTES.md` for the
underlying API behaviour.
