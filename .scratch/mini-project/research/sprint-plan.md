# The sprint plan

The artifact Brett works from on sprint day. Resolves
[Write the hour-by-hour sprint plan](../issues/08-sprint-plan.md), the destination ticket of
[the map](../map.md).

**This document does not re-decide anything.** Every choice it sequences was settled by an
earlier ticket; where a number or a rule came from somewhere else, it is linked. Read it as an
order of operations, not as an argument.

Two documents belong open alongside it on the day:
[the seeding runbook](seeding-runbook.md) during Session 1, and
[the demo narrative](../issues/10-demo-narrative.md) during Session 4.

---

## The honest budget

| | Session | Est. | Cap | Checkpoint — done when |
|---|---|---|---|---|
| **0** | CSV download, `GameCollection.Add` slide | 20 | 30 | CSV in hand and gitignored; slide exists |
| **1** | Scaffold → domain model → seed emit → green build | 110 | 150 | `dotnet build` green with real seed data in `Data/` |
| **2** | Storage impls, shared picker, **Collection** | 65 | 90 | Collection renders the 28 real owned games |
| **3** | **Log a play**, **Play Log + "not owned" badge** | 65 | 90 | A live-logged play appears in the Play Log, badged |
| **4** | Styling, rehearsal, *Statistics if ahead* | 35 | 60 | Six-step path run clean; `demo-ready` tagged |
| | | **~5h00** | **~6h50** | |

**Estimates sum to five hours; caps sum to just under seven.** That gap is the plan's slack and
it is deliberate — the map budgeted 5 hours as "the safe number" against guidelines of 8–12, so
there is no cost to overrunning into the caps and no need to justify it to anyone.

State the honest figure rather than the flattering one: **this is ~6.5 hours of work, of which
the first five produce a completely demoable app.**

### Where the slack is, and why it is not spread evenly

Session 1 carries the largest buffer — 40 minutes against a 110-minute estimate — because it
contains **all three unproven emit steps** and the only build-breaking hazard on the map. Every
other session is doing work whose shape is already known.

### The two properties worth protecting

- **After Session 3, the presentation is deliverable** even if Session 4 never happens. Session
  3's checkpoint *is* step 5 of the click path. Styling and Statistics are polish; the spine is
  done at the end of 3.
- **After Session 1, the risky half of the project is retired.** No unproven step survives into
  a session that also contains UI work.

---

## Session structure

### The stopping rule

> **A session ends when its checkpoint is true, or at its cap — whichever comes first.**
> If the cap arrives first, stop anyway. The shortfall is paid out of the *if-ahead* tier,
> never out of the next session.

