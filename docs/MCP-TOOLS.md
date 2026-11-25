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

Return the currently running session(s). You do **not** need to call this before starting — the start
tools detect an already-active session themselves and report it (see `start_session`) — but it is handy
to inspect or get a session id to end.

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
- **Paid** sessions do **not** start on the first call — the tool returns the **shortest available
  duration and its price** as the headline, lists the **other price options**, and (always) a
  **cancel/refund note** (whether stopping early refunds). To start, call again with **both**
  `durationMinutes` (the option you chose) and `confirmedCostPln` set to **exactly** that option's
  price (any mismatch is refused).
- A paid cost above `MaxSessionCostPln` (default 100 zł) is refused — use `start_expensive_session`.
- To guarantee a free-only start, use `start_free_session` instead.
- **Free-time promotions are auto-applied — but only when actually usable.** A promotion is
  **validated before it is trusted**. Only a *validated* promo expands the duration: a request shorter
  than the free window is rounded **up** to the full free window (capped by the lot's max), so a
  one-use coupon is never partly wasted, and the confirmation says how many minutes are free. If the
  promotion fails validation (expired, used-up, on cooldown, not valid for this account) it is
  **dropped** and your **exact requested duration** is quoted at the normal paid price. (The price
  simulator alone reports such a promo as free even though the real start would charge — e.g. the
  Carrefour "every 4 h, 2 h free" coupon quotes free but `promotion/validate` returns 422 and the
  start charges — so validation, not the simulator, decides.)
- The **price tiers come from the lot's tariff grid** (`GET /tariff`): a FLAT first block plus
  INCREMENTAL per-block charges, capped by any daily maximum. This is why a short request is not
  quoted at a longer tier's price.
- **One session at a time.** If a session is already active (the backend allows only one per account),
  the start is refused and the tool reports the existing session — whether it is at **this** parking
  (you can keep using it, or end it and start fresh) or **elsewhere** — including how long it has left
  and the `end_session(<id>)` call to end it. You do not need to call `get_active_sessions` first.

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `parkingId` | int | yes | — | Parking id from `find_parking`. |
| `licensePlate` | string | yes | — | Registered plate, e.g. `ABC 123DE`. |
| `durationMinutes` | int | no | server default | Desired duration in minutes. Omitting it uses the server default (60 min unless changed). Only rounded up to a free-time promotion's window when that promotion **validates as usable**; otherwise your exact requested duration is quoted. On a paid confirming call, set this to the chosen option's duration. |
| `confirmedCostPln` | decimal | no | — | Exact cost being confirmed (the chosen option's price; paid, second call only). |

**Output**

First call, free — starts immediately:
```
Parking: Tarnowskie Góry ul. Oświęcimska [616319]. Started a free session for 60 min on parking 616319. Early stop: allowed, but NO refund — the remaining paid time is forfeited. Session id: 84214483, status: ACTIVE.
```
First call, paid — returns the shortest option + the other tiers + the cancel/refund note, does not start:
```
Parking: Carrefour Tarnowskie Góry ul. Kościuszki 5 [9882]. This is a PAID session. Shortest option: 60 min = 2.00 PLN. Re-invoke with durationMinutes=60 and confirmedCostPln=2.00 to start. Other options: 120 min = 6.00 PLN. Early stop: allowed, but NO refund — the remaining paid time is forfeited.
```
First call, paid but a validated free-time promotion applies — the free window is spelled out:
```
Parking: … [9999]. This is a PAID session. Shortest option: 180 min = 3.00 PLN. Re-invoke with durationMinutes=180 and confirmedCostPln=3.00 to start. A 120-min free promotion ('FREE120') is applied, so you are only charged for the time beyond it. Other options: 240 min = 7.00 PLN. Early stop: allowed, with a refund.
```
Second call with matching `durationMinutes=120` and `confirmedCostPln=6.00` — starts:
```
Parking: Carrefour Tarnowskie Góry ul. Kościuszki 5 [9882]. Started a 6.00 PLN session for 120 min on parking 9882. Early stop: allowed, but NO refund — the remaining paid time is forfeited. Session id: 84215000, status: ACTIVE.
```
Over the cost cap:
```
Parking: … [10584]. Cost 150.00 PLN for 240 min exceeds the limit of 100.00 PLN. Use start_expensive_session to override. Other options: … Early stop: …
```
A session is already active at this parking (keep it or end it):
```
Parking: Tarnowskie Góry ul. Oświęcimska [616319]. You already have an active session at THIS parking (session 84214483 at 'Tarnowskie Góry ul. Oświęcimska' [616319], plate ABC 123DE, 25 min left). It is already running here — you can keep using it, or end it with end_session(84214483) and start a fresh one.
```
A session is already active somewhere else (end it, then retry):
```
Parking: Carrefour Tarnowskie Góry ul. Kościuszki 5 [9882]. Can't start here — a session is already active ELSEWHERE: session 84214483 at 'Tarnowskie Góry ul. Oświęcimska' [616319], plate ABC 123DE, 25 min left. Only one session can run at a time. End it with end_session(84214483) and then retry, or keep the current one.
```

---

## `start_free_session`

Start a **free-only** session at a parking (by id). Auto-applies a **validated** active free-time
promotion and starts only if the session is actually free (cost 0). If it would cost anything, it
starts **nothing** and instead reports the **price options** and suggests `start_session` for a paid
session. Never incurs a charge — the safe default for "park free at this parking". When a usable promo
applies the session fills the whole free window: a `durationMinutes` shorter than the free window is
rounded up to it (capped by the lot's max duration), and a longer one is refused.

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `parkingId` | int | yes | — | Parking id from `find_parking`. |
| `licensePlate` | string | yes | — | Registered plate. |
| `durationMinutes` | int | no | full free window | Desired duration in minutes. A shorter value is rounded up to the full free window; longer than the window is refused. |

**Output**
```
Parking: Tarnowskie Góry ul. Oświęcimska [616319]. Started a free session for 120 min on parking 616319. Early stop: allowed, but NO refund — the remaining paid time is forfeited. Session id: 84214483, status: ACTIVE.
```
Not free (nothing started — reports pricing and points to the paid tool):
```
Parking: Carrefour … [9882]. No free parking is available at parking 9882. Shortest paid option: 60 min = 2.00 PLN. Other options: 120 min = 6.00 PLN. Use start_session to start a paid session. Early stop: allowed, but NO refund — the remaining paid time is forfeited.
```

---

## `start_session_current_location`

Like `start_session` (**may cost money**), but finds the closest parking to a GPS location within a
radius and starts there. Use when the user says "park here / where I am". Same free/paid behaviour: a
paid start returns the shortest option + the other price tiers + a cancel/refund note, and needs a
second call with the chosen `durationMinutes` and `confirmedCostPln`. To guarantee a free-only start
here, use `start_free_session_current_location`.

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `latitude` | number | yes | — | Current latitude (decimal degrees). |
| `longitude` | number | yes | — | Current longitude (decimal degrees). |
| `licensePlate` | string | yes | — | Registered plate. |
| `durationMinutes` | int | no | server default | Desired duration in minutes. Omitting it uses the server default (60 min unless changed). Only rounded up to a free-time promotion's window when that promotion validates as usable. On a paid confirming call, set this to the chosen option's duration. |
| `radiusMeters` | number | no | `CurrentLocationRadiusMeters` | Max distance to search. Omitting it uses the configured `ApcoaFlow:CurrentLocationRadiusMeters` (300 m by default). |
| `confirmedCostPln` | decimal | no | — | Exact cost being confirmed (the chosen option's price; paid, second call only). |

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
starts only if the session is actually free; if it would cost anything it starts nothing and reports
the price options, suggesting `start_session_current_location` for a paid session. Never incurs a
charge.

**Inputs**

| Name | Type | Required | Default | Description |
|---|---|---|---|---|
| `latitude` | number | yes | — | Current latitude (decimal degrees). |
| `longitude` | number | yes | — | Current longitude (decimal degrees). |
| `licensePlate` | string | yes | — | Registered plate. |
| `durationMinutes` | int | no | full free window | Desired duration in minutes. A shorter value is rounded up to the full free window; longer than the window is refused. |
| `radiusMeters` | number | no | `CurrentLocationRadiusMeters` | Max distance to search. Omitting it uses the configured `ApcoaFlow:CurrentLocationRadiusMeters` (300 m by default). |

**Output** — same shape as `start_free_session`, with the distance included.
```
Parking: Tarnowskie Góry ul. Oświęcimska [616319], 237 m away. Started a free session for 120 min on parking 616319. Session id: 84214483, status: ACTIVE.
```

---

## `start_expensive_session`

Identical to `start_session` but **bypasses the maximum-cost cap**. Exposed as a separate tool so a
host can allow/deny or require extra confirmation for over-cap sessions independently. Still uses the
two-step confirmation (chosen `durationMinutes` + exact `confirmedCostPln`) for paid sessions, returns
the price tiers + cancel/refund note, and only applies/fills a free-time promotion when it validates.

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
