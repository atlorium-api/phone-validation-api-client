# Phone Number Validation API — E.164 normalization, carrier lookup, mobile vs landline detection

[Русский](README.md) · **English**

[![Live API tests](https://github.com/atlorium-api/phone-validation-api-client/actions/workflows/examples.yml/badge.svg)](https://github.com/atlorium-api/phone-validation-api-client/actions/workflows/examples.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![API](https://img.shields.io/badge/API-Swagger-brightgreen)](https://atlorium.com/phoneAPI)

Ready-to-run examples for the **phone number validation API** in six languages: **Python, TypeScript (Node.js), Go, Java, C#, PHP.**
**Phone number lookup** for any country: is the number valid, how is it correctly written (**E.164**, international, national, RFC 3966), which country it belongs to, whether it is **mobile or landline**, which carrier the range was allocated to, and which time zones it covers.

Parsing runs **locally**, against a built-in world numbering plan — no carrier calls, no network wait, an answer in single-digit milliseconds.

Every example **runs out of the box — no signup, no key, no card.** A public demo key is baked in.

```bash
git clone https://github.com/atlorium-api/phone-validation-api-client
cd phone-validation-api-client/python && pip install -r requirements.txt && python main.py
```

> The demo key returns **mocks**, not a real parse. The mock never loads the numbering library: it always answers `isValid: true` and `numberType: Mobile`, and builds `e164` straight from the digits you sent. So in the sandbox a Moscow landline is not detected as `FixedLine`, a premium-rate number is not detected as `PremiumRate`, and `8 916 123-45-67` does not collapse into its `+7 916 …` twin — the mock does not know that a leading `8` means `+7`. We explain that rather than tune the example to look pretty. Swap in a live key and the same code — unchanged, line for line — returns a real parse.

---

## What it is for

Validate a phone number in a signup form *before* spending an SMS on it. Clean a contact list before a campaign. Normalize phone numbers in a CRM so the same person is not stored three times. Tell mobile from landline before choosing a channel. Know the subscriber's time zone so you do not call at 3 a.m.

The examples do not just print JSON — they **apply** it. Each ships a `normalizeContactList()` function that prepares a contact list for an **SMS campaign**:

| Input | Decision | Why |
|---|---|---|
| `isValid: false` | **DROP** | The number is outside any allocated range. The SMS goes nowhere and you still pay for it |
| `numberType: PremiumRate` | **DROP** + warning | Premium-rate numbers are billed at a higher rate; sending to them by accident burns budget |
| `numberType: FixedLine` | **DROP** for SMS | A landline cannot receive SMS — but it is still a live contact, so it is flagged as callable by voice |
| `numberType: Voip` | **FLAG** | Virtual numbers are often disposable: elevated fraud risk during verification |
| `numberType: Mobile`, `isValid: true` | **KEEP** | Normalized to **E.164**, the only format SMS gateways accept. `timeZones` is kept so you do not text people at night |

Plus **deduplication**: `8 916 123-45-67` and `+7 916 123 45 67` are the same person stored as two different rows. Once normalized to E.164 they collapse into one contact. The sender pays for every redundant SMS, so dedup is money, not cosmetics.

Finally, a summary: how many to send, how many dropped, how many duplicates collapsed, and a breakdown by country and number type.

## What this service does NOT do

This matters more than the feature list, so it comes before the quick start rather than in fine print at the bottom.

> **The service answers "can this number exist, and how is it correctly written". It does NOT check whether a live subscriber uses the number, and it reveals nothing about the owner.**
>
> - **"Valid" does not mean "in service".** `isValid: true` means exactly one thing: the number is well-formed and falls inside a range actually allocated in its country. It may never have been sold, may be disconnected, or may be sitting switched off in a drawer.
> - **Network activity, the current carrier, number portability (MNP), roaming, MCC/MNC, IMSI — none of these are returned.** They are personal data about a specific subscriber, obtainable only by querying the carrier's network, which this service never does. It is offline by design.
> - **`originalCarrier` is the carrier the RANGE was allocated to**, not necessarily the current one. Subscribers can port a number to another carrier and keep it (MNP); a numbering plan cannot know about that. For an arbitrary number, treat this field as an educated guess. Do not build call billing or SMS routing on it.
> - **`location` describes the range, not the person.** The owner of a Moscow number may be drinking coffee in Lisbon. For mobile numbers this field is usually `null` — an honest answer, not missing data.
>
> **The only Atlorium service that truly verifies a recipient exists is for email** — [Email verification](https://github.com/atlorium-api/email-verification-api-client) actually talks to the mail server and checks whether the mailbox is there. There is no equivalent for phone numbers, here or — frankly — at most vendors who claim otherwise.

## Quick start

Try the API without cloning anything:

```bash
curl -H "Authorization: Bearer ak_sandbox_demo_mockdata_v1" \
     "https://atlorium.com/api/Phone/%2B79161234567"
```

Note the `%2B` — that is how `+` must be encoded. Sent raw, many web servers and proxies read it as a space, the number loses its country code, and parsing falls apart. All six examples encode it correctly.

| Language | Run | Requires |
|----------|-----|----------|
| [Python](python/) | `pip install -r requirements.txt && python main.py` | Python 3.10+ |
| [TypeScript / Node.js](node/) | `npm install && npm start` | Node.js 20+ |
| [Go](go/) | `go run .` | Go 1.22+ |
| [Java](java/) | `java Main.java` | JDK 17+ (no dependencies) |
| [C#](csharp/) | `dotnet run` | .NET 8+ |
| [PHP](php/) | `php main.php` | PHP 8.1+ |

Pass your own numbers as a comma-separated list:

```bash
python main.py "+79161234567,8 495 785-63-00,+1 650 253 0000"
```

## Authentication

The key goes in the `Authorization` header:

```
Authorization: Bearer YOUR_KEY
```

| Key | Behaviour |
|-----|-----------|
| `ak_sandbox_demo_mockdata_v1` | **Demo key.** Public, shared by everyone. Returns mocks, charges nothing, needs no account. Responses are deterministic, so you can assert on them in tests. |
| Live key | A real parse. Get one at [atlorium.com](https://atlorium.com) |

Switching to a live key requires **no code changes** — every example reads an environment variable:

```bash
export ATLORIUM_API_KEY="ak_your_live_key"
```

Every sandbox response carries the header `X-Atlorium-Sandbox: true`, so mock data can never be mistaken for real data.

## Endpoints

Base URL: `https://atlorium.com`

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/api/Phone/{number}` | Parse a number: validity, formats, country, type, range carrier, time zones |

### `GET /api/Phone/{number}`

| Parameter | In | Type | Description |
|-----------|----|------|-------------|
| `number` | path | string | **Required.** International format (`+79161234567`) or national (`8 916 123-45-67`). Spaces, brackets and dashes are fine. **`+` must be encoded as `%2B`.** |
| `countryCode` | query | string | Optional. ISO-3166 alpha-2 (e.g. `RU`) — the country in which to interpret a number written **without `+`**. Ignored for numbers with `+`. |

Two equivalent requests:

```
GET /api/Phone/%2B79161234567
GET /api/Phone/89161234567?countryCode=RU
```

A number without `+` and without `countryCode` has no country to be resolved against — the service answers `400`.

## Response fields

The payload is nested inside the `number` object — do not miss that level when parsing.

| Field | Type | Meaning |
|-------|------|---------|
| `input` | string | The number exactly as you sent it |
| `number` | object | The parse result — every field below lives inside it |
| `elapsedMs` | number | Parse time in milliseconds |

Inside `number`:

| Field | Type | Meaning |
|-------|------|---------|
| `isValid` | bool | **The key field.** The number falls inside a range actually allocated in its country — not merely the right length. Does not imply a live subscriber |
| `isPossible` | bool | The number merely *looks* like a phone number (plausible length), but its range may not be allocated. Softer than `isValid`. If `false`, it is definitely not a phone number |
| `e164` | string | **Canonical format:** `+79161234567`. Store this in your database and feed it to SMS gateways |
| `international` | string | Human-readable international format: `+7 916 123-45-67` |
| `national` | string | National format as written in the number's own country: `8 (916) 123-45-67` |
| `rfc3966` | string | RFC 3966 dial link: `tel:+7-916-123-45-67`. Drop straight into an `href` |
| `countryCallingCode` | number | Country calling code without the plus: `7`, `1`, `44` |
| `countryCode` | string | ISO-3166 alpha-2 country code: `RU`. For codes shared by several countries (`+7` Russia/Kazakhstan, `+1` USA/Canada) the country is resolved from the following digits |
| `country` | string | Country name |
| `numberType` | string | Number type — see the table below |
| `originalCarrier` | string | The carrier the **range** was allocated to. **Not necessarily the subscriber's current carrier** — see "What this service does NOT do". `null` if unknown |
| `location` | string | Geographic binding of the **range**, not of the subscriber. Usually `null` for mobile numbers |
| `timeZones` | array | IANA time zones of the range: `["Europe/Moscow", "Asia/Yekaterinburg"]`. Mobile numbers in large countries span several — the subscriber may be in any of them |

### `numberType` values

| Value | Meaning |
|-------|---------|
| `Mobile` | Mobile number |
| `FixedLine` | Landline |
| `FixedLineOrMobile` | The country's numbering plan does not separate the two (e.g. the USA) |
| `TollFree` | Free for the caller (8-800 and international equivalents) |
| `PremiumRate` | Premium-rate call |
| `SharedCost` | Cost shared between caller and callee |
| `Voip` | IP telephony |
| `PersonalNumber` | Personal number with forwarding |
| `Pager` | Pager |
| `Uan` | Universal access number, usually corporate |
| `Voicemail` | Voicemail |
| `Unknown` | The numbering plan does not know this range |

## Error handling

| Code | Cause | What to do |
|------|-------|------------|
| `400` | Not a phone number: wrong format, implausible length, or written without `+` while `countryCode` was omitted | Check the number, and pass `countryCode` for national notation |
| `401` | Key missing, expired or invalid | Check the `Authorization` header |
| `402` | Insufficient credit balance | Top up at [atlorium.com](https://atlorium.com) |
| `429` | Rate limit exceeded | Retry with backoff |
| `503` | Service temporarily unavailable: scheduled maintenance | Retry later. **You are not charged for our failures** |

There is no `404` here: a string either parses as a number or it is not a number at all — and then it is a `400`. A `503` only happens during scheduled platform maintenance: the service itself is offline, so there is no external source to fail. The `message` of such a response links to the [status page](https://atlorium.com/status).

**A string that is not a phone number is not billed.** It is rejected before a credit is reserved — you do not pay because someone typed "call the office" into a form.

All six examples map these codes to human-readable causes — see the `AtloriumError` class.

## Pricing

**Pay-as-you-go, no subscription** — you pay per successful request. Parsing is local, so this is one of the cheapest services in the catalogue. Current prices: **[atlorium.com/pricing](https://atlorium.com/pricing)**

## Other Atlorium APIs

Phone numbers are rarely validated alone — usually the whole contact record gets cleaned at once. The same account and the same key also give you:

- [Email verification](https://github.com/atlorium-api/email-verification-api-client) — syntax, MX records, disposable addresses
- [Address standardization](https://github.com/atlorium-api/address-standardization-api-client) — parse a string into components, quality score
- [AI chat](https://github.com/atlorium-api/ai-chat-api-client) — models, sessions, text summarization
- [GAR/FIAS addresses](https://github.com/atlorium-api/gar-fias-address-api-client) — search and suggestions from the official registry
- [Image moderation](https://github.com/atlorium-api/image-moderation-api-client) — content, objects and text ON the image
- [Russian test data generator](https://github.com/atlorium-api/test-data-generator-api-client) — fictional INN, SNILS, OGRN and accounts with valid checksums

Full catalogue: [atlorium.com](https://atlorium.com)

## Links

- **API reference (Swagger):** [atlorium.com/phoneAPI](https://atlorium.com/phoneAPI)
- **OpenAPI spec:** [phone_en-US.json](https://atlorium.com/openapi/phone_en-US.json)
- **Support:** support@atlorium.com

## License

[MIT](LICENSE)
