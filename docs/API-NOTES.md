# APCOA Flow API notes

The unofficial API this server uses, as the APCOA Flow app 26.12.1 (web app `https://app.apcoaflow.com` and the
mobile apps) talks to it. Everything here was observed on the wire; dates say when. The client is
`src/ApcoaFlowApiClient`; the sandbox (`src/ApcoaFlow.Sandbox`, see `CLAUDE.md`) re-checks any of it in one command.

## Hosts

| Host | Role |
|---|---|
| `https://api.nucleus.apcoaflow.com` | The REST API, everything under `/api/v1/…`. ASP.NET Core behind Azure Front Door and Cloudflare; `x-trace-id` response header. |
| `https://auth.prd.nucleus.valuespaces.io` | The OAuth server (OpenIddict): `/connect/token` for sign-in and token refresh. |

Headers the app sends on every API call, and so does the client: `Authorization: Bearer <access token>`,
`x-user-agent: Flow 26.12.1 (Web)`, `Accept-Language: en`, `X-Timezone-Offset: <minutes>` (60 in Polish
winter). `sessions/startParking` also carries `idempotency-key: <uuid>`.

## Sign-in

- **Password grant** (2026-11-27): `POST {auth}/connect/token`, form `grant_type=password`, `username` = the
  account e-mail (what this server uses) or the phone number in E.164 (`+48…`), `password`,
  `client_id=flownextapp.prd.nucleus.valuespaces.io`, `scope=offline_access`. No client secret, no bot check on
  this path. → `200 {access_token, token_type: "Bearer", expires_in: 3600, refresh_token}`.
  - Slow: a flat **~7.2 s** server-side. **The first sign-in of a new account took 71 s**, so the client keeps the
    default 100 s timeout.
  - Wrong e-mail or password: `400 {"error":"invalid_grant","error_description":"The username/password couple is invalid."}`.
  - E-mail sign-in was only seen on accounts whose e-mail is verified (`users/profile` → `email.status: "approved"`,
    `loginMethods` includes `email`).
- **Refresh grant**: same endpoint, `grant_type=refresh_token&refresh_token=…&client_id=…` → ~0.15 s, a new 1 h
  access token and a **rotated** refresh token; the old refresh token was still accepted right after. Its lifetime
  is unknown, so the client signs in again whenever a refresh fails.
- **Access token**: RS256 JWT (`typ at+jwt`), 1 h. The refresh token is encrypted (opaque).
- **No, invalid or expired token on the API**: `403 {"code":"Unauthenticated","message":"An error has occurred."}`
  (not 401, no `WWW-Authenticate`). An expired token was checked 2026-11-27 (sandbox `expired-token`): still 200 at
  59.5 min, this 403 at 65 min. The client renews the token before expiry anyway, and on this 403 renews and retries
  once; it treats 401 the same, defensively.

## Errors

Business errors are `{"code": "...", "message": "..."}`; validation errors are ASP.NET ProblemDetails
(`{type, title, status, errors}`).

| Code | Status | When |
|---|---|---|
| `Unauthenticated` | 403 | No or invalid bearer token. |
| `ParkingSessionInvalidArgument` | 400 | A second start for a plate that has a running session ("The session for the same license plate has already been started."); extending a stopped session ("Only planned or active sessions can be extended."). |
| `SessionNotFound` | 404 | Stopping an unknown session id; `sessionsHistory/{id}` of a running post-pay session. |
| `NotFound` | 404 | Generic (e.g. `users/features/{id}` with an unknown id). |

## Account

- `GET /api/v1/users/profile` → `{userId, firstName, lastName, phoneNumber, email: {address, status} | null,
  loginMethods, preferredLanguage, isMarketingOptIn, isTelematicsOptIn, termsToAccept: [{termsId, title,
  description, versionId, language, content, required, category}]}`.
- `GET /api/v1/vehicles` → `[{id, countryCode, licensePlate, description, isDefault, isAnprAutoPaymentEnabled,
  validUntil, companyName, …}]`. `id` is the `licensePlateId` that prices and starts take. **Plates come without
  spaces** (`"ABC12345"`); `description` is the user's own label.
- `GET /api/v1/paymentMethods[?spaceId=]` → `[{id, type, identifier: "****4412", expiration, status, isDefault, psp:
  "stripe", flow, requiredAction, availability}]`. `flow: "saved"` methods are charged without any interaction;
  there's also a pseudo-method `{id: "hosted-checkout", type: "unknown", flow: "hostedCheckout"}` (an interactive
  payment page). With `spaceId`, only what that space accepts.

