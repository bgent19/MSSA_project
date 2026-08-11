# Survey board game data sources

Type: research
Status: closed
Resolved: 2026-08-10
Blocked by: —

## Question

Where could board game data come from, and what would each option actually cost in sprint
hours?

Fact-finding only — the decision itself is
[Choose the game data source and seeding strategy](05-choose-data-source.md). This ticket
exists so that decision is made against real facts rather than assumptions about what an
API is like.

Investigate:

- **BoardGameGeek XML API v2** — the obvious candidate. What are the endpoints for search
  and thing-by-id? What does the XML actually look like? Is there rate limiting, and does
  it use the "202 accepted, retry shortly" queue pattern people get caught by? Are there
  terms of use restricting non-commercial or academic use? Is an API key needed?
- **Parsing cost in C#** — XML rather than JSON. What does `System.Xml.Linq` (XDocument)
  cost in lines versus `System.Text.Json`? Is there a maintained .NET client library, and
  is depending on one wise for a graded fundamentals project?
- **Static seed data** — hand-authored JSON or a C# collection of ~20 well-known games. What
  fields would it need to make the app feel real in a demo?
- **Any alternative APIs** worth knowing about, and whether they're live and free.

For each option, report honestly: setup time, failure modes during a live demo (offline
venue, rate limit, slow response), and how much of the resulting code would be Brett's
own versus library calls.

Note the guidelines explicitly say "Mock data source strongly suggested" — so the research
should treat static seed data as the incumbent and ask what an API would have to be worth
to displace it.

Deliverable: a markdown summary saved in the repo and linked from the answer.

---

## Resolution (2026-08-10)

Full findings: [research/board-game-data-sources.md](../research/board-game-data-sources.md).
Everything below was verified by live request or by running code on this machine, not read
off documentation.

**The API changed under us.** Since mid-2025 the BGG XML API is no longer anonymous. Both
v2 (`/xmlapi2/…`) and the old v1 (`/xmlapi/…`) return `401 Unauthorized` to every
unauthenticated request; a syntactically valid but bogus bearer token also gets `401`, so
the token is really validated. Access needs a registered application at
`boardgamegeek.com/applications` — BGG warns approval "may be a week or more" — and an
`Authorization: Bearer <uuid>` header, sent to `boardgamegeek.com` **without** the `www`
prefix. Non-commercial licences are free. Two consequences: every pre-2025 tutorial is
wrong, and the API can never be a *fallback*, because failure is total rather than
degraded.

**Rate limits are unpublished.** BGG says only "we are still determining exact usage
limits," advises server-side calls with caching, and monitors per-application usage.
Throttling surfaces as **500/503, not 429**; the wiki suggests a 5-second gap between
requests, the community runs at ~2/sec. The **202-queue pattern is real but confined to
`/collection`** — `thing` and `search` don't do it. Either way, seeding is a batch job, not
a startup task. `/thing` takes up to **20 ids per call**, so a 40-game catalog is two HTTP
requests.

**XML parsing is a non-issue, and is actually an asset.** The complete XML→domain mapping
is **~20 lines of LINQ-to-XML, one `using`, zero NuGet packages** — written and run
successfully on .NET 10 (code in the research doc). It is *less* ceremony than
`System.Text.Json`, which would want a DTO hierarchy. It is also `Where`/`Select`/`First`
over a hierarchy into a typed collection — i.e. it scores directly on the rubric's LINQ and
data-structure items, in Brett's own code. **A .NET client library would be the wrong
call**: most predate the auth change, and a dependency removes Brett's code from the page.
Three gotchas cost beginners real time and are documented in the research: values live in
`value=` attributes not element text; a game has multiple `<name>` elements (filter to
`type="primary"`); and `<description>` is *double*-encoded, needing `WebUtility.HtmlDecode`
after XML decoding.

**There is no free unauthenticated alternative in 2026.** Board Game Atlas and the
bgg-json community proxy both failed to connect, in a run where boardgamegeek.com
responded normally. Commercial JSON wrappers are paid. BGG is the only real source.

**The finding that should drive [ticket 05](05-choose-data-source.md):** the API's value is
almost entirely at **build time**, not run time. Real titles, accurate player counts, box
art and a broad catalog are delivered just as well by data fetched once now and committed;
the network dependency, throttling, latency and token risk are incurred *only* by calling
it live on stage. So the option the ticket didn't list dominates both of the ones it did —
**use the token now, offline, as a one-off seeding step**, commit the result, and have the
sprint app read local data only. That keeps the real data, keeps Brett's LINQ-to-XML
parsing code on the page for the rubric, supports an honest "this came from the BGG API"
line in the presentation, and leaves the demo with zero network dependency.

Also flagged for 05: BGG publishes a **CSV dump of every game** (id, name, year, rank,
average) at `/data_dumps/bg_ranks`, downloadable with an approved application. That is the
brute-force answer to the unlisted-convention-game gap left open by
[the domain model](02-domain-model.md) — a catalog of thousands rather than twenty.

**One thing this ticket could not settle**, now split out as
[Confirm BGG API access is a working token](09-verify-bgg-token.md): whether Brett's
existing "API access" is an *approved application with a token in hand* or a BGG *account*.
The week-plus approval queue makes the difference decisive for a 5-hour sprint, and it
blocks 05.
