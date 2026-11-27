# MCP tools

The human-facing reference for the server's tools: names, inputs, example outputs. Update it in the same change
whenever a tool is added, renamed, or changes its parameters, behaviour or output. Tool names follow the APCOA Flow
API's naming, except that a parking (the API's "space") is always called a parking: a parking is the place, a
session is parking there.

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

## `find_parkings`

Find parkings by name or text and/or near a position. Read-only, idempotent.

| Input | |
|---|---|
| `query` | Name or text: shop brand ("Biedronka" — shop parkings that come with free time), town, street, postcode or the parking's location code. Optional when a position is given. |
| `latitude`, `longitude` | Position to search around. |
| `radiusMeters` | Without a query: the radius around the position (default 2000). |

- **With a query** (with or without a position): APCOA's own search, which also matches addresses, towns and location
  codes. With a position the nearest come first (any distance); without one the distance is left out. Hits near the
  position are measured to the parking's **outline** when it has one ("you are inside it", "… (to its edge)").
- **Position only**: every parking on the map within the radius, nearest first, by outline or pin.
- Results are always parkings on the map: a position-less API search returns unmapped duplicates (no coordinates, no
  address), so a name-only query is searched around the middle of Poland.

```
[3f6f0269-d77a-4ff6-bc88-8ac41c7b7e3f] SPP Czarny Dunajec - you are inside it - Czarny Dunajec, 34-470 Poland (code 26397)
[7138743d-7fe6-4c6a-a328-6280601d04f6] SPP Czarny Dunajec Abonamentowy - 23 m (to its edge) - Czarny Dunajec, 34-470 Poland
[7acd8ec5-4e5e-4662-ae8b-a8be2a60d6ba] SPP Czarny Dunajec - parking w pasie ruchu drogowe - 84 m - Czarny Dunajec, 34-470 Poland (code 29086)
```

```
[c78e0ce2-64af-4592-b01b-4372e341f687] Biedronka Jelenia Góra ul. Sobieskiego 58 - ul. Sobieskiego 58, Jelenia Góra, 58-500 Poland (code 25898)
```

## `get_parking_details`

Everything about one parking. Read-only, idempotent. Input: `parkingId`.

- **Location**: address, coordinates, whether it has an outline on the map (distances are then measured to its edge).
- **Sessions**: status ("Open", "AutoPay"), whether you start sessions (in this server or the app) or the parking
  recognises plates and starts and ends them by itself, the usual stay (shop parkings: their free window) and the
  longest stay.
- **Pricing**: the tariff table and rules as the operator writes them. For the price of a specific stay for your
  vehicle, use `get_price`.
- **Amenities**: EV charging, season tickets, pre-booking, flexi permits, municipal parking; payment (card, Stripe).

```
[3f6f0269-d77a-4ff6-bc88-8ac41c7b7e3f] SPP Czarny Dunajec (location code 26397)
Location:
  Address: Czarny Dunajec, 34-470 Poland
  Coordinates: 49.44096, 19.855513
  Outline on the map: yes (10 points; distances are measured to its edge)
Sessions:
  Status: Open
  You start and end a session (start_session / start_free_session, or in the APCOA Flow app).
Pricing:
  Parking: Up to 1 hour: 0.00 PLN, Up to 2 hours: 0.00 PLN, Max day price: 22.00 PLN
    Parking dostępny 24h. Płatny: Poniedziałek - Piątek 8:00 - 16:00. Sobota 8:00 - 13:00. 1 PLN za pierwszą rozpoczętą godzinę. …
  Parking fees: Prices include convenience fees
Amenities: season tickets
Payment: card (Stripe)
```

A plate-recognition parking (e.g. Lidl Poznań ul. Fabryczna 23) says `Sessions can't be started here …` and
`Plate recognition: sessions start and end automatically when the car enters and leaves.`; the start tools refuse
there.

## `get_price`

