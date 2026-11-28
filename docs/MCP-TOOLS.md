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
| `licensePlate` | Optional, one of the account's vehicles: the free time in the first 5 summaries is then checked for it with one quote each (`free now for up to …`, `free now for at least …`, `free now (get_price says for how long)` or `paid now`). A bit slower. |

- **With a query** (with or without a position): APCOA's own search, which also matches addresses, towns and location
  codes. With a position the nearest come first (any distance); without one the distance is left out. Hits near the
  position are measured to the parking's **outline** when it has one ("you are inside it", "… (to its edge)").
- **Position only**: every parking on the map within the radius, nearest first, by outline or pin.
- Results are always parkings on the map: a position-less API search returns unmapped duplicates (no coordinates, no
  address), so a name-only query is searched around the middle of Poland.
- **The first 5 results get a short summary** from the parking's hourly prices (one details call each): the free time
  (for any car, or checked for the given vehicle), then the price rows exactly as the APCOA Flow app shows them
  ("Up to 1 hour", "Up to 2 hours", "Max day price" — the server writes these labels; "Max day price" is the current
  day's maximum, not the price of a 24 h stay: `get_price` gives that).

```
[3f6f0269-d77a-4ff6-bc88-8ac41c7b7e3f] SPP Czarny Dunajec - you are inside it - Czarny Dunajec, 34-470 Poland (code 26397)
[7138743d-7fe6-4c6a-a328-6280601d04f6] SPP Czarny Dunajec Abonamentowy - 23 m (to its edge) - Czarny Dunajec, 34-470 Poland
[7acd8ec5-4e5e-4662-ae8b-a8be2a60d6ba] SPP Czarny Dunajec - parking w pasie ruchu drogowe - 84 m - Czarny Dunajec, 34-470 Poland (code 29086)
```

```
[c78e0ce2-64af-4592-b01b-4372e341f687] Biedronka Jelenia Góra ul. Sobieskiego 58 - ul. Sobieskiego 58, Jelenia Góra, 58-500 Poland (code 25898) - free up to 1 h 30 min (any car) · Up to 1 hour PLN 0.00 · Up to 2 hours PLN 2.00 · Max day price PLN 90.00
[1303023a-d5d8-4174-b0e3-c8e406e33a9b] Chorzów ul. Moniuszki 4 - 18.96 km - ul. Moniuszki 4, Chorzów, 41-500 Poland (code 22919) - Up to 1 hour PLN 3.00 · Up to 2 hours PLN 7.00 · Max day price PLN 30.00
```

## `get_parking_details`

Everything about one parking. Read-only, idempotent. Input: `parkingId`.

- **Location**: address, coordinates, whether it has an outline on the map (distances are then measured to its edge).
- **Sessions**: status ("Open", "AutoPay"), whether you start sessions (in this server or the app) or the parking
  recognises plates and starts and ends them by itself, the usual stay (shop parkings: their free window) and the
  longest stay.
- **Pricing**: the price rows exactly as the APCOA Flow app shows them (labels written by the server), what a stay
  starting now costs hour by hour for the next day (from the parking's own price curve, the same for every vehicle),
  and the rules as the operator writes them. For your vehicle (a shop's free time may be used
  up), use `get_price`.
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
  Up to 1 hour: PLN 0.00
  Up to 2 hours: PLN 1.00
  Max day price: PLN 22.00
  Hour by hour if you start now (any vehicle; get_price checks yours): up to 1 h: 0.00 PLN, 2 h: 1.00 PLN, 3 h–23 h: 2.00 PLN
  Rules: Parking dostępny 24h. Płatny: Poniedziałek - Piątek 8:00 - 16:00. Sobota 8:00 - 13:00. 1 PLN za pierwszą rozpoczętą godzinę. …
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
idempotent. Inputs: `parkingId`, `licensePlate` (spaces and case don't matter), and either `durationMinutes` or
`until` (see below).

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
one-time free period). The **free window** and the **longest stay at the same price** are read off the parking's
own price curve (its details asked with `priceOptions` 1–24: one row per hour, in that order, each the price of a stay
of up to N hours starting now; hour 24 is the day maximum and isn't used for durations) and verified with one quote for
the vehicle — never searched for. Row labels are display text and are never read. A concrete stay (any length, also
across days) is always priced by a quote, as in the APCOA Flow app. The curve has whole hours, so within the last free hour the parking's
preselected duration decides (shop parkings: their free window, e.g. Biedronka's 90 min → "free for up to 1 h 30
min"); without one it says "free for at least N h". If the curve shows no free time for the vehicle, the stay is only
known to be free right now; a free start without a duration then uses the parking's usual duration (or 60 min) and
still starts only if that quote is 0.00.

| Tool | Inputs | Does |
|---|---|---|
| `start_session` | `parkingId`, `parkingName`, `licensePlate`, `durationMinutes?` or `until?`, `confirmedCostPln?` | Free → starts at once. Paid → the first call returns the price and starts nothing; call again with the same `durationMinutes` (or `until`) and the exact `confirmedCostPln` after the user agrees. **Same price, longer stay:** when a longer stay costs the same (read off the parking's hourly prices, checked with one quote — e.g. PLN 0.40 per started hour: 15 min → the full hour), the session runs that long; the quote says so ("so the session will run until …") and the result reports it. Refuses above `ApcoaFlow__MaxSessionCostPln` (default 100). Without a duration: the whole free window when free, else the parking's usual duration (60 min if it has none). |
| `start_free_session` | `parkingId`, `parkingName`, `licensePlate`, `durationMinutes?` or `until?` | Only if free for the whole stay; never charges. Without a duration: the whole free window. |
| `start_expensive_session` | as `start_session` | `start_session` without the cost limit. |
| `start_session_here` / `start_free_session_here` | `latitude`, `longitude`, `licensePlate`, `durationMinutes?` or `until?`, (`confirmedCostPln?`), `radiusMeters?` | The same at the nearest parking within the radius (default `ApcoaFlow__CurrentLocationRadiusMeters`, 300 m), measured to its outline when it has one. If the position is inside exactly one outline, that one. If two parkings are within **30 m** of each other in distance (GPS can't tell them apart), nothing starts and both are listed. |
| `extend_session` | `sessionId`, `parkingName`, `minutes` or `until` (the new end), `confirmedCostPln?` | Quotes the extension (stretched to the longest end at the same price: from the hourly prices, else the next full hour); free → extends at once, paid → price first, then with `confirmedCostPln`. The cost limit is for the **whole session**: what it already costs plus the extension. A paid extension answers "awaiting payment" with a pending id, which is followed like a paid start's (a failed payment is reported). Reports "Extended" only once the end has moved past the old one and reached the new one (it waits up to ~20 s; an extension APCOA processes later is reported as accepted, not to be repeated). |
| `end_session` | `sessionId`, `parkingName` | Stops the session, then reads the final cost from the history. |

**`until`** — an end time instead of a length, the way people say it ("park until 18:00"): a clock time in the
**parking's** local time (its country's zone, from `countryCode`; Polish time when unknown — what matters is the time
where the car is, wherever the user is; other zones are named, "18:00 (Europe/London time)"), meaning its next occurrence ("8:00" late in the evening is tomorrow morning), or a date and time
("2026-12-05 18:00"). It becomes the minutes from now (rounded up), or for `extend_session` from the session's current
end. Give minutes or `until`, not both. A paid quote then says to call again with the same `until`.

**Same price, longer stay** (every mode — minutes, `until`, free and paid starts, extensions): a free stay runs to the
end of the free window; a paid start to the longest stay at the same price from the parking's hourly prices; an
extension to the longest end at the same price, from the same hourly prices read as the session's whole length since
its start (e.g. Chorzów, 30 PLN from 8 h: an extension at 8 h runs to 23 h at no extra cost), else the next full hour
of the session. Each is checked with one quote first, announced in the
quote ("…so the session will run until 16:00") and reported in the result ("It runs until 16:00: 1 h cost the same as
the 15 min asked for.").

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
1 h at Czarny Dunajec for ABC12345 costs 1.00 PLN, charged at the end to visa ****1234. Nothing was started yet:
confirm with the user, then call again with durationMinutes=60 and confirmedCostPln=1.00.
```

```
ABC12345 already has a running session: [046ca4c0-…] Targowiska Miejskie Ostrów Wlkp. III - ABC12345, from 21:49 until 22:04. End it with end_session (or extend it with extend_session) before starting another one.
```

```
Extended: [046ca4c0-fc81-4506-bb9f-d387b48e3ca8] Targowiska Miejskie Ostrów Wlkp. III - ABC12345, from 21:49 until 22:19 (free).
Ended the session at Targowiska Miejskie Ostrów Wlkp. III (ABC12345). Final cost: 0.00 PLN.
```
