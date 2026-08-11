# Board game data sources — survey

Research asset for [Survey board game data sources](../issues/03-survey-data-sources.md).
Investigated 2026-08-10. All HTTP facts below were verified by live request from this
machine on that date, not taken from documentation alone.

**Headline:** the BGG XML API changed materially in 2025 — it is no longer anonymous.
Every endpoint now returns `401 Unauthorized` without a registered application token.
Brett already has API access, so this is not a blocker for us, but it invalidates every
tutorial and Stack Overflow answer written before mid-2025, and it kills the "just add an
API call as a stretch goal, no setup" assumption. It also means the API cannot be a
*fallback* — if the token is wrong, nothing works at all.

---

## 1. BoardGameGeek XML API v2

### Registration and authorization (the big change)

Live results, 2026-08-10:

| Request | Result |
|---|---|
| `GET /xmlapi2/thing?id=13` (no header) | `401` — `Unauthorized. See https://boardgamegeek.com/using_the_xml_api` |
| `GET /xmlapi2/search?query=catan` (no header) | `401`, same body |
| `GET /xmlapi/boardgame/13` (the **old** v1 API, no header) | `401`, same body |
| `GET /xmlapi2/thing?id=13` with a syntactically valid but bogus bearer token | `401`, empty body |

So: v1 and v2 are both gated, and the token is genuinely validated — presence alone isn't
enough. A browser User-Agent doesn't change anything, so this is a real auth gate, not
Cloudflare bot filtering.

Per [Using the XML API](https://boardgamegeek.com/using_the_xml_api) (page version date
2025-07-02):

- Register an application at `https://boardgamegeek.com/applications`. BGG warns approval
  "may be a week or more."
- Create tokens under that application, then send:
  ```
  Authorization: Bearer e3f8c3ff-9926-4efc-863c-3b92acda4d32
  ```
  Bearer tokens, currently no refresh requirement.
- **Domain matters.** Requests must go to `boardgamegeek.com` **without** the `www.`
  prefix — BGG explicitly calls out `www` as a cause of authorization failures. Same
  warning appears on the API2 wiki page.
- **Licence class.** Commercial vs non-commercial. A student project with no monetisation
  is squarely non-commercial: "A non-commercial license is generally provided at no cost."
- **Public-facing apps must display the "Powered by BGG" logo** linking back to BGG. A
  classroom demo running on localhost is arguably not public-facing, but the logo is a
  cheap bit of polish and shows the terms were read.
- **Exception worth knowing:** downloading *your own* collection while logged in needs no
  registration at all. Other users' collections work while logged in but are "heavily rate
  limited."

*Assumption to confirm with Brett:* "I already have BGG API access" is read here as **an
approved application with a bearer token in hand**. If it actually means "a BGG user
account," the week-plus approval wait puts the API outside a 5-hour sprint entirely, and
the recommendation below becomes mandatory rather than merely preferred.

### Rate limits

There is no published numeric limit. Three sources, in descending order of authority:

- **BGG's own guidance** ([Using the XML API](https://boardgamegeek.com/using_the_xml_api)):
  "We are still determining exact usage limits." Advice: make requests **server-side and
  cache the results**; client-side traffic "could be grounds for having your license
  suspended." Usage is monitored per application at
  `https://boardgamegeek.com/applications` → "Usage".
