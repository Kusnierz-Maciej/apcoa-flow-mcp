# APCOA Flow API Notes

Captured from iOS app traffic (APCOA FLOW 6.85.2 / Alamofire 5.10.2) on 2026-11-23.
Base host: `api.apcoaflow.com`

---

## Auth

- Login: `POST /authentication/v2/consumer/login` — JSON body with `email`, `password`, `locationCode` (ISO country, e.g. "PL"), `failedResponseRequired: true`.
- Response field is `authToken` (NOT `accessToken`) — this is the bearer token.
- Also returns `refreshToken` (separate long-lived token), `ttl` in ms (observed: 7776000000 = 90 days), `userId`.
- Logout: `POST /authentication/v1/consumer/logout` — form-urlencoded body with `refreshToken`.
- All consumer endpoints require `Authorization: Bearer <authToken>`.
- Token is attached by `ApcoaFlowAuthHandler`.

---

## Endpoint Oddities

### Path prefix `/authentication/v1/`
All consumer endpoints use this prefix, even non-auth ones (parking details, tariffs, etc.).
This is an APCOA API convention, not a mistake.

### EV Sessions trailing slash
`GET /authentication/v1/evSession/` — the trailing slash is real, server returns 404 without it.

### `vehicleNumbwe` typo in tariff breakdown
`GET /authentication/v1/tariff/tariffBreakDownV2` has the query param `vehicleNumbwe` (not `vehicleNumber`).
This is a **server-side typo** preserved in production. Must be sent exactly as `vehicleNumbwe`.

### Tariff breakdown `parkingSessionId`
When extending an existing session, the tariff breakdown request also includes `parkingSessionId`.
When starting fresh it is absent. Both cases observed in dumps.

### Favourites 204 = empty list
`GET /authentication/v1/consumers/favourite/parking` returns **204 No Content** when the user has
no favourites. The client should treat 204 as an empty list rather than an error.

### One active session per account → HTTP 424 `LIMIT_EXCEEDED`
Starting a session (`POST /consumer/parkingSession`) while one is already active is rejected with
**HTTP 424 Failed Dependency** and body `{"message":"LIMIT_EXCEEDED","statusCode":5012,...}`. The
backend allows only one active session per account. Handle this by listing active sessions and
surfacing the existing one rather than retrying.

### Tariff breakdown applies promos it cannot honour (free quote, paid start)
`GET /tariff/tariffBreakDownV2` with a promotion applies the discount **optimistically** — it returns
`totalPayableAmountInCents=0` / `discountApplicable=1` even for a promo that is actually unusable
(expired, used up, or not valid for the account). The real start then **charges full price**. The
authoritative check is `POST /promotion/validate`: a usable coupon returns 2xx; an unusable one returns
**HTTP 422** with `{"message":"INVALID_PROMOTION_CODE","statusCode":5007,...}`. Always validate before
trusting a promo-applied quote. (The `free_duration*` fields on the breakdown are always 0/empty in PL
and are not a reliable signal either — use the base-tariff `totalPayableAmountInCents` to judge "free".)

Concrete example (Carrefour Tarnowskie Góry, lot `10584`, coupon `FREE120PL2_4H` = 2 h free every 4 h):
the breakdown "with promo" returns `discountType=percent, discountValue=10` (a trivial 10 % discount,
NOT the free window; `freeTimeSlot:[]`, `freeDurationApplied:false`), while `promotion/validate`
returns 422 `INVALID_PROMOTION_CODE`/5007 because the per-person 4 h usage window is still consumed.

### `GET /tariff?parkingLotId=...` returns the full pricing grid (unit = 1/100 cent!)
Confirmed live (2026-11-25). The response is **not** flat name/description — it is the whole rate grid:
```
{ "tariffTemplates": [ {
    "tariffId", "parkingLotId", "name", "isDefault", "isActive",
    "isProrationAllowed",            // 0 = early stop is NOT prorated/refunded
    "freeDurationInMinutes",
    "stacks": [ {
      "slots": [ {
        "startDay","startTime","endDay","endTime","maxMinutes","freeSlot",
        "grids": [ { "type": "FLAT"|"INCREMENTAL", "amountInCents", "duration", "sequence" } ]
      } ],
      "maxima": [ { "type": "DAY", "amountInCents", "isDurationBoss" } ]
    } ]
} ] }
```
**UNIT GOTCHA:** `amountInCents` here is in **1/100 of a cent** — divide by **10000** (not 100) for the
currency amount. Carrefour lot `10584`: FLAT `20000`/60 min = **2.00 PLN** first hour; INCREMENTAL
`40000`/60 min = **+4.00 PLN** per further hour (so 90–120 min = 6.00 PLN); DAY maxima `500000` =
**50.00 PLN/day**. Price tiers are derived from this grid (FLAT block + cumulative INCREMENTAL blocks,
capped by the daily maximum). The iOS app does **not** call `/tariff`; it derives prices by probing
`tariffBreakDownV2` at discrete durations — but the grid is the authoritative source.

