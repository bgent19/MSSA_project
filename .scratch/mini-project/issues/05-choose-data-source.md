# Choose the game data source and seeding strategy

Type: grilling
Status: open
Assignee: claude + Brett (wayfinder session, 2026-08-11)
Blocked by: 02, 03, 09 (all closed — unblocked)

## Question

Where does game data come from in the sprint build, and what exactly ships as seed data?

Blocked on [Model the domain: games, collections, and plays](02-domain-model.md) — we can't
choose a source before we know what fields a `Game` needs — and on
[Survey board game data sources](03-survey-data-sources.md), which supplies the facts.

To settle:

- Static seed data, a live BoardGameGeek API call, or both (seed by default, API as a
  stretch goal)?
- If seeded: how many games, chosen how? A demo is more convincing with games the audience
  recognises, and more convincing still if the play history looks lived-in rather than
  freshly typed.
- Does the user add games by hand, pick from a catalogue, or both? This is a UX decision
  with real modelling consequences.
- Should seed data include *plays*, so the app has history to show on first run? A stats
  screen with one play on it demos badly.
- Where does seed data physically live — an embedded JSON file, or a C# static class? The
  rubric cares about data structure use; one of these shows more of it.
- If an API is in scope at all: what happens in the demo when it's slow or the venue's
  wifi is bad? Decide the fallback *now*, not on stage.

**Constraint added by [Model the domain](02-domain-model.md).** That ticket settled that
plays are independent of ownership — logging a game you don't own (conventions, cafés,
friends' copies) is a *frequent* case per Brett, not an edge case — and that games live in
a `GameCatalog` with **no ad-hoc creation path**. So a title missing from the catalog
cannot be logged at all. This ticket must answer for that: either the catalog is broad
enough that it rarely happens, or a "add a game not in the catalog" path gets added back
(and then decide whether such games persist into the catalog). Do not close this ticket
leaving that unaddressed.

**Constraints added by [Survey board game data sources](03-survey-data-sources.md)**
(full findings: [research/board-game-data-sources.md](../research/board-game-data-sources.md)):

- **The first bullet above is now a false trichotomy.** The survey found a fourth option
  that dominates: use the API **at build time only** — one offline seeding run with the
  token, commit the output, ship an app that reads local data. Real data and Brett's own
  LINQ-to-XML parsing code (rubric-relevant) without any live-demo network dependency.
  Treat this as the incumbent and make the live-API path argue its way in.
- **The API is no longer anonymous** and failure is *total*, not degraded — `401` on
  everything without a valid token. So "API as a stretch goal with seed as fallback" is not
  a graceful-degradation story; it's two independent code paths.
- **Parsing is cheap and rubric-positive**: ~20 lines of LINQ-to-XML, no NuGet, verified
  running on .NET 10. So "XML is hard" should not weigh against the API at all. No client
  library — it would take Brett's code off the page.
- **The unlisted-game gap has a brute-force answer**: BGG's CSV dump of every game
  (`/data_dumps/bg_ranks`) gives a catalog of thousands rather than twenty. Weigh that
  against a tighter curated demo.
- **Seed-data location is a live rubric question**: a `static readonly List<Game>` in C# is
  compile-time checked, cannot fail at runtime, and demonstrates collection/data-structure
  use directly; an embedded JSON file needs a DTO plus a deserializer call and adds a
  runtime failure mode. The survey leans C#; decide it here.
- **If a real collection import is in play**, note `/collection` is the one endpoint with
  the 202-retry queue, and that its default subtype mislabels expansions.

**Facts added by [Confirm BGG API access is a working token](09-verify-bgg-token.md).**
Access is real — approved application, bearer token, verified `200`. Username
**TheGentleBean**, collection **~40 games**. So build-time seeding is live rather than
hypothetical, and the ~40-game shelf is itself a candidate seed set: real, one API call,
small enough to hand-curate. This weakens the CSV-dump-of-thousands option, which existed
mainly to close the unlisted-game gap. The token is not yet stored anywhere; if seeding is
build-time-only it never needs to reach app configuration at all.

The live-demo failure mode is the thing to weigh most heavily. A dependency that fails in
front of the class costs more than the feature is worth.
