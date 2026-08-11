# Choose the game data source and seeding strategy

Type: grilling
Status: closed
Resolved: 2026-08-11
Assignee: claude + Brett (wayfinder session, 2026-08-11)
Blocked by: 02, 03, 09 (all closed)

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

**Constraints added by [Prototype the screens and the demo click path](07-prototype-screens.md).**
That ticket settled a **populated start** — the app opens full, roughly 18 games and ~40 plays
across several months, with one deliberate gap filled live on stage. Two consequences land
directly on this ticket, and the fourth bullet above ("should seed data include plays?") is
now answered *yes* rather than open:

- **Seed data must include plays, not just games.** ~40 of them, dated across several months
  so `RecentFirst()` and `MostPlayed()` have something to chew on. Volume is the point:
  filtering four rows does not read as filtering.
- **The catalog must contain games that are *not* in the collection.** The demo's strongest
  beat picks an unowned game in the log form and then shows it on the stats screen as
  "played but not owned". That makes the unlisted-game gap from
  [Model the domain](02-domain-model.md) a *demo requirement*, not just a modelling worry —
  a catalog identical to the collection kills the beat.

**Facts added by [Confirm BGG API access is a working token](09-verify-bgg-token.md).**
Access is real — approved application, bearer token, verified `200`. Username
**TheGentleBean**, collection **~40 games**. So build-time seeding is live rather than
hypothetical, and the ~40-game shelf is itself a candidate seed set: real, one API call,
small enough to hand-curate. This weakens the CSV-dump-of-thousands option, which existed
mainly to close the unlisted-game gap. The token is not yet stored anywhere; if seeding is
build-time-only it never needs to reach app configuration at all.

The live-demo failure mode is the thing to weigh most heavily. A dependency that fails in
front of the class costs more than the feature is worth.

## Answer

**Build-time seeding, no runtime network at all.** The BGG API is used once, offline, on
Brett's machine; its output is committed as C# source; the running app never opens a socket.

### The seven decisions

| # | Question | Decision |
|---|---|---|
| 1 | Runtime data source | **Build-time seed only.** No live API path, no `HttpClient` in the web app |
| 2 | Catalog width | **~200 games** — Brett's ~40 owned, plus BGG's top-ranked to fill |
| 3 | Seed location | **C# static class** — a generated `static readonly List<Game>` |
| 4 | Seeded plays | **Yes, ~60-80** over the past ~12 months, fixed data, not randomly generated |
| 5 | Game selection UX | **Search box filtering the catalog**, one component reused by two flows |
| 6 | Seeder code | **Committed console project** in the solution, unreferenced by the web app |
| 7 | Expansions | **Excluded** from the catalog entirely |

### Why build-time seeding won

Ticket 03 nominated it as incumbent and nothing argued its way past it. The decisive framing:
**build-time seeding keeps the rubric-positive parsing code and discards only the runtime
risk.** Brett still writes the LINQ-to-XML by hand and still hits the real API — just on his
own machine, once, where a failure costs a retry instead of a demo.

The live-API alternative was rejected on failure mode, not difficulty. Ticket 03 established
that BGG failure is *total* (`401` on everything) rather than degraded, and that throttling
surfaces as `500`/`503`. So a live call in front of the class has no graceful path: it works
or you get a blank screen and nothing to say. Against that, the rubric — branching, loops,
methods, classes, OOP design, data structures — rewards an `HttpClient` call with precisely
nothing.

### Why the catalog is wider than the shelf (the ticket-02 gap, closed)

This ticket was forbidden to close while the unlisted-convention-game gap stayed open. It is
closed by **width, not by an escape hatch**.

Ticket 02's central finding was that plays are independent of ownership — café, convention
and friends'-copy plays are frequent. But it also closed the catalog to ad-hoc creation. Put
together, a catalog of only Brett's 40 games would mean **you can only log games you own**,
making the model's most interesting property impossible to demonstrate. Seeding BGG's
top-ranked games alongside the owned shelf makes the catalog visibly wider than the
collection, so logging an unowned game is a natural demo beat rather than a blocked path.

**No manual-entry fallback.** Ticket 02's "no ad-hoc creation path" stands. The presentation
answer to *"what if you played something not in the list?"* is that the catalog is seeded
from BGG's top 200 for demo size and the real version seeds BGG's full catalog — a
data-volume choice, not a design limitation. No code required to defend it.

### The CSV dump found a role — as an id source, not as catalog data

The dump (`/data_dumps/bg_ranks`) was proposed as a brute-force catalog of thousands. It
loses that job on a hard technical fact: **it carries only id, name, year, rank and average
rating — no player counts.** Ticket 02 put a seat-count invariant on `Play` (no more players
than the game seats), so a dump-sourced catalog could not enforce Brett's own invariant
without a second fetch anyway.