### Start charges immediately; early stop does not refund
`POST /consumer/parkingSession` (start) **captures money immediately** — the response carries
`transactionStatus:SUCCESSFUL` and `transactionAmount` (⚠ in **cents**, e.g. `200.0` = 2.00 PLN) and
`sessionPaymentType:CAPTURE`. `PUT /consumer/parkingSession` (stop) returns the final amount nested at
`parkingSession.amount` (PLN) — the **top-level `transactionAmount` is null** on stop. Ending a paid
session early does **not** refund when the lot's `parkingLotConfiguration.allowRefund=0` (and tariff
`isProrationAllowed=0`) — the remaining paid time is forfeited. `enableStopButton` says whether a
session can be stopped at all. (Carrefour `10584`: `allowRefund:0`, `isProrationAllowed:0`,
`enableStopButton:1`.)

---

## Parking Search — S3 Offline Data

The app does **not** use a live search API. It downloads and locally searches static binary files from S3.

### S3 Setup
- Bucket: `apcoa-parking-data` (public, no signing required despite the app using pre-signed URLs)
- Region: `eu-central-1`
- Checksum manifest: `apcoa_parking_checksum_map` — JSON, keys like `apcoa_u3_PL_parking_checksum`, values are base64-encoded MD5 hashes.
- Data files: `apcoa_{geohash2}_{CC}_parking` — e.g. `apcoa_u3_PL_parking`
  - Geohash2 = first 2 chars of geohash covering that region
  - CC = ISO country code (PL, DE, UK, AT, NO, SE, DK, IT, NL, BE, CH, IE)

### File Encryption: AES-128-GCM
Format: `[nonce:12 bytes][ciphertext][auth_tag:16 bytes]`

- Key: `parkingDataKey` from `GET /authentication/v1/dashboard/tenant/appConfigByCompanyId/{companyId}?version=v3`
  - Value is a base64-encoded 16-byte key (AES-128). Fetched at runtime — see `ApcoaS3ParkingDataSource`.
- After decryption: JSON object keyed by parking ID string.

### Decrypted JSON Structure
Top-level: `{ "5688": { ...parkingEntry }, "5689": { ... }, ... }` — ~1355 entries in `apcoa_u3_PL_parking`.

Each entry has:
```
id, displayName, address, city, zipCode, countryCode, currencyCode
geoLocation: { latitude, longitude }
entranceGeoLocation: { latitude, longitude } | null
isParkingEnabled: 0|1
parkingType: null | string
accessTypeList: ["LPR", ...]
services: ["VIDEO_SURVEILLANCE", "LIFT", "STAFF_ON_SITE", "OTHERS", ...]
operationalHours: [{ id, day(1-7), openTime, closeTime }, ...]
parkingLotTypeList: ["CAR", "MOTORCYCLE", ...]
isPassAvailable, isPermitAvailable, isPreBookingAvailable, isEvOnly: 0|1
parkingTranslations: { "pl": { name, displayName, address, city, description }, ... }
parkingImageObjects: [{ sequence, externalId(UUID) }, ...]
parkingConfigs: { locationManagerDetails: { ... } }
appInterface: "DIALER_UI" | ...
showPromoSection: "EVERYWHERE" | ...
isTimeAdditionEnabled: 0|1
geofences: []
displayPolygon, displayPolygonZoomLevel
externalId: string (the old external ID)
```

**No pricing/tariff data in S3 files** — only location, metadata, and operational hours.
Tariff info requires a separate API call: `GET /authentication/v1/tariff?parkingLotId=...` — which
returns the full pricing grid (see "`GET /tariff` returns the full pricing grid" above).

### Credentials in App Config
The app config (`GET /authentication/v1/dashboard/tenant/appConfigByCompanyId/9?version=v3`) returns:
- `awsParkingBucket`: `"apcoa-parking-data"` — S3 bucket name
- `awsParkingRegion`: `"eu-central-1"`
- `awsParkingAccessKeyId`: `[redacted]` — for S3 request signing (not needed for public bucket)
- `awsParkingSecretKey`: `[redacted]` — S3 signing only
- `awsParkingChecksumMap`: `"apcoa_parking_checksum_map"` — manifest filename
- `parkingDataKey`: `[redacted]` — **file decryption key (AES-128); fetched at runtime, never hardcoded**

**Implementation note:** Fetch app config to get `parkingDataKey`, then use it to decrypt S3 files.
The bucket is publicly accessible without signing.

---

## Session Flow