- **The API2 wiki** ([BGG_XML_API2](https://boardgamegeek.com/wiki/page/BGG_XML_API2)):
  throttling shows up as **HTTP 500 or 503**, not 429. "Currently, a 5-second wait between
  requests seems to suffice."
- **Community consensus** (forum threads, and the Python write-up at
  [drangovski.com](https://drangovski.com/posts/boardgamegeek-python-data-fetching/)):
  roughly **2 requests/second**, learned by getting throttled; 0.5s sleeps are the common
  mitigation.

Practical read: the wiki's 5 seconds is the safe number for a batch job, ~0.5s is what
people get away with. Either way, **seeding a 20-40 game catalog is a minutes-long batch
job, not something to do at app startup.**

### The 202 queue pattern

Confirmed, and it is **specific to `/collection`** — not to `thing` or `search`. From the
wiki: "if it's 202 (vs. 200) then it indicates BGG has queued your request and you need to
keep retrying (hopefully w/some delay between tries) until the status is not 202."

This is the trap people get caught by, and it matters here because "import Brett's actual
BGG collection" is the single most demo-friendly use of the API — and it is exactly the
endpoint with the retry loop. A first call to a cold collection commonly returns 202 and
needs a few seconds of polling.

Second `/collection` gotcha from the wiki: the default `subtype=boardgame` returns
expansions too, mislabelled as `boardgame`. The workaround is two calls —
`excludesubtype=boardgameexpansion`, then a second call for the expansions.

### Endpoints we'd actually use

Root: `https://boardgamegeek.com/xmlapi2/`

| Endpoint | Use | Key parameters |
|---|---|---|
| `/search` | find a game by title | `query=` (spaces → `+`), `type=boardgame`, `exact=1` |
| `/thing` | full detail by id | `id=` (**comma-delimited, max 20 per call**), `stats=1`, `versions=1`, `videos=1` |
| `/collection` | a user's owned games | `username=`, `own=1`, `stats=1`, `excludesubtype=` — **202 retry loop** |

`/thing` accepting 20 ids per call is the useful detail: a 40-game seed catalog is **two
HTTP requests**, not forty.

There is also a **CSV dump of every game** (id, name, year, rank, average rating) at
`https://boardgamegeek.com/data_dumps/bg_ranks`, which BGG calls "the preferred way" to get
all game names and ranks. It requires being logged in with an approved application — the
URL returns the HTML page, not the file, to an unauthenticated client. Worth flagging for
the data-source decision: this is the cheap route to a catalog of *thousands* of titles,
which is directly relevant to the unlisted-convention-game gap left open by
[the domain model](../issues/02-domain-model.md).

### What the XML looks like

`/xmlapi2/thing?id=224517&stats=1` returns, in outline:

```xml
<items termsofuse="...">
  <item type="boardgame" id="224517">
    <thumbnail>https://cf.geekdo-images.com/...__thumb/img/....jpg</thumbnail>
    <image>https://cf.geekdo-images.com/...__original/img/....jpg</image>
    <name type="primary" sortindex="1" value="Brass: Birmingham"/>
    <name type="alternate" sortindex="1" value="..."/>
    <description>Brass: Birmingham is an economic strategy game &amp;amp; sequel...</description>
    <yearpublished value="2018"/>
    <minplayers value="2"/>
    <maxplayers value="4"/>
    <playingtime value="120"/>
    <minplaytime value="60"/>
    <maxplaytime value="120"/>
    <minage value="14"/>
    <link type="boardgamecategory" id="1021" value="Economic"/>
    <link type="boardgamemechanic" id="2040" value="Hand Management"/>
    <link type="boardgamedesigner" id="10" value="Gavan Brown"/>
    <link type="boardgamepublisher" id="34188" value="Roxley"/>
    <statistics page="1">
      <ratings>
        <usersrated value="47932"/>
        <average value="8.41297"/>
        <ranks>
          <rank type="subtype" name="boardgame" friendlyname="Board Game Rank" value="1" .../>
        </ranks>
        <averageweight value="3.9106"/>
      </ratings>
    </statistics>
  </item>
</items>
```

Three shape gotchas, each of which costs a beginner twenty minutes:

1. **Values live in `value=` attributes, not element text.** `(int)item.Element("minplayers")`
   throws; you need `.Attribute("value")`. `<description>`, `<thumbnail>` and `<image>` are
   the exceptions — those *are* element text.
2. **A game has many `<name>` elements.** You must filter to `type="primary"`.
3. **`<description>` is double-encoded.** The XML contains `&amp;amp;` and `&amp;#10;`, so
   XML decoding leaves you holding `&amp;` and `&#10;`. It needs a second pass through
   `WebUtility.HtmlDecode` or the description renders with visible entities.

### Parsing cost in C# — measured, not estimated

Written and **run on .NET 10 (`dotnet run parse.cs`)** against a representative response.
The complete XML→domain mapping, including categories and the decode fix:

```csharp
var games = doc.Root!.Elements("item").Select(item => new Game(
    BggId:      (int)item.Attribute("id")!,
    Title:      item.Elements("name").First(n => (string?)n.Attribute("type") == "primary")
                    .Attribute("value")!.Value,
    Year:       (int?)item.Element("yearpublished")?.Attribute("value"),
    MinPlayers: (int)item.Element("minplayers")!.Attribute("value")!,
    MaxPlayers: (int)item.Element("maxplayers")!.Attribute("value")!,
    PlayTimeMinutes: (int)item.Element("playingtime")!.Attribute("value")!,
    Description: WebUtility.HtmlDecode(item.Element("description")?.Value ?? ""),
    ImageUrl:   (string?)item.Element("thumbnail"),
    Categories: item.Elements("link")
                    .Where(l => (string?)l.Attribute("type") == "boardgamecategory")
                    .Select(l => l.Attribute("value")!.Value)
                    .ToList()
)).ToList();
```

**~20 lines, one `using System.Xml.Linq;`, zero NuGet packages, verified working.**

Findings that bear on the decision:

- **XML is not meaningfully harder than JSON here.** `XDocument` needs no schema, no
  generated classes, no attribute-decorated DTOs — less ceremony than
  `System.Text.Json` would need for the same mapping, because `System.Text.Json` would want
  a DTO hierarchy mirroring the whole document. The explicit-cast operators on `XElement`
  and `XAttribute` do the type conversion.
- **This code scores well against the rubric.** It is `Where`/`Select`/`First` over a
  hierarchy into a typed collection — LINQ and data-structure use, written by Brett, not a
  library call. That is a genuine point in the API's favour that has nothing to do with
  whether the app calls it live.
- **A .NET client library would be the wrong move.** Some exist (`BggApi`, various
  wrappers), but most predate the 2025 auth change and won't set the `Authorization`
  header. More importantly, taking a dependency here *removes* Brett's code from the page —
  the exact opposite of what a fundamentals rubric rewards. 20 lines of our own beats a
  NuGet reference.

### Failure modes in a live demo

Honest list, worst first:

- **Bad or revoked token → total failure.** Not degraded results — `401` on everything.
  Unlike the pre-2025 API, there is no anonymous fallback path.
- **No/poor venue wifi → total failure.** A classroom presentation is precisely the wrong
  place to depend on outbound HTTPS.
- **Throttling → 500/503** mid-demo if the app fires several requests in quick succession
  (e.g. the audience clicks around a search screen). Recoverable but ugly.
- **Latency.** `search` + `thing` is two sequential round trips before anything renders; a
  cold `/collection` adds a 202 polling loop on top. A spinner on stage is dead air.
- **BGG-side outages.** No SLA, no support: "No technical support is available for the XML
  API."

---

## 2. Static seed data

The incumbent — the guidelines say "Mock data source strongly suggested."

**Setup cost:** near zero, and it is *fixed* cost with no runtime risk. **Failure modes in
a demo:** none.

Fields needed to make the app feel real, given the domain model:
title, BGG id (harmless to carry, and it makes a later API swap trivial), year, min/max
players, playing time, a category or two, and a thumbnail URL. Thumbnails are the single
highest-impact field — a grid of box art reads as a real product; a list of bare titles
reads as a homework exercise. Note that BGG image URLs are hotlinkable and stay valid, so
seed data can carry them without shipping any binary assets.

**Where it lives — JSON file vs C# static class.** Genuinely a rubric question, and it cuts
against the intuitive answer:

- An **embedded JSON file** requires deserialization code (`System.Text.Json`), which is a
  library call, plus a DTO. Slightly more machinery, slightly less of Brett's own logic,
  and a runtime failure mode (file not found / not marked as embedded resource) that has
  bitten every beginner at least once.
- A **C# static class** — a `static readonly List<Game>` built with collection and object
  initialisers — is compile-time checked, cannot fail at runtime, and *is itself* a
  demonstration of data-structure and collection use. It also means the seed can construct
  domain objects directly through their real constructors, exercising the invariants from
  the domain model.

The C# route is less "professional-looking" and more rubric-aligned. Worth arguing out in
[the data source decision](../issues/05-choose-data-source.md).

**The weakness:** 20 hand-picked games can't cover an arbitrary convention title, which is
the gap the domain model handed downstream.

---

## 3. Alternative APIs

Checked live on 2026-08-10; both failed to connect from this machine, while
`boardgamegeek.com` resolved and responded normally in the same run — so this is about
those hosts, not local networking.

- **Board Game Atlas** (`api.boardgameatlas.com`) — no connection. It was the standard
  free JSON alternative and has been widely reported as shut down since 2023. Search
  results still surface its docs pages; they are stale. **Do not plan around it.**
- **bgg-json.azurewebsites.net** (community JSON proxy over BGG) — no connection. Even
  when up, its own README warns it runs on a free Azure tier with "extremely low quotas…
  not viable for production purposes." It would also now need to carry someone's BGG token.
- **Commercial JSON wrappers** (e.g. TCGAPIs) exist and are paid. Out of proportion for a
  5-hour student project.

**Conclusion: there is no live, free, unauthenticated board game API in 2026.** BGG's own
API is the only real option, and it now requires a token.

---

## 4. What this means for the decision

The ticket asked what an API would have to be worth to displace static seed data. Stated
plainly:

**The API's value is almost entirely at build time, not at run time.**

Everything the API is good for — real titles, accurate player counts, box art, a catalog
big enough to contain that convention game — is delivered just as well by data *fetched
once, now, and committed to the repo*. Everything the API is bad at — network dependency,
throttling, latency, token failure, 202 polling — is incurred only by calling it *live,
during the demo*.

That points at a third option the ticket didn't list, which dominates both:

> **Use the token now, offline, as a one-off seeding step.** Run a throwaway console app
> (or the parse code above) against `/collection?username=<brett>&own=1` and/or two
> `/thing?id=<20 ids>` calls, map the XML with LINQ, and emit the seed catalog. Commit the
> result. The sprint app then reads only local data.

This keeps every advantage — real data, real box art, Brett's own LINQ-to-XML parsing code
on the page for the rubric, and a genuinely honest "this data came from the BoardGameGeek
API" line in the presentation — while the demo itself has zero network dependency. It also
sidesteps the licence question almost entirely, since nothing is public-facing.

The live-API path should be judged as what it is: a stretch goal that adds demo risk and
roughly a dozen lines of `HttpClient` plumbing, on top of parsing code that has to exist
either way.

Two things this survey could not settle, both for
[Choose the game data source](../issues/05-choose-data-source.md):

1. Whether Brett's "API access" is an approved application with a token in hand, or a BGG
   account. The week-plus approval queue makes this decisive.
2. Whether the catalog should be ~20 curated games or the full CSV dump. The dump closes
   the unlisted-game gap by brute force; 20 curated games keep the demo tight. That is a
   product decision, not a research one.

---

## Sources

- [Using the XML API](https://boardgamegeek.com/using_the_xml_api) — BGG, page version date 2025-07-02 (registration, licences, tokens, usage limits)
- [BGG XML API2 wiki](https://boardgamegeek.com/wiki/page/BGG_XML_API2) — endpoints, parameters, rate limit, 202 collection behaviour, CSV dump
- [Registration and Authorization coming to the XML API](https://boardgamegeek.com/thread/3492262/registration-and-authorization-coming-to-the-xml-a) — announcement thread
- [XML API: Read this for uninterrupted access](https://boardgamegeek.com/thread/3539581/xml-api-read-this-for-uninterrupted-access) — enforcement thread
- [BoardGameGeek data fetching with Python](https://drangovski.com/posts/boardgamegeek-python-data-fetching/) — community rate-limit practice, XML element names
- [Updated API Rate Limit Recommendation?](https://boardgamegeek.com/thread/2388502/updated-api-rate-limit-recommendation) — forum discussion of the 2 req/s figure
- [bgg-json](https://github.com/ervwalter/bgg-json) — community JSON proxy, free-tier caveat
- Live `curl` probes and a `dotnet run` parse test executed 2026-08-10 (findings inline above)