## Spaces

Parking places are **spaces**, with GUID ids. There's no offline dataset: the app finds spaces with these calls.

- `GET /api/v1/search?query&latitude&longitude&size&isParking&isCharging` → `{spaces, chargers, places}`.
  - Space hits: `{id, name, tariffCode, coordinates: {latitude, longitude}, address: {displayValue}, distance,
    parkingIconUrl, countryCode, isUserCountry}`. `tariffCode` is the space's **location code** (what users type);
    searching for it returns exactly that space.
  - With a position: nearest first, `distance` in **km**; 20 hits by default, `size=50` gave 30 (2026-11-27).
  - Without a position: arbitrary order, and **parkings that aren't on the map**: other ids than with a position, no
    coordinates and no address (in the hit and in their details), though they quote. With a position every hit is a
    mapped parking with an address (pinned by `FreeOptionsContractTests`). So the server always searches with a
    position (the user's, else the middle of Poland).
  - `places` are geocoded streets/towns, not spaces.
- `GET /api/v1/spaces/getByBoundingBox?NELatitude&NELongitude&SWLatitude&SWLongitude&Zoom` → map pins `[{id, name,
  coordinates, parkingType: "startStop" | "anpr", chargingType, mapPinImage, geoBorder, isMunicipality}]`
  (`[]` for an empty view, 500 without parameters). `Zoom` changes nothing and there's no cap: the whole of Poland
  returns all 525 pins (2026-11-27).
  - **`geoBorder`** is the space's outline, when it has one (78 of 525 Polish spaces, 2026-11-27):
    `{coordinates: [{latitude, longitude}, …], color: null}`, a closed ring (last point = first). Only these pins
    carry it; search hits and space details don't.
- `GET /api/v1/spaces/{spaceId}` → `{id, name, tariffCode, maxDuration, defaultLengthOfStayInMinutes, coordinates,
  address, parkingOverview: [{title, imageUrl}], sessionsStartAutomatically, prices: [{sectionName, prices: [{label,
  price}], description}], allowedPSPs, hasLengthOfStayPreset, ctaButtonType: "startParking", isMunicipality,
  countryCode, disclaimer, canStartCharging, hasAvailableContracts, hasPrebooking, hasFlexiPermits, …}`. `prices` is
  the readable tariff table, not a quote. Across 45 sampled parkings: `parkingOverview` titles "Open" and "AutoPay"
  (plate recognition, `sessionsStartAutomatically: true`); `ctaButtonType: "none"` where sessions can't be started (e.g.
  Lidl Poznań ul. Fabryczna 23: "1.5 h free without starting a session"); season tickets on 18; some pins answer
  `404 {"code":"FaTariffNotFound","message":"No tariff available…"}`. Biedronka spaces:
  `defaultLengthOfStayInMinutes: 90` (= their free window) and `maxDuration: 43200` (**minutes**: 30 days; quotes
  don't enforce it, a 31-day quote still comes back).

## Prices

Amounts are strings: `"PLN 5.00"` (currency, space, invariant decimal); large amounts have a thousands separator:
`"PLN 2,698.00"`.

- **Quote a start**: `GET /api/v1/prices?SpaceId&LicensePlateId&Duration&StartDate` (what the app sends; `StartDate`
  UTC with milliseconds). **`Duration` is in seconds.** → `{priceItems, priceDetailItems, totalPrice,
  promocode, priceRequestIdentifier, startDate, endDate, requiresPrepayment, prepaymentMode: "autoCharge" | "none",
  prepaymentMethod: {paymentMethodId, maskedIdentifier, flow}, hostedCheckoutAllowed, …}`.
  - The quote already includes whatever the space gives this plate: Biedronka's free 1.5 h (once per 4 h per space)
    shows as PLN 0.00 for up to 90 min and as the full price once used. Quotes right after a session can lag for a
    minute.
  - A 1-minute quote usually costs the same as 15 minutes (minimum charge). Quotes list a "Convenience fee" line.
  - Biedronka Opole on 2026-11-27: 1–90 min PLN 0.00, 91 min 2.00, 4 h 10.00, 24 h 90.00. Ostrów (Friday evening):
    up to 4 h PLN 0.00, 12 h 5.00 (crosses Saturday 06–15).
  - **Promo codes**: `Promocode=<code>` on the quote (and `promoCode` on start/extend). An unknown code is
    **silently ignored**: same price, `promocode: null`, no error. A real code hasn't been seen yet.
- `GET /api/v1/prices/lengthOfStayPresets?spaceId&licensePlateId&startDate` (the app's duration presets) didn't
  answer within 20–30 s twice: not used.
- `GET /api/v1/benefits` → `[]` on both accounts so far (vouchers, subscriptions, contracts live here).
- **Quote an extension**: `GET /api/v1/prices/updatedPrice?sessionId&duration` (seconds to add) → the same shape,
  with the new `endDate`.

## Sessions

- **Start**: `POST /api/v1/sessions/startParking {spaceId, licensePlateId, startDate, endDate, paymentMethodId,
  paymentChoice: null, extendedSessionId: null, promoCode: null, priceRequestIdentifier: null, tariffPeriodId: null,
  companyBillingAddressId: null, isPreBooked: false}` + `idempotency-key`. `paymentMethodId` is needed even for a
  free start (the app sends the default method). Two answers seen (2026-11-27):
  - free starts and a post-pay saved-card start: `200 {id, status: null, pendingId: null, clientSecret: null, …}` —
    running at once;
  - a **paid pre-pay** start with a saved card: `200 {id: null, pendingId}` — processed **asynchronously**. Poll
    `GET /api/v1/sessions/pending/{pendingId}` → `{status, sessionId, failureReason}`; it said
    `{"status":"completed","sessionId":…}` within seconds. The session runs (and is charged) even if you never poll.
  - A `clientSecret` would mean the card payment needs an interactive step (Stripe / 3-D Secure); not seen yet.
- **Running sessions**: `GET /api/v1/sessions` → `[{id, type: "Parking", status: "active", startDate, endDate,
  sessionCanBeStopped, sessionCanBeExtended, space: {id, name, tariffCode, countryCode, …}, vehicle: {id,
  licensePlate, …}, details: [{title: "Total", subTitle: "0", footer: "PLN"}], extensionType: "flexible", promoCode,
  canApplyPromoCode, paymentFlow: "prePay" | "postPay"}]`; `[]` when none. There is no `GET sessions/{id}` (404).
- **Extend**: `POST /api/v1/sessions/updateParkingDuration {sessionId, newEnd, paymentMethodId, promoCode}` → 200,
  empty body; the same session continues with the new end.
- **Stop**: `POST /api/v1/sessions/stopParking/{id}` → **204**. Stopping it again is 204 too (silent); an unknown id
  is `404 SessionNotFound`.
- Post-pay sessions stopped after 17–19 s were billed PLN 0.00 although quoted PLN 1.00 and 5.00 (two operators,
  2026-11-27). **Nothing relies on this**: assume there is no grace period — a paid start costs its quote. Pre-pay
  sessions are charged at start and **not refunded** on an early stop.

## History

- `GET /api/v1/sessionsHistory?page=1&size=8` → newest first, `[{id, spaceName, type: "parking", endDate,
  totalPrice, paymentStatus: "paid", licensePlate, state: "billed", subType, permitDeduction}]`.
- `GET /api/v1/sessionsHistory/{id}` → `{id, type, spaceName, spaceId, tariffCode, receiptUrls, paymentMethod:
  {paymentId, type, identifier}, startDate, endDate, status, licensePlate, priceItems, totalPrice, promoCode,
  isReceiptManagedExternally, receiptType, isSessionActive, state: "active" | "billed", userOwesNothing}`.
  - Works for a running pre-pay session (`isSessionActive: true`); a running **post-pay** one is `404 SessionNotFound`.
  - `endDate` is the real stop time for post-pay, but stays the **planned** end for pre-pay even after an early stop.

## Test parkings

Tests focus on **free options**; no test relies on paid behaviour or a grace period. Write tests (start / extend /
stop) only where a **dated** quote for the test car says PLN 0.00 right before the start, and end the session within
minutes.

| Space | Id | Location code | Free when |
|---|---|---|---|
| Targowiska Miejskie Ostrów Wlkp. III, ul. Grabowska 29a, Ostrów Wielkopolski | `c9a52a81-3c6f-4ddc-9879-21a9ad0e4cc8` | `22941` | Always, **except Saturday 06:00–15:00** (Europe/Warsaw). Pre-pay. |
| Any parking found with the query **"Biedronka"** (shop parkings that come with free time; search with a position) | — | — | The first 1.5 h (= its usual stay), **once per 4 h per parking**: rotate. Pre-pay. Never hard-code one. |

## Contracts guarded by live tests

Every API behaviour the server relies on has a live test (`[Category("LiveApi")]` in `src/ApcoaFlowApiClient.Tests`,
run nightly). When code starts depending on a new behaviour, check it with the sandbox, add the test, and add a row
here.

| Behaviour | Test (`AccountContractTests`) |
|---|---|
| Password grant with the e-mail returns an access and a refresh token, ~1 h | `Password_grant_with_the_email_returns_an_access_and_a_refresh_token` |
| Refresh grant rotates the refresh token | `Refresh_grant_rotates_the_refresh_token` |
| Wrong password is `400 invalid_grant` | `Wrong_password_is_400_invalid_grant` |
| No token on the API is `403 Unauthenticated` | `No_token_on_the_API_is_403_Unauthenticated` |
| Profile has the sign-in e-mail, `approved` | `Profile_has_a_verified_email` |
| Vehicles carry an id and a plate without spaces | `Vehicles_carry_an_id_and_a_plate_without_spaces` |
| The default payment method is a saved card (`flow: saved`, no action needed) | `The_default_payment_method_is_a_saved_card` |
| Sessions and history are lists | `Sessions_and_history_are_lists` |
| Stopping an unknown session is `404 SessionNotFound` | `Stopping_an_unknown_session_is_404_SessionNotFound` |

Free options, read-only (`FreeOptionsContractTests`):

| Behaviour | Test |
|---|---|
| Searching "Biedronka" (shop parkings with free time) returns matching parkings with location codes | `Searching_the_free_time_query_returns_matching_parkings_with_location_codes` |
| A free-time parking's dated quote is 0.00 up to its usual stay (`defaultLengthOfStayInMinutes`) and paid one minute later | `Free_time_is_in_the_quote_exactly_up_to_the_parkings_usual_stay` (skipped when no Biedronka has free time left) |
| Details of a free-time parking carry its tariff table, coordinates and `ctaButtonType: startParking` | `Details_of_a_free_time_parking_carry_its_tariff_and_say_it_can_be_started` |
| A search with a position returns mapped parkings; without one, unmapped ones | `A_search_with_a_position_returns_mapped_parkings_without_one_unmapped` |
| The test parking quotes 0.00 outside Saturday 06–15 | `The_test_parking_is_free_outside_Saturday_06_to_15` |
| A location-code search returns exactly that parking | `A_location_code_search_returns_exactly_that_parking` |
| Map pins carry closed outlines | `Map_pins_carry_closed_outlines` |

One free cycle per run (`SessionContractTests.Free_session_lifecycle`, on Ostrów or a Biedronka with a dated 0.00
quote; skipped when none is free):

| Behaviour | Checked |
|---|---|
| A free start with the default saved card runs at once, nothing pending | `startParking` → `{id}`, no `pendingId` / `clientSecret` |
| A started session is listed by `sessions` right away (`active`, vehicle, parking) | ✓ |
| A second start for the same plate is `400 ParkingSessionInvalidArgument` | ✓ |
| A free extension: `updatedPrice` gives the new `endDate`, `updateParkingDuration` moves the same session's end | ✓ |
| Stop is 204, and the session leaves `sessions`; a second stop is 204 too | ✓ |
| Extending a stopped session is `400 ParkingSessionInvalidArgument` | ✓ |
| `sessionsHistory/{id}` has it, not active, PLN 0.00 | ✓ (polled up to ~10 s) |

## Trusted, not tested

Behaviours relied on without a live test, because testing them costs money or can't be repeated:

| Behaviour | Observed |
|---|---|
| An expired access token gets the same `403 Unauthenticated` as no token (needs > 1 h, so not in the nightly run) | 2026-11-27, sandbox `expired-token` |
| A paid post-pay start with a saved card runs at once, no interactive step | 2026-11-27, Czarny Dunajec, stopped after 19 s |
| A paid pre-pay start with a saved card is pending, then `completed`; the card is charged at start (PLN 1.00, visa, history `status: paid`, `state: billed`) | 2026-11-27, Markinvest Warszawa al. Wilanowska 363 |
| A pre-pay session stopped early is **not** refunded (stopped after ~30 s, billed the full PLN 1.00) | 2026-11-27, same session |
| (Observed, not relied on: assume no grace) A post-pay session stopped within seconds was billed PLN 0.00 | 2026-11-27: Czarny Dunajec (19 s, quoted PLN 1.00) and Katowice ul. Sobieskiego 23 (17 s, quoted PLN 5.00) |
| Right after a post-pay stop, `sessionsHistory/{id}` can lack a parseable `totalPrice` for a moment; it then shows the real stop time and `state: billed` | 2026-11-27, Sobieskiego 23 |
| Biedronka free time is once per 4 h per space, and shows in the quote | 2026-11-27, Biedronka Żyrardów |
