# Spec: MeepleLedger

Triage label: `ready-for-brett` — **see [A note on the label](#a-note-on-the-label-and-the-tracker).**

Broken into 23 build tickets at
[`meepleledger-build/issues/`](../meepleledger-build/issues/README.md).

Synthesized from [the completed map](map.md) and its twelve closed tickets. This document
re-decides nothing; it consolidates. Where a decision has an argument behind it, the ticket is
linked and the argument stays there. The one-page executive version is [PRD.md](PRD.md); the
order of operations is [research/sprint-plan.md](research/sprint-plan.md).

---

## Problem Statement

Brett plays board games at home, at cafés, and at conventions, and currently records none of it.
Two things get lost: which games are on the shelf (so duplicates get bought and gaps go
unnoticed), and which games actually got played (so "what do I like, really?" has no answer
beyond memory).

The existing tool for this, BoardGameGeek, gets the shape wrong for how Brett plays. It demands
an account, and it treats **ownership as the primary fact** with plays bolted onto it. But a
large share of Brett's plays are of games he does not own — convention tables, café copies,
friends' shelves. Those are frequent cases, not edge cases, and a tracker that models plays as a
property of owned games cannot record them without lying.

There is a second problem sitting behind the first. This app is a **graded MSSA Mini Project**
whose rubric marks fundamentals — branching, loops, methods, classes, OOP design, data structure
use — not architecture. So the tracker has to be built in a way that puts Brett's own code on the
page, in roughly five hours, with a live demo at the end that cannot fail in front of an examiner.
A solution that solves the board-game problem beautifully by leaning on frameworks, scaffolding,
or a cloud service solves none of the grading problem.

## Solution

**MeepleLedger** — a .NET 10 Blazor Server web app, single user, no accounts, no network at
runtime — built around one load-bearing modelling decision: **the catalog is the world, the
collection is my shelf, the log is my history**, and the shelf and the history never speak to each
other. Both point *into* the catalog; neither points at the other. That is what makes "log a play
of a game you don't own" a natural operation rather than a workaround.

Four screens, all reading one in-memory store that **starts populated**:

| Screen | Interactive | Job |
|---|---|---|
| **Collection** (landing) | yes | Browse the shelf; search by title/designer; filter by player count |
| **Log a play** | yes | Record a session: pick the game from the **catalog**, not the shelf |
| **Play Log** | yes | History, most recent first, with a per-row **"not owned" badge** |
| **Statistics** | static | Most played, totals, played-but-not-owned |

The app opens full — 28 real owned games, a catalog of ~75–200, and ~60–80 seeded plays — with
one deliberate gap filled live on stage. An empty start burns the first ninety seconds typing and
cannot show LINQ doing anything; filtering four rows is not filtering.

The demo is a **six-step click path**: populated collection → search → player-count filter → log a
play of an **unowned** game → it appears on top of the Play Log with a "not owned" badge →
Statistics moves. Step 5 is the whole point of the app made visible in ten seconds.

Data comes from the BoardGameGeek XML API, called **once, offline, at build time**, by a committed
console project. Its output is committed as C# source. The running app never opens a socket.

---

## Testing Decisions

*(Placed before the user stories because the seam question is the one thing in this spec that
wants confirmation before work starts.)*

### The seam

**One seam, and it already exists: the three domain aggregates.**

`GameCatalog`, `GameCollection`, and `PlayLog` are plain C# classes with no Blazor dependency, no
DI dependency, and no I/O. A test constructs them directly and calls their methods. Nothing needs
to be introduced, extracted, or refactored to make this testable — the seam is a consequence of
[the storage seam decision](issues/06-storage-seam.md), which deliberately kept the store a
*persistence port* that hands over fully-built aggregates rather than a repository that reaches
inside them. The aggregates own their state and their rules, so the aggregates are where behavior
can be observed.

This is the **highest** available seam for the behavior that matters, because the behavior that
matters — the three invariants — lives nowhere else. Testing through a component would test Blazor;
testing through `IMeepleStore` would test property getters.

**The second seam exists but is not used for testing.** `IGameCatalogSource` and `IMeepleStore`
would let a fake store be substituted under a component test. Component testing (bUnit) is
explicitly not on the add-back list and is not proposed here. Noted only so that a future map does
not conclude the seam was overlooked.

**No new seams are proposed.**

### What makes a good test here

Only external behavior. Each of the three tests names a rule a user could state in a sentence, and
asserts it through the public method that enforces it:

1. **You can't own the same title twice** — `GameCollection.Add` rejects a second `OwnedGame`
   with a title already on the shelf.
2. **A play can't seat more than the game allows** — the `Play` constructor rejects more
   `PlayerResult`s than `Game.MaxPlayers`. Upper bound only: a solo play is legal, so a test
   asserting a *lower* bound would encode a rule the model deliberately does not have.
3. **Every play in your log includes you** — `PlayLog.Record` rejects a `Play` whose `Results` do
   not contain the `OwnerName`.

Not tested: the private `_games` `Dictionary` or the private `_plays` `List` (implementation
detail — the point of the encapsulation is that nothing can see them), the search and filter
methods (LINQ over an in-memory list; the failure mode is a compile error, not a wrong answer),
the seeder (throwaway build-time tooling), and anything Blazor.

### Prior art

**There is none — this repository has no test project and no test framework.** The tests are
add-back item #4 in [the sprint plan](research/sprint-plan.md), estimated at 30 minutes, and are
explicitly **not baseline sprint work**. They are added only if the sprint finishes ahead, and
their value is stated plainly: they *prove* the "the domain model is the app" through-line rather
than asserting it. The absence of prior art is part of why they sit at position 4 rather than
position 1 — the 30 minutes includes standing the project up from nothing.

Scope when built: three tests, one per invariant. Nothing else.

---

## User Stories

### Browsing the shelf

1. As a board game owner, I want the app to open directly on my collection, so that the thing I
   look at most often costs me zero clicks.
2. As a board game owner, I want to see every game I own in one list, so that I know what is on
   my shelf without walking to the shelf.
3. As a board game owner, I want each row to show the title, designer, player count and playtime,
   so that I can judge a game's fit without opening anything.
4. As a board game owner, I want to search my collection by title, so that I can confirm whether I
   already own something while standing in a game shop.
5. As a board game owner, I want to search my collection by designer, so that I can find the other
   games by someone whose work I liked.
6. As a board game owner, I want to filter my collection by player count, so that I can answer
   "what can five of us play tonight?" without doing arithmetic on every box.
7. As a board game owner, I want the collection to be populated the first time I open the app, so
   that I am not staring at an empty screen wondering whether it is broken.
8. As a board game owner, I want my shelf to reflect the games I genuinely own, so that the app is
   a record rather than a demo fixture.
9. As a board game owner, I want to be told when I try to add a game I already own, so that my
   shelf never contains the same title twice.
10. As a board game owner, I want each owned copy to carry its condition, so that I can tell a mint
    copy from a worn one when someone asks to borrow it.
11. As a board game owner, I want condition to be a fixed set of choices rather than free text, so
    that I cannot record a condition that means nothing.
12. As a board game owner, I want to record when I acquired a copy, so that my shelf has a history
    as well as a contents list.

### Logging a play

13. As a board game player, I want to record that I played a game today, so that my history builds
    up without effort.
14. As a board game player, I want to pick the game I played from the **catalog** rather than from
    my shelf, so that I can log the convention game I do not own.
15. As a board game player, I want to search the catalog by title when picking a game, so that
    choosing from hundreds of games is typing rather than scrolling.
16. As a board game player, I want the same picker in both the add-to-collection and log-a-play
    flows, so that I learn one interaction and not two.
17. As a board game player, I want only the game and the date to be required, so that logging a
    play is fast enough that I actually do it.
18. As a board game player, I want to optionally record who else was at the table, so that a play
    can be a shared memory and not just a tally.
19. As a board game player, I want to optionally record scores, so that the games where we counted
    are captured and the games where we did not are still loggable.
20. As a board game player, I want to mark a winner without entering any scores, so that co-op
    wins and uncounted games are recordable.
21. As a board game player, I want to mark more than one winner, so that team games and shared
    victories are not forced into a single name.
22. As a board game player, I want to optionally record how long the session ran, so that I can
    learn which games actually take as long as the box claims.
23. As a board game player, I want to optionally record where I played, so that convention and café
    plays are distinguishable from plays at my own table.
24. As a board game player, I want to be stopped from seating more players than the game allows, so
    that an obvious typo does not become a permanent bad record.
25. As a board game player, I want to log a solo play, so that games I played alone are part of my
    history — the seat limit is an upper bound, not a requirement to fill the table.
26. As a board game player, I want every play in my log to include me, so that the log stays *my*
    history and not a general database of games other people played.

### Reading the history

27. As a board game player, I want to see my plays most-recent-first, so that the thing I just
    recorded is the thing I see.
28. As a board game player, I want a play I just logged to appear immediately, so that I trust the
    app recorded it.
29. As a board game player, I want each row to show clearly whether I own that game, so that the
    distinction between what I own and what I have played is visible rather than inferred.
30. As a board game player, I want plays of games I do not own to sit in the same list as plays of
    games I do, so that my history is one history.
31. As a board game player, I want a play to survive removing that game from my collection, so that
    selling a game does not erase the evenings I spent with it.
32. As a board game player, I want to see how many times I have played a particular game, so that I
    can tell a favourite from an impulse buy.

### Understanding the collection

33. As a board game owner, I want a count of the games I own, so that I have one number to quote.
34. As a board game owner, I want a count of the plays I have logged, so that I can see the habit
    accumulating.
35. As a board game owner, I want to see my most-played games, so that I learn what I actually
    reach for rather than what I think I like.
36. As a board game owner, I want to see the games I have played but do not own, so that I have a
    ready-made shortlist of things to buy.

### Trusting the app

37. As a board game owner, I want the app to keep my data as I navigate between screens, so that
    moving from Log a play to Play Log does not lose what I just entered.
38. As a board game owner, I want my data to survive a browser refresh, so that an accidental F5
    does not empty my shelf.
39. As a board game owner, I want the app to work with no internet connection, so that I can log a
    play at a convention on bad venue wifi.
40. As a board game owner, I want a restart of the app to land me on a populated collection, so
    that a crash is an inconvenience rather than a loss.
41. As a board game owner, I want the app not to ask me to sign in, so that recording a play takes
    seconds.

### Presenting the work (Brett as presenter)

42. As a student presenting a graded project, I want the app to open populated, so that my first
    ninety seconds are spent demonstrating rather than typing.
43. As a student presenting a graded project, I want a fixed six-step click path, so that I am
    executing a rehearsed sequence rather than improvising in front of an examiner.
44. As a student presenting a graded project, I want the "not owned" badge on the screen where the
    live write lands, so that my central design decision proves itself on save rather than on a
    screen I might have cut.
45. As a student presenting a graded project, I want enough seed volume that filtering visibly
    does work, so that a LINQ query looks like a feature rather than a decoration.
46. As a student presenting a graded project, I want the demo's proof to be present in the seed
    data, so that the live write is a flourish and not a load-bearing beam.
47. As a student presenting a graded project, I want a pre-decided one-sentence response to a dead
    click, so that a missing render mode costs me a sentence and not a pause.
48. As a student presenting a graded project, I want to show my code on a slide rather than in an
    IDE, so that no live editor state can undermine the beat.
49. As a student presenting a graded project, I want a talk with marked optional sections, so that
    I can fit an unknown time slot by dropping marked beats rather than improvising cuts.
50. As a student presenting a graded project, I want to be able to say the API was a decision
    rather than an omission, so that "no network at runtime" reads as engineering judgment.

### Being graded (Brett as author)

51. As a student being graded on fundamentals, I want the domain model to be the centre of the app,
    so that the graded artifact is the thing I spent my time on.
52. As a student being graded on OOP design, I want behavior to live on the domain classes rather
    than in components, so that my classes demonstrate design rather than holding data.
53. As a student being graded on encapsulation, I want the collection and log to hide their
    internal storage behind read-only views, so that the invariants cannot be bypassed from
    outside.
54. As a student being graded on data structure use, I want the collection keyed by title in a
    `Dictionary`, so that rejecting a duplicate is O(1) and the choice of structure has a stated
    reason.
55. As a student being graded on data structure use, I want the play log to be an ordered `List`,
    so that "most recent first" is a property of the structure I chose.
56. As a student being graded on branching, I want guard clauses and an enum-driven `switch`, so
    that branching appears where it is genuinely warranted.
57. As a student being graded on loops, I want to use LINQ throughout and be able to say why, so
    that "same iteration, less code to get wrong" is a defensible position rather than a gap.
58. As a student being graded on interfaces and DI, I want storage behind two small interfaces
    registered in the container, so that there is one clean instance of each concept to point at.
59. As a student writing every graded line myself, I want the assistant to explain, review, and
    unblock rather than to write the code, so that the deliverable is genuinely mine.

### Building it (Brett as developer)

60. As a developer, I want real data rather than invented rows, so that the demo shows a lived-in
    app instead of a fixture.
61. As a developer, I want the seed emitted once at build time and committed as C# source, so that
    it cannot fail at runtime.
62. As a developer, I want the seeder committed as a project in the solution, so that the
    LINQ-to-XML parsing counts as evidence an examiner can open.
63. As a developer, I want the seeder to need no project reference in either direction, so that the
    web app does not depend on build tooling.
64. As a developer, I want the seeder to drop invalid rows before writing, so that a bad row never
    reaches the app — where it would throw inside singleton construction and read on stage as
    "Blazor is broken".
65. As a developer, I want my API token in an environment variable and never in a commit, so that
    a private repo does not become a leaked credential.
66. As a developer, I want the token never to reach app configuration at all, so that the running
    app has no secret to leak.
67. As a developer, I want storage behind an interface, so that swapping in EF Core later touches
    two files rather than the whole app.
68. As a developer, I want no `Save` method anywhere, so that there is nothing to forget to call
    and no silent data loss.
69. As a developer, I want namespaces to follow folders rather than the project, so that splitting
    out a class library later is a file move with no edits.
70. As a developer, I want generated data in its own folder separate from hand-written code, so
    that my four storage files are not buried under 200 lines of game titles.
71. As a developer, I want a committed state immediately before each seed emit, so that "revert and
    re-emit" exists as a recovery option.
72. As a developer, I want the domain model to compile green before the seeder runs, so that
    emitting seed data cannot break the build.
73. As a developer, I want each work session to end at its checkpoint or its cap, so that falling
    behind costs an optional feature rather than the next session.
74. As a developer, I want the cut list decided in advance, so that a cut is a non-event rather
    than a decision made tired.

---

## Implementation Decisions

### Stack and shape

- **.NET 10 Blazor Web App, `InteractiveServer` render mode**, scaffolded with
  `dotnet new blazor -n MeepleLedger -int Server -au None`. Chosen because Brett has solid OOP and
  HTML/CSS but no .NET web experience: component state lives in server memory across clicks, so
  console-app intuition holds, and there is **no JavaScript, no API layer, and no JSON boundary**
  to learn. ([04](issues/04-verify-toolchain.md), [01](issues/01-learn-blazor-component.md))
- **`-int Server` gives per-page interactivity, not global.** Every screen with a button or a form
  must declare `@rendermode InteractiveServer` or its clicks silently do nothing — **no error, no
  exception, no console output.** This is the first thing to check on any dead click.
  ([04](issues/04-verify-toolchain.md))
- **No authentication.** ASP.NET Identity is largely scaffolded code that demonstrates none of the
  six rubric items. `-au None` also keeps `Program.cs` at ~27 lines with **zero package
  references**.
- **No EF Core and no database.** Deferred behind the storage seam.
- **One project, three folders, namespaces that follow the folders.** `Domain/`, `Storage/`,
  `Data/` under `MeepleLedger/`, plus a sibling `MeepleLedger.Seeder/` console project. A class
  library was declined: folder-mapped namespaces make the later split a pure file move, because no
  namespace ever mentions the project. `Data/` is separate from `Storage/` on the line *generated
  files you never open* vs *code you wrote*. ([12](issues/12-solution-structure.md))
- **`_Imports.razor` gains exactly two `@using` lines.** `MeepleLedger.Data` is **excluded on
  purpose** — the confusing Razor error it would prevent can only fire when a component reaches for
  seed data, which is precisely the thing that should not compile.

### The domain model — seven types, three aggregates

The graded artifact. *The catalog is the world, the collection is my shelf, the log is my history.*
([02](issues/02-domain-model.md))

```csharp
class Game                    // a title, in the abstract
    Name, Designer, MinPlayers, MaxPlayers, PlaytimeMinutes

class OwnedGame               // my copy of a title
    Game Game
    DateAcquired, Condition, Notes

class GameCatalog
    IReadOnlyList<Game> Games
    Search(term) · ByPlayerCount(n)

class GameCollection
    private Dictionary<string, OwnedGame> _games      // keyed by title
    Add(OwnedGame)            → throws if already owned
    Remove(name) · Search(term) · FilterByPlayerCount(n) · TotalGames

class Play                    // one session at one table
    Game Game                 // required
    DateTime PlayedOn         // required
    int? DurationMinutes      // optional
    string? Location          // optional
    List<PlayerResult> Results
    Winners  => Results.Where(r => r.IsWinner)
    HasWinner
                              → throws if Results.Count > Game.MaxPlayers

class PlayerResult
    string PlayerName
    int?  Score               // optional
    bool  IsWinner            // optional

class PlayLog
    string OwnerName          // whose log this is
    private List<Play> _plays
    Record(Play)              → throws unless OwnerName is among the Results
    ForGame(game) · RecentFirst() · MostPlayed()
```

*(Shape fixed by ticket 02's grilling session, reproduced here because it encodes the decisions
more precisely than prose.)*

- **Three invariants, each on the class that owns the state.** No duplicate title
  (`GameCollection.Add`); no more players than the game seats — **upper bound only**, because a
  solo play is always legal (`Play` constructor); every play includes you (`PlayLog.Record`). The
  rule about the log belongs to the log, so `Play` stays ignorant of which log it is filed in.
- **`Game` / `OwnedGame` split.** A title and a copy diverge fast — a title has a designer, a copy
  has a condition. The split is what makes a browsable catalog possible at all.
- **`Play` is permissive.** Required: game, date, owner's presence. Optional: everything else. Real
  play logging is lossy, and a model that demands full data does not get used.
- **`IsWinner` is a flag, not a computation over scores**, because scores are optional and co-op
  wins have no numbers. Multiple winners fall out for free. Accepted cost: the flag can disagree
  with the scores and nothing reconciles them.
- **`Condition` is an enum** — `{ Mint, Good, Played, Worn }` — so bad values are unrepresentable,
  it renders as a dropdown, and it yields a `switch` for free.
- **Named `GameCollection`, not `Collection`**, which collides with `System.Collections` and with
  the tutorial sense of "a list". Reads as a set with its siblings.
- **Behaviour lives on the domain classes.** Components call `Search`, `FilterByPlayerCount`,
  `MostPlayed`; they do not reimplement them inline.
- **Plays are independent of ownership.** `Play` references `Game`, never `OwnedGame`.
  `PlayLog.Record` checks the owner is present; it does **not** consult the collection.
- **Not modelled:** expansions, variants, house rules, personal ratings. `Play.Location` was kept
  in, because conventions carry real meaning for Brett.

### The storage seam — two interfaces, five members, zero methods

```csharp
public interface IGameCatalogSource  { GameCatalog Catalog { get; } }        // the world
public interface IMeepleStore        { GameCollection Collection { get; }    // mine
                                       PlayLog        PlayLog    { get; } }
```

Both registered `AddSingleton`. Both sync. ([06](issues/06-storage-seam.md))

- **A persistence port, not a repository.** The store hands over fully-built aggregates and never
  sees an `OwnedGame` or a `Play`. The tutorial shape (`IOwnedGameRepository.Add/Remove/GetAll`)
  was rejected because it would relocate the duplicate-title guard and the private `Dictionary` —
  the model's best encapsulation evidence — out of the domain and into infrastructure. Accepted
  cost, stated up front: this is not how EF Core wants to be used, so map two may redraw the seam
  rather than slot into it.
- **The catalog is split off from the mutable data** because it differs on four axes at once:
  read-only vs mutable, seeded vs user-created, file-forever vs replaced-by-EF, shared vs personal.
- **`AddSingleton`, and this is the trap.** `AddScoped` in Blazor Server is **per circuit**, and a
  circuit dies on refresh — a scoped store would empty the collection in front of the examiner,
  silently and with no error. Singleton is also *correct* while there is exactly one user.
- **In-memory, seeded in the constructor — not JSON to disk.** JSON's only added benefit is
  surviving app restart, and it costs a whole DTO layer, because `System.Text.Json` cannot touch
  the private fields or rebuild through the guard clauses. Constructor seeding gets restart
  survival anyway.
- **No `Save` method — it was deleted, not stubbed.** With a singleton holding the aggregates in
  fields, `Collection.Add(x)` has *already* persisted. A no-op you must remember to call fails
  **invisibly today** and silently in map two.
- **`InMemoryMeepleStore` takes `IGameCatalogSource` as a constructor dependency**, because the
  seeded `OwnedGame`s and `Play`s must reference **the same `Game` instances** the catalog holds —
  otherwise the catalog and the shelf disagree about what "Catan" is.
- **Map two's swap touches two files**: a new `EfMeepleStore.cs` and one registration line. Stated
  without varnish: EF cannot be a singleton, so map two will likely reintroduce `Save` and revisit
  this shape. **What the seam protects is the domain model and the components** — not a promise
  that map two is free.

### Data and seeding

- **Build-time seeding; no runtime network at all.** The BGG XML API runs once, offline, on Brett's
  machine; its output is committed as C# source. This keeps the rubric-positive LINQ-to-XML parsing
  (~20 lines, no NuGet) and discards only the demo risk. ([05](issues/05-choose-data-source.md))
- **The BGG XML API is no longer anonymous.** Since 2025 both v1 and v2 return `401` without a
  registered application's bearer token, so every pre-2025 tutorial is wrong and the API cannot
  serve as a runtime *fallback* — failure is total, not graceful.
  ([03](issues/03-survey-data-sources.md))
- **No client library.** A NuGet dependency would take Brett's code off the page.
- **Seed shape: a C# `static readonly List<Game>`**, not JSON, because it cannot fail at runtime.
- **Catalog target ~75–200 games; owned shelf 28 (verified actual); ~60–80 plays as fixed data.**
  Nothing downstream depends on the literal figure 200 — only on *catalog wider than shelf*. If the
  BGG CSV dump is not in hand, the **pre-sanctioned fallback is `/xmlapi2/hot?type=boardgame`**
  (50 items, verified working), accepted at ~75 games, needing no decision on the day.
  ([11](issues/11-run-seeding-pipeline.md))
- **The CSV dump is a manual browser download.** The bearer token authorizes the XML API, not site
  pages — in code it returns an Angular HTML shell. Do not attempt it programmatically.
- **`/thing` batched at 20 ids with `stats=1`**, ~5s between calls. Throttling presents as
  `500`/`503`, not `429`. The `202` retry queue applies only to `/collection` and resolves in ~2s.
- **Expansions excluded**, because the model has no expansion type and their blank player counts
  would break the seat invariant.
- **Multiple designers: take the first.** 20% of games have several;
  `.FirstOrDefault()?.Attribute("value")?.Value ?? "Unknown"`.
- **The seeder filters at emit time** — drop any catalog game with `maxplayers < 1`; assert every
  emitted play includes `TheGentleBean`. This is required by the seam's shape: because the store is
  a singleton seeded in its constructor, a violating row does not produce a bad row, it throws
  *inside singleton construction* and **the app fails to start**, wrapped in a DI exception. An
  internal validation-bypassing load path was **rejected** — an invariant you can bypass is not an
  invariant, and it would undercut the talk's entire spine.
- **The seeder is committed as a project in the `.slnx`**, with **no project reference in either
  direction** (it emits source *text*). Committed rather than thrown away because code an examiner
  cannot open is not evidence. Accepted risk: Visual Studio may launch the wrong startup project.
- **`BGG_TOKEN` is a User-scope environment variable**, never committed, and never reaches app
  configuration at all. A terminal opened *before* it was set will not see it — that presents
  identically to a revoked token.
- **The emitted seed lands inside the web project**, so generating it **breaks the build until the
  domain model exists**. Ordering is therefore load-bearing: domain model → green build → commit →
  emit. The pre-emit commit is what makes "revert and re-emit" possible; it is not hygiene.

### Screens and interactions

- **Four screens, collection-first, populated start.** Chosen from three prototyped variants
  ([prototypes/screens-prototype.html](prototypes/screens-prototype.html)); the collection-first
  structure minus its game-detail screen, with the activity-first variant's headline beat folded
  in. ([07](issues/07-prototype-screens.md))
- **One shared search-box picker**, reused by both the add-to-collection and log-a-play flows. It
  filters the **catalog**, which is what closes the unlisted-game gap **by width rather than by an
  escape hatch**. There is no manual game entry anywhere.
- **The per-row "not owned" badge on the Play Log is a build constraint, not polish** — a
  `Dictionary` lookup against the collection plus a conditional in the markup. Build it *with* the
  Play Log, not after. The spine's only visible proof otherwise sat on Statistics, which is first
  to cut. ([10](issues/10-demo-narrative.md))
- **Statistics is static** — most played, totals, played-but-not-owned. No interactivity.
- **Accepted cost:** no home for a per-game win rate, because the game-detail screen was dropped.
  It returns as add-back item #3.
- **Build order:** Collection and Log a play first. Cut order: Statistics → Play Log filtering →
  Play Log. Styling is time-boxed and last.

### Delivery

- **Five gated sessions; ~5h of estimates under ~6h50 of caps.** The honest figure is ~6.5 hours,
  of which the first five produce a completely demoable app.
  ([research/sprint-plan.md](research/sprint-plan.md))
- **The stopping rule is the load-bearing part**, not the grouping: a session ends at its
  checkpoint or its cap, whichever comes first, and the shortfall is paid out of the *if-ahead*
  tier, never out of the next session. This is what makes the cut list function as slack.
- **Statistics and Play Log filtering are not in the baseline** — they are if-ahead items, so
  falling behind means not adding them rather than losing something planned.
- **Sessions are split, not contiguous**, so cut decisions get made rested. **Session 1 is the
  exception and must not be interrupted** — it crosses the compile-order boundary.
- **Branch `main`, no feature branch.** Solo project, no reviewer; an examiner reading history sees
  a linear story. Never commit a red build; never end a session on a red build.
- **After Session 3 the presentation is deliverable**, because Session 3's checkpoint *is* step 5
  of the click path.

### Presentation

- **A 7-minute core with two marked optional beats extending to ~12**, built as one talk so cuts
  are made by dropping marked sections. The guidelines say nothing about the presentation — no
  slot, no format — and the real number is still unknown. ([10](issues/10-demo-narrative.md))
- **Through-line: "the domain model is the app."** Hooked by "I built something I'll actually use",
  closed on "small on purpose".
- **The six fundamentals are tiered, not recited.** OOP design, data structures and classes get a
  full beat anchored to a click; branching, methods and loops get a clause.
- **The loops gap is answered out loud**: the app is LINQ end to end, so the only honest `foreach`
  lives in the unopened seeder. Rather than write a worse loop to satisfy a checklist —
  *"LINQ rather than hand-written loops: same iteration, less code to get wrong."*
- **The code beat is `GameCollection.Add` on a slide, never live Visual Studio.** The relationships
  *between* classes are carried by a spoken structural sentence over the running app, because no
  single file shows them.
- **The API is reframed from omission to decision** — *"no network dependency, not because I
  couldn't call it but because I did, once, offline."*
- **Failure plan:** a restart lands on a full app; the spine's proof is seeded, so the live write is
  a flourish, not a beam; a dead click gets a one-sentence diagnosis and **no pause**.

---

## Out of Scope

**Out of this sprint, returning as later maps:**

- **Authentication and user accounts.** High cost, near-zero rubric value. First candidate for map
  two. When it arrives, `PlayLog.OwnerName` becomes the account name and the singleton registration
  is what changes — likely to `Collection(string owner)`, an interface change rather than only a
  lifetime one.
- **EF Core and a real database.** Deferred behind the storage seam. Map two.
- **Azure hosting and services.** The app must not *preclude* Azure; nothing gets deployed.
- **The AI chatbots** (curator, rules explainer) and the **price tracker** — the reason the project
  keeps going, and entirely outside a 5-hour fundamentals showcase.
- **Live BGG API calls at runtime.** The API is used once at build time. Keeping collections in
  sync with BGG is a later map.
- **Manual game entry.** The unlisted-game gap is closed by catalog *width*, deliberately, not by
  an escape hatch.

**Considered and rejected as the framework** — revisiting is a new effort, not a step here:
Blazor WebAssembly, MVC, Razor Pages.

**Not in the baseline sprint, ordered for add-back if ahead:**

1. Statistics (15 min) · 2. Play Log filtering (10 min) · 3. Game detail + per-game win rate
(40 min) · 4. Three invariant unit tests (30 min) · 5. Edit and delete a play (30 min).
Baseline plus all five lands around 8–9 hours, inside the guidelines' 8–12.

**Explicitly not modelled in the domain:** expansions, variants, house rules, per-play notes,
personal ratings.

**Not tested, even at add-back position 4:** components (no bUnit), the seeder, search and filter
methods, and anything private.

**Unknown rather than out of scope:** the presentation slot length, and what map two opens with —
likely auth and EF Core, in an order that depends on what the sprint actually produces.

---

## Further Notes

### A note on the label and the tracker

Two things about this spec's delivery differ from the `/to-spec` default, and both are flagged
rather than silently resolved:

**1. There is no configured issue tracker.** GitHub Issues on `bgent19/MSSA_project` is empty and
carries only the nine default labels — `ready-for-agent` does not exist. The tracker this project
actually uses is the wayfinder convention in `.scratch/mini-project/issues/`, which is where this
spec is filed. If a GitHub issue is wanted instead, the label needs creating first.

**2. The label is `ready-for-brett`, not `ready-for-agent`** — Brett's call, and it resolves a real
conflict with the standing coaching contract. The map states the contract plainly:
*Claude scaffolds pure ceremony only — project creation, `.gitignore`, folder structure, config.
Brett writes every line that demonstrates skill: classes, interfaces, LINQ, components, event
handlers.* This is a **graded academic deliverable**, and the domain model is both the graded
artifact and the substance of this spec. Handing it to an implementing agent would defeat the
point of building it.

So read this document as a **build brief for Brett**, with the assistant explaining, reviewing and
unblocking. The parts genuinely eligible for agent execution are the ceremony ones already named in
the contract — scaffold, folders, `.gitignore`, `.slnx` — plus, arguably, `MeepleLedger.Seeder/`,
which is build-time tooling outside the graded app, though ticket 05 deliberately committed it as
evidence, which cuts the other way.

### Things easy to lose track of

- The **CSV file must stay out of the repo**, gitignored or stored outside it. It is an input, not
  a deliverable.
- **Session 0 lands at least two days before Session 1.** The CSV download is the most fragile step
  on the map — not automatable, not quickly retryable — and two days early makes a second attempt
  free.
- **Session 4 must be adjacent to the presentation**, because rehearsal has to happen on the same
  machine and display.
- **"Apply Hot Reload on File Save"** was unchecked in Visual Studio and had to be turned on.
- **Run the revert command once during rehearsal**, so that on stage it is a command already typed
  rather than a `git log` excavation.

### Stale premises corrected during charting, recorded so they are not re-inherited

- The owned shelf is **28 games, not ~40** — ticket 09 over-estimated; there are no owned
  expansions, so the exclusion filter changes nothing.
- The seed is **C# source, not JSON**, which killed the `wwwroot` and embedded-resource options and
  renamed `JsonGameCatalogSource` to `SeededGameCatalogSource`.
- **Real data does not exist before the clock starts.** Ticket 08's body assumed it did; ticket 11
  moved the entire seeding run inside the sprint, which is why Session 1 is domain-first rather
  than screen-first.
- The **CSV dump is not reachable with the bearer token**, and the **202 retry queue is a
  non-event** — both contrary to what the map assumed.

### The one thing this spec cannot settle

The **presentation slot length is still unknown.** The talk survives not knowing, but finding out
is free and changes which optional beats get planned in.
