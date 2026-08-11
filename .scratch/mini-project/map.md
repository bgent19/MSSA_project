# Map: Mini Project sprint plan

Label: `wayfinder:map`

## Destination

Every technical decision for the 5-hour MSSA Mini Project is made, written down, and
understood by Brett — stack, domain model, data source, storage seam, screens, and an
hour-by-hour plan — so the sprint itself is pure execution with nothing left to decide.

The map is done when Brett could sit down, start the clock, and never once have to stop
and think about *what* to build or *how* to structure it.

## Notes

**Domain.** A web app that tracks a user's board game collection and lets them log plays of
those games. Long term (later maps, not this one) it grows AI chatbots for curation and
rules help, plus a price tracker.

**This is a graded academic deliverable.** MSSA Mini Project: solo, ~5 hours of coding
effort budgeted (guidelines say 8-12; we plan to 5 as the safe number), ending in a demo
and presentation. Guidelines live at `MiniProject Guidelines.docx` in the repo root.

**The rubric grades fundamentals, not architecture.** Explicitly: branching, loops,
methods, classes, OOP design, data structure use. Nothing rewards auth, cloud, or database
sophistication. Guidelines say "Mock data source strongly suggested. Data Base okay, but
don't let it interfere with rest of implementation." Every scoping decision on this map
should ask: *does this put more of Brett's own code on the page?*

**Settled while charting** (constraints for every ticket, not open questions):

- **Framework: Blazor Server** — a .NET 10 Blazor Web App with the `InteractiveServer`
  render mode. Chosen because Brett has solid OOP and HTML/CSS but no .NET web
  experience; Blazor Server keeps component state in memory across clicks, so their
  console-app intuition holds, and there is no JavaScript, no API layer, and no JSON
  boundary to learn.
- **No authentication in this sprint.** ASP.NET Identity is mostly scaffolded code that
  demonstrates none of the six rubric items while consuming a large share of the budget.
- **No EF Core or real database in this sprint.** Storage goes behind an interface so the
  swap is cheap later.
- **.NET 10 SDK** (10.0.302) confirmed installed.

**Coaching contract.** Teach the concept before asking Brett to build. Claude scaffolds
pure ceremony only — project creation, `.gitignore`, folder structure, config. Brett
writes every line that demonstrates skill: classes, interfaces, LINQ, components, event
handlers. Claude explains, reviews, and unblocks; Claude does not write the graded code.

**Skills every session should consult.** `/grilling` and `/domain-modeling` by default.
`/research` for the AFK research tickets, `/prototype` for the prototype ticket.

**This map is one leg of a longer journey.** The project is meant to be a continual
learning vehicle. Auth, EF Core, Azure, and the chatbots are not abandoned — they are the
subject of later maps, and several are listed under Out of scope below with that intent.

## Decisions so far

<!-- one line per closed ticket: the gist, then zoom the link for the detail -->

- [Learn how a Blazor component works](issues/01-learn-blazor-component.md) — yes, and it
  fits in one sitting: [research/blazor-primer.md](research/blazor-primer.md). A `.razor`
  file is an ordinary C# class deriving from `ComponentBase`, living in server memory on a
  SignalR circuit — the console-app intuition holds. Ticket also records three facts the
  storage seam depends on, chiefly that `AddScoped<T>` is scoped to the *circuit* (one
  instance per browser tab, dies on refresh), not to an HTTP request.

- [Model the domain: games, collections, and plays](issues/02-domain-model.md) — seven
  types across three aggregates: **GameCatalog** (the world), **GameCollection** (my
  shelf), **PlayLog** (my history). Both point into the catalog; neither points at the
  other — because **plays are independent of ownership** (convention and café plays are
  frequent, not edge cases). `Play` is permissive: only the game, the date, and the owner's
  presence are required; scores, winner, duration and location are optional, so `Winner` is
  an `IsWinner` flag rather than a computation over scores. Three invariants, each on the
  class that owns the state: no duplicate title (`GameCollection.Add`), no more players
  than the game seats — upper bound only (`Play`), and every play includes you
  (`PlayLog.Record`). Hands one gap downstream: a catalog with no ad-hoc creation can't log
  an unlisted convention title, now a constraint on
  [Choose the game data source](issues/05-choose-data-source.md).