The price of a stay starting now, for one of the account's vehicles, as APCOA quotes it: free time the parking gives
(e.g. a shop's free 1.5 h once per 4 h) is already included. When the stay can be free, also how long. Read-only,
idempotent. Inputs: `parkingId`, `licensePlate` (spaces and case don't matter), `durationMinutes`.

```
Biedronka Opole ul. Ozimska 48, ABC12345, 2 h from now: 2.00 PLN (charged at start).
Free for up to 1 h 30 min for this vehicle right now; longer stays cost money.
```

"Charged at start" (pre-pay) or "charged at the end" (post-pay) says when the default card is charged.

## `get_active_sessions`

The running sessions: session id (for `extend_session` / `end_session`), parking, plate, start and end (Warsaw time;
the date is shown when it isn't today), time left, how it's paid, and whether it can't be extended or stopped early.
Read-only, idempotent.

```
[35da0b7f-4860-4c98-b22e-2121ac0f1b2a] Targowiska Miejskie Ostrów Wlkp. III - ABC12345, from 14:50 until 15:20 (20 min left), paid at start
```

With none: `No parking session is running.`

## `get_session_history`

The 10 most recent sessions, newest first: parking, plate, when it ended, cost (the state is added when it isn't
`billed`). Read-only, idempotent.

```
Biedronka Żyrardów ul. Legionów Polskich 61 - ABC12345, ended 2026-11-26 15:04, 0.00 PLN
```

With none: `No past parking sessions on the account.`

For **pre-pay** sessions APCOA keeps the *planned* end in its history even after an early stop, so "ended" can be
later than the real stop; post-pay sessions show the real stop time.

## Starting, extending and ending sessions

All of these change something and are never idempotent (each start can cost money), except `end_session`.
`start_expensive_session` and `end_session` are marked destructive, so clients always ask before them. Tools that
act on a parking or session id also take **`parkingName`**: it isn't sent to APCOA, it only makes the client's
permission prompt say *which* parking.

**Payment**: always the account's **default** payment method. A **paid** start or extension needs it to be a saved
card charged without any extra step (`flow: saved`, no action needed — not Apple Pay, not the hosted checkout page);
otherwise the tool refuses before starting and says to set a saved card as the default in the APCOA Flow app. Free
starts and extensions work with any default method (its id is sent, as the app does; nothing is charged).

**Free** means the dated quote for this vehicle is 0.00 — whatever the reason (a free tariff, a shop's free time, a
one-time free period). The **free window** is the longest stay that still quotes 0.00 (the parking's preselected
duration is tried first, else a short search of up to a day).

| Tool | Inputs | Does |
|---|---|---|
| `start_session` | `parkingId`, `parkingName`, `licensePlate`, `durationMinutes?`, `confirmedCostPln?` | Free → starts at once. Paid → the first call returns the price and starts nothing; call again with the same `durationMinutes` and the exact `confirmedCostPln` after the user agrees. Refuses above `ApcoaFlow__MaxSessionCostPln` (default 100). Without a duration: the whole free window when free, else the parking's usual duration (60 min if it has none). |
| `start_free_session` | `parkingId`, `parkingName`, `licensePlate`, `durationMinutes?` | Only if free for the whole stay; never charges. Without a duration: the whole free window. |
| `start_expensive_session` | as `start_session` | `start_session` without the cost limit. |
| `start_session_here` / `start_free_session_here` | `latitude`, `longitude`, `licensePlate`, `durationMinutes?`, (`confirmedCostPln?`), `radiusMeters?` | The same at the nearest parking within the radius (default `ApcoaFlow__CurrentLocationRadiusMeters`, 300 m), measured to its outline when it has one. If the position is inside exactly one outline, that one. If two parkings are within **30 m** of each other in distance (GPS can't tell them apart), nothing starts and both are listed. |
| `extend_session` | `sessionId`, `parkingName`, `minutes`, `confirmedCostPln?` | Quotes the extension; free → extends at once, paid → price first, then with `confirmedCostPln`. The cost limit is for the **whole session**: what it already costs plus the extension. Reports "Extended" only once the new end shows in the running sessions (it waits up to ~20 s; an extension APCOA processes later is reported as accepted, not to be repeated). |
| `end_session` | `sessionId`, `parkingName` | Stops the session, then reads the final cost from the history. |

Refusals are normal results (not errors): a parking where sessions can't be started (plate recognition), unknown plate, a running session for that plate (it's named, with its
id), not free, over the free window, over the cost limit, price changed since the quote, default payment method not
a saved card, longer than the parking allows, no parking nearby, too close to call. If a paid start ever comes back
needing an interactive payment step (3-D Secure), the session is ended at once and the tool says so. If APCOA accepted
a start or an extension but the server couldn't read the result back, the tool says so and that it must **not** be
repeated (check `get_active_sessions`): a retry could charge twice.

```
Started: [046ca4c0-fc81-4506-bb9f-d387b48e3ca8] Targowiska Miejskie Ostrów Wlkp. III - ABC12345, from 21:49 until 22:04 (free).
```

```
1 h at Czarny Dunajec for ABC12345 costs 1.00 PLN, charged at the end to visa ****4412. Nothing was started yet:
confirm with the user, then call again with durationMinutes=60 and confirmedCostPln=1.00.
```

```
ABC12345 already has a running session: [046ca4c0-…] Targowiska Miejskie Ostrów Wlkp. III - ABC12345, from 21:49 until 22:04. End it with end_session (or extend it with extend_session) before starting another one.
```

```
Extended: [046ca4c0-fc81-4506-bb9f-d387b48e3ca8] Targowiska Miejskie Ostrów Wlkp. III - ABC12345, from 21:49 until 22:19 (free).
Ended the session at Targowiska Miejskie Ostrów Wlkp. III (ABC12345). Final cost: 0.00 PLN.
```