```
1. POST /authentication/v1/consumer/parkingSession        → start (creates session)
2. GET  /authentication/v1/consumer/parkingSessionV2      → poll active sessions
3. POST /authentication/v1/consumer/parkingSession/extendParkingSession → extend
4. PUT  /authentication/v1/consumer/parkingSession        → stop (ends session)
```

The app polls step 2 aggressively (~every 2–5 s while the home screen is visible).

Before starting a session the app always:
1. `GET /authentication/v1/tariff/tariffBreakDownV2` with `duration=0` to show entry cost
2. `POST /authentication/v1/parking/operational-hours/validate`
3. `POST /authentication/v2/promotion/applicableOffersWithPromotion`
4. `POST /authentication/v1/promotion/validate`
5. `GET /authentication/v1/parking/utcDateTime`

---

## Real IDs Observed in Traffic

| Item | Value |
|---|---|
| Company ID (Poland) | `9` |
| Consumer/User ID | `6341735` |
| Consumer Profile ID | `6340557` |
| Payment Method ID (BLIK) | `5876545` |
| Vehicle ID | `7417618` |
| Vehicle plate | `ABC 123DE` |
| Parking IDs observed | `9882`, `8331`, `10395`, `616319` |
| Parking Lot IDs observed | `9085`, `10584`, `11089`, `41905` |
| Country code | `PL` |

### Good parking for testing (unlimited free sessions)

**Parking ID: `616319`** — observed to have no session limit and free tariff (tariff ID `108014`,
parking lot ID `41905`). Safe to use for start/stop/extend testing without consuming the daily limit.
Name / location TBD — appeared in dump 24 Nov 1027.

---

## Missing / TBD — Needs Live API Testing

The following response shapes were NOT cleanly isolated from the WebProxyTool HAR dumps (buffer merging issue). Confirm these during live testing against parking `616319`.

### Response shapes to verify

| Endpoint | Unknown fields |
|---|---|
| `GET /authentication/v1/consumer/parkingSessionV2` | Full `ParkingSessionV2` shape — `parkingName`? `tariffId`? `promotionId`? exact field names |
| `GET /authentication/v1/parking/{id}` | Full `ParkingDetail` — does it include `operationalHours`? `parkingLots`? `tariffId`? |
| `POST /authentication/v2/promotion/applicableOffersWithPromotion` | Full `Offer` shape |
| `GET /authentication/v1/consumers/favourite/parking` | Full `FavouriteParking` shape (may have more than 3 fields) |

Resolved (confirmed live 2026-11-25):
- `PUT /consumer/parkingSession` (stop) — final amount nested at `parkingSession.amount`; top-level `transactionAmount` null. See "Start charges immediately; early stop does not refund" above.
- `GET /parking/{id}/parkingLots` — `parkingLotConfiguration` carries `enableStopButton`, `allowRefund`, `allowExtension`, `minDuration`/`maxDuration`, `durationPerRotationInMins`, `paygTariffRows`, `sessionIntervalInMins`.
- `GET /tariff?parkingLotId=...` — full pricing grid; see "`GET /tariff` returns the full pricing grid" above (amountInCents is 1/100-cent, ÷10000 for PLN).

### Other TBD

- **Parking 616319 name / location** — **Tarnowskie Góry ul. Oświęcimska**, ul. Oświęcimska 21, 42-600 Tarnowskie Góry, PL. Lat: 50.44333, Lon: 18.86299. Found in `apcoa_u2_PL_parking` tile.
- **`ParkingSessionV2Query` params** — observed: `displayVat`, `displayVatWithoutConvenienceFeeVat`, `pageNumber`, `pageSize`, `parkingSessionStatuses`, `profileNames`, `status`. Confirm all are correct.
- **`DELETE` favourites** — endpoint not observed, likely `DELETE /authentication/v1/consumers/favourite/parking/{parkingId}`.
- **App config `parkingDataKey` for other companies/regions** — may differ per `companyId`. Always fetch from live config before decrypting.
- **`appInterface` field on parking** — values seen: `"DIALER_UI"`. Others unknown.

---

## Pagination

Standard across the API:
- `pageNumber` (1-based)
- `pageSize`

---

## App / SDK Headers

- `User-Agent: APCOA FLOW/6.85.2 (com.apcoaflow.consumer; build:1; iOS 27.2.1) Alamofire/5.10.2`
- `Accept-Language: en-PL;q=1.0, pl-PL;q=0.9`
- Requests use JSON bodies (Content-Type: application/json) unless noted.

---

## Third-Party Traffic (not APCOA API — ignore)

The dumps contain noise from other SDKs the app bundles:
- `/a1?os=iOS` — analytics (likely Adjust)
- `/glm/mmap` — Google Maps / Location
- `/api/v6.17/iosevent` — OneSignal push
- `/distanceMatrix/v2:computeRouteMatrix` — Google Routes API
- `/a/t?m=a` — Usabilla / SurveyMonkey
- `/log/batch` — logging SDK