- [Survey board game data sources](issues/03-survey-data-sources.md) — full findings in
  [research/board-game-data-sources.md](research/board-game-data-sources.md). **The BGG XML
  API is no longer anonymous**: since 2025 both v1 and v2 return `401` without a registered
  application's bearer token (verified live; a bogus token also fails), so every pre-2025
  tutorial is wrong and the API can't serve as a *fallback* — failure is total. Rate limits
  are unpublished (throttling shows as 500/503, not 429; 5s between requests is the safe
  gap), the 202-queue pattern applies only to `/collection`, and `/thing` takes 20 ids per
  call. Parsing is a **non-issue and a rubric asset**: the whole XML→domain mapping is ~20
  lines of LINQ-to-XML, no NuGet, verified running on .NET 10 — no client library, since a
  dependency would take Brett's code off the page. No free unauthenticated alternative
  exists in 2026 (Board Game Atlas and bgg-json both dead). **Key conclusion:** the API's
  value is at *build time*, not run time — seed once offline with the token, commit the
  result, demo with zero network dependency. Hands two things downstream: that
  recommendation plus a CSV-dump route to a catalog of thousands, both now constraints on
  [Choose the game data source](issues/05-choose-data-source.md); and a fact to nail down,
  [Confirm BGG API access is a working token](issues/09-verify-bgg-token.md).

- [Verify the toolchain end to end](issues/04-verify-toolchain.md) — the whole loop works on
  Brett's machine: scaffold, build (0 warnings), serves a page, hot reload, and a
  **breakpoint in an event handler hits** — so the console-app debugging intuition transfers
  and the Blazor Server bet holds in practice. Sprint-day command is one line:
  `dotnet new blazor -n MeepleLedger -int Server -au None`. Repo pushed private to
  [github.com/bgent19/MSSA_project](https://github.com/bgent19/MSSA_project), branch `main`,
  settling this ticket's open GitHub question. Two findings: (1) Brett's first scaffold used
  Individual Accounts, pulling in Identity + EF Core + a mandatory LocalDB connection string
  against two settled constraints — re-scaffolded with `-au None`, cutting `Program.cs` from
  ~60 lines to 27 with **zero package references**; (2) the big one — `-int Server` gives
  **per-page** interactivity, so any screen with a button or form needs
  `@rendermode InteractiveServer` or its clicks silently do nothing with no error at all.
  That second finding is now a constraint on
  [Prototype the screens and the demo click path](issues/07-prototype-screens.md). Also:
  "Apply Hot Reload on File Save" was unchecked in VS and had to be turned on.

- [Prototype the screens and the demo click path](issues/07-prototype-screens.md) — **four
  screens, a populated start, and the collection as the landing page.** Three variants built
  as throwaway wireframes
  ([prototypes/screens-prototype.html](prototypes/screens-prototype.html)): collection-first
  (5 screens), activity-first (4), and one single screen with no routing. Chose the
  collection-first structure minus its game-detail screen, with the activity-first variant's
  headline beat folded in: **Collection** (landing), **Log a play**, **Play Log**, all
  interactive, plus a **static Statistics** screen — each tagged so sprint-day Brett types
  `@rendermode InteractiveServer` without thinking. The app **starts full** (~18 games, ~40
  plays) with one deliberate gap filled live on stage, because an empty start burns the first
  ninety seconds typing and can't show LINQ doing anything — filtering four rows isn't
  filtering. Hour-one target is Collection + Log a play; cut order is Statistics → Play Log
  filtering → Play Log; styling is time-boxed and last. The six-step click path ends on the
  **"played but not owned"** row, making the domain model's most consequential decision
  visible in ten seconds. Accepted cost: no home for a per-game win rate. Hands seed-must-
  include-plays and a catalog-wider-than-the-collection to
  [Choose the game data source](issues/05-choose-data-source.md), the hour-one target and cut
  order to [Write the hour-by-hour sprint plan](issues/08-sprint-plan.md), and graduates the
  presentation out of the fog as
  [Design the demo and presentation narrative](issues/10-demo-narrative.md).

- [Design the storage seam](issues/06-storage-seam.md) — **two interfaces, five members,
  zero methods**: `IGameCatalogSource { Catalog }` and `IMeepleStore { Collection, PlayLog }`,
  both `AddSingleton`, both sync. The seam is a **persistence port, not a repository** — it
  hands over fully-built aggregates and never sees an `OwnedGame`, so the domain model stays
  the graded artifact. Two findings drive the rest: `AddScoped` in Blazor Server is
  **per circuit and dies on F5**, which would empty the collection mid-demo, so singleton is
  both safe *and* correct while there is one user; and because a singleton holds the
  aggregates in fields, `Collection.Add(x)` has *already* persisted — so `Save` is deleted
  rather than stubbed, since a no-op you must remember to call fails **invisibly today** and
  silently in map two. Today's implementation is **in-memory seeded in the constructor** (a
  mid-demo restart lands on a populated app, and JSON would need a whole DTO layer to get
  past the private `_games`/`_plays`). Map two touches **two files** —
  `EfMeepleStore.cs` plus one line of `Program.cs`, with zero component or domain changes —
  though honestly: EF can't be a singleton, so map two will likely revisit the shape. What
  the seam protects is the domain model and the components. Graduated fog into
  [Decide the project and folder structure](issues/12-solution-structure.md).