This is the rule that makes the cut list function as slack rather than as an emergency. Without
it, Session 2 silently eats Session 3 and the problem surfaces at the demo. See
[the tiers](#the-tiers).

### Sessions are split, not contiguous

Four working sessions plus a Session 0, rather than one five-hour block. Cut decisions get made
rested, between sessions, which is where they are made well — the ticket's own concern was that
"tired sprint-brain makes it badly."

The cost is a restart tax of roughly 5–10 minutes per session, which is inside the caps.

**Session 1 is the exception and must not be interrupted partway.** It crosses the compile-order
boundary (see [the traps](#traps-and-their-pre-decided-responses)), and the window between "seed
emitted" and "domain model complete" is a broken build. Do not stop inside it.

Dates are deliberately relative rather than pinned. The only calendar constraints:

- **Session 0 lands at least two days before Session 1** — see below.
- **Session 4 is adjacent to the presentation**, because rehearsal must be on the same machine
  and display, immediately before presenting
  ([demo narrative](../issues/10-demo-narrative.md)).

---

## Session 0 — before the clock starts

**Est. 20 min · Cap 30 min · At least two days before Session 1**

Neither item is code, so neither spends sprint budget. Both are things that are annoying to
discover missing.

1. **Download the BGG CSV dump by hand**, from a logged-in browser, at
   `https://boardgamegeek.com/data_dumps/bg_ranks`.
   **Do not attempt this in code** — the seeding runbook verified that the bearer token
   returns an Angular HTML shell, because it authorizes the XML API and not site pages.
   Save it outside the repo, or add it to `.gitignore`. It is an input, not a deliverable.
2. **Make the `GameCollection.Add` slide** — one slide, the method on it, no IDE.
   The [demo narrative](../issues/10-demo-narrative.md) fixed this as the code beat and fixed
   that it is a slide rather than live Visual Studio.

### Why two days early and not the night before

The CSV download is the single most fragile step in the plan: not automatable, not quickly
retryable, and it gates the catalog. Two days early makes a second attempt free. The night
before gives zero retries if BGG is down.

The slide has no failure mode and can genuinely be night-before work.

**Checkpoint:** the CSV is on disk and gitignored, and the slide exists.

---

## Session 1 — domain model and seed

**Est. 110 min · Cap 150 min · Do not interrupt**

The largest session, and the one that ends with **no UI at all**. That is intended.

### Why no screen renders in this session

The usual argument for "get one ugly screen up first" is that it proves the stack works while
there is time to react. **That risk was already retired** by
[Verify the toolchain end to end](../issues/04-verify-toolchain.md): scaffold, build, serve,
hot reload, and a breakpoint hit in an event handler, all confirmed on this machine. Paying
sprint time to re-prove it would buy nothing.

> **Note on a stale premise.** Ticket 08's body argued that a screen-first hour was "much
> cheaper, because real data already exists before the clock starts." That was true when seeding
> was scheduled *before* the sprint, and was reversed by
> [Run the seeding pipeline](../issues/11-run-seeding-pipeline.md), which moved the entire
> seeding run inside the sprint. The argument does not survive the reversal, and the ordering
> question was decided fresh rather than inherited.

The real cost of this ordering is **morale**, not risk — nearly two hours of typing with nothing
to look at. Step 2 below exists to blunt that, and nothing else.

### Order of operations

1. **Scaffold** — `dotnet new blazor -n MeepleLedger -int Server -au None`, the exact line
   confirmed by [ticket 04](../issues/04-verify-toolchain.md). Then the folders and namespaces
   settled by [Decide the project and folder structure](../issues/12-solution-structure.md):
   `Domain/`, `Storage/`, `Data/`, and exactly two `@using` lines in `_Imports.razor` —
   `MeepleLedger.Data` is **excluded on purpose**. `MeepleLedger.Seeder/` already exists.
   *(~15 min)*

2. **Run it and look at it — 5 minutes, deliberately.** The default page, in a browser. This is
   the only visual feedback in the session and it is scheduled rather than stolen. *(5 min)*

3. **Write the whole domain model** — the seven types across three aggregates fixed by
   [Model the domain](../issues/02-domain-model.md), with all three invariants on the classes
   that own the state. This is the graded artifact and the thing most worth writing unhurried.
   *(~40 min)*

4. **`dotnet build` — must be green before going near the seeder.** This is the compile-order
   guard. *(~2 min)*

5. **Commit.** Mandatory, load-bearing — see [commit points](#commit-points). This commit is what
   makes "revert the generated file and re-emit" possible.

6. **Run the seeding pipeline** — follow [the runbook's](seeding-runbook.md) checklist, steps
   1–7. Emit, build, commit before each subsequent emit. *(~45–60 min)*

7. **Final green build, eyeball the owned rows, commit.** Confirm with `git status` that no
   token and no raw API response dump is in the commit.

### The emit-time filter

The seeder **drops bad rows before writing**, rather than letting the app's aggregates reject
them at startup:

- Drop any catalog game with `maxplayers < 1`.
- Assert every emitted play includes `TheGentleBean` in its `Results`.

This is not defensive habit — it is required by the shape of the storage seam. Because
[the store is a singleton seeded in its constructor](../issues/06-storage-seam.md), a violating
row does not produce a bad row. It throws *inside singleton construction*, so **the app fails to
start**, wrapped in a DI exception. On sprint day that reads as "Blazor is broken," not as "row
147 is bad."

The alternative — an internal load path that bypasses validation when seeding — was **rejected**.
It would mean the graded invariants are not the thing actually guarding the app's data, which
directly undercuts the "the domain model is the app" through-line the whole talk is built on. An
invariant you can bypass is not an invariant.

The filter is ~4 lines of LINQ `Where`, lives in the seeder where it costs no graded-app
complexity, and is itself worth a sentence in Q&A: *validate at the boundary so the domain model
can trust its inputs.*

Accepted cost: the catalog may come out at 196 rather than 200. Nobody will notice — see the
next section.

### The catalog is a range, not a number

**Target ~75–200 games.** The figure "200" from
[Choose the game data source](../issues/05-choose-data-source.md) is a preference, not a
requirement, and nothing downstream depends on it:

| Depends on | Actually needs | Satisfied by ~75? |
|---|---|---|
| Closing the unlisted-game gap "by width" | catalog wider than the 28-game shelf | yes |
| Plays-independent-of-ownership being demonstrable | some catalog games unowned | yes — ~47 |
| The search box being worth having at all | scrolling worse than typing | yes, marginally |
| The six-step click path | the "not owned" row | unaffected |
| The literal figure 200 | — | — |

So **if the CSV is not in hand when Session 1 starts, fall back to
`/xmlapi2/hot?type=boardgame`** (50 items, works with the token, verified) and move on. This
needs no decision on the day: it is pre-sanctioned. The fallback is also *faster* — roughly 4
API calls instead of 10, saving ~10 minutes.

**Do not spend Session 1 time fighting the download.**

Accepted cost: a 75-game catalog makes the search box slightly less obviously necessary in the
demo. That is demo aesthetics, not function, and the talk never leans on catalog size.

**Checkpoint:** `dotnet build` is green, with real seed data in `MeepleLedger/Data/`, committed.

---

## Session 2 — storage and the Collection screen

**Est. 65 min · Cap 90 min**

1. **Storage implementations and DI** — `SeededGameCatalogSource` and the in-memory
   `IMeepleStore`, both `AddSingleton`, both sync, five members and zero methods, per
   [Design the storage seam](../issues/06-storage-seam.md). There is no `Save`; it was deleted
   rather than stubbed. *(~15 min)*
2. **The shared search-box picker** — one picker, reused by both the add-to-collection and
   log-a-play flows ([ticket 05](../issues/05-choose-data-source.md)). Build it once here; Session
   3 consumes it. *(~20 min)*
3. **The Collection screen** — the landing page. First real component, so the learning curve is
   priced in. `@rendermode InteractiveServer`. *(~30 min)*

### Why this checkpoint matters more than it looks

This is the first moment the whole stack is *yours* end to end — your domain model, your storage
seam, your component, your 28 games. If anything about the Blazor Server bet is wrong in
practice, it surfaces here, with two full sessions still in hand.

**Checkpoint:** the Collection screen renders the 28 real owned games from seed data.

---

## Session 3 — the demo's spine

**Est. 65 min · Cap 90 min**

1. **Log a play** — the form, the player results, consuming Session 2's picker. The hardest
   screen. `@rendermode InteractiveServer`. *(~35 min)*
2. **Play Log, with the per-row "not owned" badge** — a `Dictionary` lookup against the
   collection and a conditional in the markup. `@rendermode InteractiveServer`. *(~30 min)*

**Build the badge with the Play Log, not after it.** The
[demo narrative](../issues/10-demo-narrative.md) handed this down as a build constraint, not a
polish item: the talk's spine ("plays are independent of ownership") originally had its only
visible proof on Statistics, which is first to cut. The badge moves that proof onto the screen
where the live write lands, so step 5 proves the point on save. The failure plan needs it too.

**Checkpoint:** a play logged live in the running app appears in the Play Log with a correct
"not owned" badge. **This is step 5 of the click path** — when it is true, the presentation is
deliverable.

---

## Session 4 — polish, rehearsal, tag

**Est. 35 min · Cap 60 min · Adjacent to the presentation**

1. **Styling, time-boxed** — the box is the box. Styling is cheap for Brett and therefore
   tempting, and it scores nothing on the rubric
   ([prototype ticket](../issues/07-prototype-screens.md)). *(~20 min, hard stop)*
2. **Rehearse the six-step click path once**, on the same machine and the same display,
   immediately before presenting. Not optional and not "after the budget" — it is in it. *(~15
   min)*
3. **Tag `demo-ready`.** During rehearsal, actually run the revert command once, so that on stage
   it is a command you have typed before rather than a `git log` excavation.
4. **Statistics, only if ahead** — see [the tiers](#the-tiers).

**Checkpoint:** the six-step path runs clean end to end, and `demo-ready` is tagged.

---

## The tiers

The cut list and the add-back list are the same list, read from different ends. Both are ordered
**in advance**, which is the entire point — the decision must already be made before the moment
it is needed.

### Baseline — what the plan builds

Collection · Log a play · Play Log with the "not owned" badge · styling · rehearsal.

Four screens' worth of structure minus Statistics. This is a complete, demoable app that
satisfies every beat of the talk.

### If ahead — added back in this order

| # | Item | Est. | Why here |
|---|---|---|---|
| 1 | **Statistics** (static, summary count) | 15 | Cheap; completes the four-screen structure |
| 2 | **Play Log filtering** | 10 | LINQ over a real collection, visible in the demo |
| 3 | **Game detail + per-game win rate** | 40 | Strongest rubric addition — see below |
| 4 | **Three invariant unit tests** | 30 | Proves the through-line rather than asserting it |
| 5 | **Edit and delete a play** | 30 | CRUD completeness; mostly branching. Least interesting |

Baseline plus all five lands around 8–9 hours, inside the guidelines' 8–12.

**On #3.** The game-detail screen repays a debt: the
[prototype ticket](../issues/07-prototype-screens.md) explicitly accepted "no home for a per-game
win rate" as a cost of dropping it. A win rate is a `GroupBy` and an aggregate over the play log
— data structures, methods and OOP design at once — and it gives the talk's loops beat something
richer to point at than the unopened seeder. It is **strictly additive**: it does not change the
four-screen structure and it does not change the six-step click path. The demo stays the demo.

**On #4.** Three tests, one per invariant: duplicate title rejected, over-seated play rejected,
play without you rejected. Nothing else. Component tests are not on this list.

### If behind — cut in reverse

Because Statistics and Play Log filtering are not in the baseline, **falling behind means not
adding them, rather than losing something already planned.** A cut is a non-event.

If you fall behind the baseline itself, the pre-decided order is: Play Log filtering →
Statistics → Play Log entirely. Styling is time-boxed and therefore cannot overrun into anything
else.

---

## Commit points

**Branch: `main`, no feature branch.** Solo project, no reviewer; an examiner reading history
sees a clean linear story rather than a merge. `git revert` and `git reset` give "throw away a
bad hour" just as well on `main`.

Five mandatory commits plus a tag:

| When | Why |
|---|---|
| End of each session checkpoint (×4) | Green build only. A bad session costs one session |
| **Immediately before each seed emit step** | **Load-bearing** — see below |
| Before the demo | Tagged `demo-ready` |

### The pre-emit commit is not hygiene

The pre-decided response to a seed file that will not compile is **revert the generated file and
re-emit — never hand-patch 200 lines under time pressure**. That response *only exists if the
pre-emit state is committed.* Skip this commit and the trap response is unavailable at exactly
the moment it is needed.

Accepted cost: history will contain commits whose message is essentially "before seed emit,"
which reads slightly worse than a clean narrative. Worth it — it is the difference between a
30-second recovery and a 20-minute one. **Do not substitute a stash**; a stash is precisely the
thing you lose track of at hour four.

### Two rules

- **Never commit a red build.**
- **Never end a session on a red build.** The compile-order sequence is designed so this never
  has to happen — domain model always precedes emit — but if you are somehow red at a cap,
  revert to green before stopping. Ending red poisons the start of the next session, and
  [ticket 11](../issues/11-run-seeding-pipeline.md) flagged this specifically.

---

## Traps and their pre-decided responses

Every one of these is a thing the map actually discovered. The response is decided here so that
none of them is thought about on the day.

| Trap | How it presents | Pre-decided response |
|---|---|---|
| **Missing `@rendermode InteractiveServer`** | A button does nothing. **No error, no exception, no console output** | First thing to check on *any* dead click, before anything else. `-int Server` is per-page interactivity ([ticket 04](../issues/04-verify-toolchain.md)) |
| **Emitted seed will not compile** | Whole web app stops building | **Revert the generated file, fix the seeder, re-emit.** Never hand-patch the generated file |
| **`BGG_TOKEN` returns 401** | Looks exactly like a dead or revoked token | It is a User-scope env var. A terminal opened *before* it was set will not see it. **Open a fresh terminal first**, then suspect the token |
| **API throttling** | `500` or `503` — **not** `429` | You went too fast. Wait, redo that batch. 5s between calls |
| **CSV dump unavailable or login fails** | Session 0 or 1 blocked | Fall back to `/hot`, accept a ~75-game catalog, move on. No decision required |
| **Seed row violates an invariant** | App fails to *start*, wrapped in a DI exception | Cannot happen — the emit-time filter drops them. If it does, the filter has a bug; fix the seeder, re-emit |
| **VS launches the wrong startup project** | The seeder console runs instead of the web app | Known accepted risk from [ticket 12](../issues/12-solution-structure.md). Set the startup project explicitly at scaffold time |
| **Game has multiple designers** | 20% of them do | Take the first: `.FirstOrDefault()?.Attribute("value")?.Value ?? "Unknown"` |
| **Hot reload appears broken** | Edits do not show | "Apply Hot Reload on File Save" — verify it is checked ([ticket 04](../issues/04-verify-toolchain.md)) |

---

## Not sprint work, but do not forget it

- The **`GameCollection.Add` slide** — Session 0.
- The **presentation slot length is still unknown.** The talk is planned to a 7-minute core with
  two marked optional beats extending it to ~12; neither optional beat costs build time. Find out
  the real number if you can, but the talk survives not knowing.
- The **CSV file must stay out of the repo** — gitignored or stored outside it.
- **`BGG_TOKEN` must never reach a commit**, and never reaches the web app's configuration at
  all. Seeding is build-time only.
