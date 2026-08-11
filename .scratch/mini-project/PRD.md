# PRD — MeepleLedger

**One-line:** A Blazor Server web app that tracks the board games you own and logs the games
you play — the two kept deliberately independent, because you play plenty of games you don't own.

**Status:** design complete (see [map.md](map.md), 12/12 tickets closed). Build not started.
**Context:** MSSA Mini Project — a solo, graded, ~5-hour build ending in a live demo and talk.

---

## 1. Problem & audience

Brett plays board games at home, at cafés, and at conventions, and currently records none of it.
Existing trackers (BGG) demand an account and record ownership as the primary fact, with plays
bolted to it. The single user of v1 is Brett; the immediate second audience is the MSSA grader,
who is scoring C# fundamentals — branching, loops, methods, classes, OOP design, data structures.

**Design consequence:** the domain model *is* the product. Every scoping decision asks "does this
put more of Brett's own code on the page?" — which is why there is no auth, no database, and no
runtime API call.

## 2. What it does

Four screens, all reading one in-memory store that starts populated:

| Screen | Interactive | Job |
|---|---|---|
| **Collection** (landing) | yes | Browse the shelf; search by title/designer; filter by player count |
| **Log a play** | yes | Record a session — pick the game from the **catalog**, not the shelf |
| **Play Log** | yes | History, most recent first, with a per-row **"not owned"** badge |
| **Statistics** | static | LINQ over the history: most played, totals, played-but-not-owned |

## 3. The model (the graded artifact)

Seven types, three aggregates: **GameCatalog** is the world, **GameCollection** is my shelf,
**PlayLog** is my history. Both point *into* the catalog; neither points at the other.

`Game` · `OwnedGame` · `GameCatalog` · `GameCollection` · `Play` · `PlayerResult` · `PlayLog`

Three invariants, each enforced on the class that owns the state:

1. **You can't own the same title twice** — `GameCollection.Add` (Dictionary keyed by title).
2. **A play can't seat more than the game allows** — `Play` ctor, upper bound only (playing solo is legal).
3. **Every play in your log includes you** — `PlayLog.Record`.

`Play` is permissive by design: only game, date, and the owner's presence are required. Scores,
winner, duration and location are optional, because real play logging is lossy and a model that
demands full data doesn't get used. `IsWinner` is a flag, not a computation over scores.

## 4. Non-functional decisions

- **.NET 10 Blazor Web App, `InteractiveServer`** — no JavaScript, no API layer, no JSON boundary.
- **No authentication.** No EF Core, no database. Storage sits behind two interfaces
  (`IGameCatalogSource`, `IMeepleStore`), both singletons, so the swap later touches two files.
- **Zero network at runtime.** The BGG XML API runs *once*, offline, at build time; its output is
  committed as C# source (`static readonly List<Game>`), so seeding cannot fail on stage.
- **One project, three folders** — `Domain/`, `Storage/`, `Data/` — with folder-mapped namespaces,
  making a later split into class libraries a pure file move.

## 5. Success criteria

- Clean build, and the six-step click path runs end to end: populated collection → search →
  player-count filter → log a play of an **unowned** game → it appears on top of the Play Log with
  a "not owned" badge → Statistics moves.
- All six rubric fundamentals are demonstrable in Brett's own code, with LINQ used in place of
  hand-written loops as a stated, defended choice.
- Seed data present at first launch: **28 owned games**, a catalog of **~75–200**, **~60–80 plays**
  across several months — volume enough that filtering looks like work rather than decoration.
- A ~7-minute talk (extensible to ~12) whose through-line is *"the domain model is the app."*

## 6. Explicitly out of scope for v1

Authentication · EF Core / any real database · Azure deployment · the AI curator and rules-helper
chatbots · price tracking · expansions · manual game entry (the catalog is closed by design) ·
automated tests. These are later maps, not cut corners.

## 7. Delivery plan

Five gated sessions, ~5h of estimates under ~6h50 of caps — honestly, ~6.5 hours, of which the
first five produce a completely demoable app. Statistics and Play Log filtering sit in an
"if ahead" tier so a cut is a non-event. Each session ends **at its checkpoint or its cap,
whichever comes first**, with any shortfall paid out of that tier and never out of the next
session. After session 3 the presentation is deliverable. Full plan:
[research/sprint-plan.md](research/sprint-plan.md).