It keeps a smaller and better job: **supplying the top-ranked ids** to feed `/thing`, and its
`is_expansion` column does the expansion filtering for free.

### The seeding pipeline

1. Download the CSV dump; filter out `is_expansion`; take the top ids by rank.
2. `GET /collection?username=TheGentleBean&own=1&excludesubtype=boardgameexpansion`
   → Brett's ~40 owned ids. **This is the one endpoint with the 202-retry queue** — expect a
   `202`, wait, ask again.
3. Union the two id sets and dedupe. Expect **heavy overlap** — a collector's shelf skews
   toward well-ranked games — so pull enough ranked ids to reach ~200 *unique* after merge.
4. `GET /thing?id=<20 comma-separated ids>&stats=1`, batched 20 at a time, **5 seconds
   between calls**. ~200 games is ~10 calls, under a minute.
5. Parse with LINQ-to-XML (~20 lines, no NuGet, verified on .NET 10 by ticket 03) and emit
   the C# static class.
6. Generate the play history (below) and emit it the same way.
7. Commit the generated files. Hand-curate the ~40 owned rows by eye — small enough to check.

### Why C# rather than JSON

Same principle that settled decision 1: **a `static readonly List<Game>` cannot fail at
runtime.** A typo is a build error on Brett's machine days before the demo. Embedded JSON has
two silent failure modes — a wrong build action yields a null stream, and schema drift throws
on startup — and both end in a blank app in front of the class.

The size objection doesn't survive arithmetic: at one line per game with a compact
constructor, ~200 games is **~200 lines**, not a 1,500-line monster. It also needs no DTO and
no deserializer call, and it demonstrates collection initialisers and object construction
directly.

Noted against this: JSON separates data from code and is closer to real DB seeding for map
two's EF Core work. That's a fair point and a fair thing for map two to revisit.

### Why the play history ships seeded — and what must be in it

An empty stats screen doesn't read as *new*, it reads as *broken*, and a stats view driven by
LINQ over play history is exactly where invisible OOP work becomes visible to a grader.

Two constraints on the generated history:

- **It must contain plays of games in the catalog but not in the collection** — 3-5 is
  enough. This is the only place ownership-independence becomes *visible*; if every seeded
  play is of an owned game, the demo cannot show what the domain model was built around.
- **It must be fixed data, not randomly generated at startup.** Random generation gives a
  different app every run, makes the demo unrehearsable, and adds a runtime path that can
  throw.

Weight the distribution so favourites recur — a flat one-play-per-game history looks
generated, which defeats the point.

Brett still logs **one play live** during the demo. Seeded history and the live write path
are complementary, not alternatives.

### Why the seeder is committed

A catch the earlier tickets missed. Build-time seeding was justified partly on the parsing
code being rubric-positive — but that only holds **if the parsing code is in the graded
deliverable.** A throwaway script would have quietly surrendered the benefit the decision was
argued on.

So it lives as a console project in the solution (`MeepleLedger.Seeder` or similar), run by
hand and **not referenced by the web app** — so the zero-runtime-risk property survives
intact. The ~5 minutes of project scaffolding is ceremony and falls to Claude under the
coaching contract; the ~20 lines of LINQ-to-XML inside it are Brett's.

### Why expansions are excluded

Ticket 02's model has no expansion type and no base-game relationship. An expansion in the
catalog would be a broken citizen: player counts inherited from a base game it has no link
to, making the seat invariant unreliable, and "log a play of Wingspan: European Expansion" is
really a play of Wingspan. Adding the relationship properly is real scope this sprint can't
afford. Filtering costs one query parameter and one column check.

Brett's owned count may come in slightly under 40 as a result. That's fine — the catalog is
~200 either way.

### Consequences for other tickets

- **[Prototype the screens](07-prototype-screens.md)** — the search-box picker is a settled
  component appearing in two flows, and the app opens onto *populated* data, not an empty
  state. Both constrain the screen inventory and the demo click path.
- **[Write the hour-by-hour sprint plan](08-sprint-plan.md)** — seeding must be complete
  before sprint hour 1 or the app has no data, and the solution now has two projects.
- **[Run the seeding pipeline and commit the seed data](11-run-seeding-pipeline.md)** —
  created by this ticket. The pipeline above has never been executed end to end.
- **Token storage** (loose end from ticket 09) is now resolved by implication: seeding is
  build-time only, so the token never reaches app configuration. It lives in the seeder's
  environment or user-secrets, and never in a commit.