- [Confirm BGG API access is a working token](issues/09-verify-bgg-token.md) — **access is
  genuine**: an approved application with a bearer token, not merely a BGG user account, so
  the week-plus approval queue is not in our path. Verified `200` on a live `/thing` call.
  Username **TheGentleBean**, collection **~40 games**. This makes ticket 03's build-time
  seeding recommendation live rather than hypothetical, and the real 40-game shelf is itself
  a strong seed candidate — one `/collection` call, small enough to hand-curate, and
  genuinely lived-in rather than typed to fill a demo. Correspondingly weakens the
  CSV-dump-of-thousands route, which existed mainly to close the unlisted-game gap. One
  loose end: **the token is not stored anywhere yet** — user-secrets or an untracked file,
  never a commit; and if seeding is build-time-only it need never reach app config at all.

- [Choose the game data source and seeding strategy](issues/05-choose-data-source.md) —
  **build-time seeding, no runtime network at all.** The BGG API runs once, offline, on
  Brett's machine; its output is committed as C# source; the running app never opens a
  socket. Chosen because it *keeps* the rubric-positive LINQ-to-XML parsing and discards only
  the demo risk — ticket 03 having established that BGG failure is total (`401`), not
  graceful. Seven decisions: catalog of **~200 games** (Brett's ~40 owned plus BGG
  top-ranked); seed as a **C# `static readonly List<Game>`**, not JSON, because it cannot
  fail at runtime and ~200 games is only ~200 lines; **~60-80 seeded plays** as fixed data,
  not generated at startup; a **search box filtering the catalog** as the one picker,
  reused by both the add-to-collection and log-a-play flows; the **seeder committed as an
  unreferenced console project**, since a throwaway script would put the parsing code outside
  the graded deliverable; **expansions excluded**, as ticket 02's model has no expansion type
  and their blank player counts would break the seat invariant. **Closes ticket 02's
  unlisted-game gap by width, not an escape hatch** — a catalog wider than the shelf is what
  makes plays-independent-of-ownership demonstrable at all, and there is no manual entry
  anywhere. The CSV dump loses the catalog job (it has no player counts, so it cannot enforce
  Brett's own invariant) but keeps a better one: supplying top-ranked ids to feed `/thing`.
  Also resolves ticket 09's loose end — build-time-only seeding means the token never reaches
  app configuration. Hands down constraints on
  [Prototype the screens](issues/07-prototype-screens.md) (the app opens populated, not
  empty) and [the sprint plan](issues/08-sprint-plan.md), plus one new ticket,
  [Run the seeding pipeline](issues/11-run-seeding-pipeline.md).

- [Design the demo and presentation narrative](issues/10-demo-narrative.md) — **a 7-minute
  core with two marked optional beats to ~12**, built as one talk so cuts are made by dropping
  marked sections rather than improvising. The guidelines say *nothing* about the presentation
  — no slot, no format — and the real number is still unknown. Through-line: **"the domain
  model is the app"**, hooked by "I built something I'll actually use" and closed on "small on
  purpose". The six fundamentals are **tiered, not recited**: OOP design, data structures and
  classes get a full beat anchored to a click; branching, methods and loops get a clause. The
  **loops gap is real and answered out loud** — the app is LINQ end to end, so the only honest
  `foreach` lives in the unopened seeder; rather than write a worse loop to satisfy a
  checklist, Brett says *"LINQ rather than hand-written loops — same iteration, less code to
  get wrong"*. Code beat is **`GameCollection.Add` on a slide**, never live Visual Studio;
  the spine is carried by a spoken structural sentence over the running app instead, because
  no single file shows a relationship *between* classes. The API is reframed from omission to
  decision — *"no network dependency, not because I couldn't call it but because I did, once,
  offline"*. Failure plan survives everything: a restart lands on a full app, and the spine's
  proof is **seeded**, so the live write is a flourish, not a beam; dead clicks get a
  one-sentence diagnosis and **no pause**. Surfaced a conflict between two settled decisions —
  the spine's only visible proof sat on **Statistics**, which is **first to cut** — resolved by
  handing [the sprint plan](issues/08-sprint-plan.md) one build constraint: a **per-row "not
  owned" badge on the Play Log**, which moves the proof onto the screen where the live write
  lands and makes step 5 prove the point on save.

- [Decide the project and folder structure](issues/12-solution-structure.md) — **one project,
  three folders, and namespaces that follow them.** `Domain/`, `Storage/`, `Data/` under
  `MeepleLedger/`, plus a sibling `MeepleLedger.Seeder/` console project. The class library was
  declined — it buys structurally enforced dependency direction, but **folder-mapped namespaces
  make the later split a pure file move**: create `MeepleLedger.Domain`, drag 8 files in, and
  every `using` and namespace declaration stays byte-identical, because no namespace ever
  mentioned the project. So the namespace choice is load-bearing, not cosmetic — it is what makes
  one project safe. `Data/` stays separate from `Storage/` on the line *generated files you never
  open* vs *code you wrote*, so `Storage/` reads as four hand-written files rather than four
  buried under 200 lines of game titles. `_Imports.razor` gains exactly two `@using` lines —
  `MeepleLedger.Data` is **excluded on purpose**, since the confusing Razor error it would prevent
  can only fire when a component reaches for seed data, which is the thing that should not
  compile. Corrected two stale premises in its own body: the seed is C# source, not JSON, so
  `wwwroot`/embedded-resource were dead options; and `JsonGameCatalogSource` is really
  `SeededGameCatalogSource`. The seeder needs **no project reference in either direction** — it
  emits source *text* — but it is listed in the `.slnx`, since code an examiner cannot open is not
  evidence; accepted risk is Visual Studio launching the wrong startup project. Structure gets
  **no presentation beat** — a folder tour shows organization, not skill — but a one-sentence Q&A
  answer that lands the dependency-direction point we declined to pay for structurally. Hands the
  seeder's location to [Run the seeding pipeline](issues/11-run-seeding-pipeline.md) and a
  minutes-not-hours hour-one step to [the sprint plan](issues/08-sprint-plan.md).

- [Run the seeding pipeline and commit the seed data](issues/11-run-seeding-pipeline.md) —
  **pipeline verified, execution deliberately deferred into the sprint.** Resolved re-scoped
  rather than as written: the risk half is done, the seed half is Brett's choice to move into
  hour 1, made against a recommendation to emit beforehand. Runbook:
  [research/seeding-runbook.md](research/seeding-runbook.md). All three unproven steps
  retired, **two contrary to what the map assumed**: the **CSV dump is not reachable with the
  bearer token** (returns an Angular HTML shell — the token authorizes the XML API, not site
  pages, so step 1 is a manual browser download), and the **202 retry queue is a non-event**
  (`202` then `200`, ~2s — not the awkward endpoint ticket 03 flagged). `/thing` batched at 20
  with `stats=1` is confirmed working, all five domain fields present on all 28 owned games,
  zero missing, no zero player counts. Two corrected facts: the **owned shelf is 28, not ~40**
  (no owned expansions — the filter changes nothing; ticket 09 over-estimated), so the catalog
  needs ~172 ranked ids; and **20% of games have multiple designers**, promoting `Game.Designer`
  from footnote to a real choice (take the first; guard with `FirstOrDefault() ?? "Unknown"`).
  Surfaced the map's sharpest hazard — the emitted seed lands **inside** the web project, so
  generating it **breaks the whole build** until the domain model exists. Accepted cost, stated
  rather than buried: **45–60 minutes of hour 1** now goes to seeding, with all three emit
  steps unproven. Scaffolded `MeepleLedger.Seeder/` (in the solution, 0 warnings, zero
  packages); `BGG_TOKEN` stored as a User-scope env var, never in a commit. Hands the budget
  line, the compile-order ordering constraint, and a night-before CSV download to
  [Write the hour-by-hour sprint plan](issues/08-sprint-plan.md) — **now unblocked, and the
  last ticket on the map.**

## Not yet specified

- **Testing.** Whether any automated tests belong in a 5-hour sprint, and if so what kind.
  Probably a later map, but worth a deliberate decision rather than a silent omission.
- **What map two opens with.** Likely auth and EF Core, in some order, but the order
  depends on what the sprint actually produces.

## Out of scope

Ruled beyond *this* destination. These return as later maps, not as tickets here.

- **Authentication and user accounts** — high cost, near-zero rubric value, guidelines
  don't ask for it. First candidate for map two.
- **EF Core and a real database** — deferred behind the storage seam. Map two.
- **Azure hosting and services** — the app must not *preclude* Azure, but nothing gets
  deployed in this sprint.
- **The AI chatbots** (curator, rules helper) and the **price tracker** — the reason the
  project keeps going, and entirely outside a 5-hour fundamentals showcase.
- **Blazor WebAssembly, MVC, Razor Pages** — considered and rejected as the framework for
  this sprint; revisiting them is a new effort, not a step on this route.
