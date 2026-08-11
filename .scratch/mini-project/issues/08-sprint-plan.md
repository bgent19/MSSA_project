# Write the hour-by-hour sprint plan

Type: grilling
Status: closed
Resolved: 2026-08-11
Assignee: claude + Brett (wayfinder session, 2026-08-11)
Blocked by: 01, 04, 05, 06, 07, 09, 10, 11, 12 (all closed)

## Question

What exactly does Brett do in each of the five hours, and what gets cut if they fall
behind?

This is the destination ticket. When it closes, the map is done: everything is decided and
the sprint is pure execution.

Blocked on every other ticket, because a plan is only worth writing once the decisions it
sequences have been made.

To settle:

- The hour-by-hour sequence. Which piece first? Domain classes before UI is the usual
  answer, but "get one ugly screen rendering real data in hour one" has a strong argument
  too: it proves the whole stack works while there is still time to react.
- Checkpoints. At the end of each hour, what must be true? Named, checkable conditions —
  "the collection list renders seeded games" — not "make progress on the UI."
- The cut list, ordered in advance. When hour four arrives and two features remain, the
  decision must already be made, because tired sprint-brain makes it badly.
- Commit points. Where does Brett commit, so a bad hour costs one hour and not the
  project?
- Where does the 5-hour budget actually go, honestly? Reserve time for the demo rehearsal
  and for things breaking. A plan with no slack is a plan that fails.
- What are the known traps, given everything learned on this map, and what is the
  pre-decided response to each?
- If the true budget turns out to be closer to the guidelines' 8-12 hours, what are the
  first things added back?

**Inputs already fixed by [Prototype the screens and the demo click path](07-prototype-screens.md)** —
do not re-litigate these, sequence them:

- **Hour-one target:** the Collection screen and the Log-a-play form. That pair alone tells a
  whole story.
- **Cut order, pre-decided:** Statistics → Play Log's filtering → Play Log entirely.
- **Styling is time-boxed and goes last**, after the cut list is clear. It is cheap for Brett
  and therefore tempting, and it scores nothing.
- **Four screens, three of them interactive.** Every screen with a button or form needs
  `@rendermode InteractiveServer`; a missing directive fails silently.

**Constraints from [Design the demo and presentation narrative](10-demo-narrative.md)** — it
did hand back a build constraint, plus three items the budget must actually contain:

- **The Play Log needs a per-row "not owned" badge** — a `Dictionary` lookup against the
  collection and a conditional in the markup. Not cosmetic: the talk's spine ("plays are
  independent of ownership") currently has its only visible proof on **Statistics**, which is
  **first on the cut list**, and the failure plan's fallback also needs it. Build it with the
  Play Log, not after. Statistics keeps the summary count and stays first to cut.
- **Rehearsal is in the budget, not after it.** The exact six-step path is run once
  immediately before presenting, on the same machine and display. Reserve the time.
- **A known-good commit before the demo** is a named commit point, so "revert and rerun" is a
  real option on stage.
- **A slide with `GameCollection.Add` on it** is a deliverable — ~5 minutes, the night
  before, not sprint time. Note it so it does not get forgotten.
- The talk is planned to a **7-minute core**; the real slot length is still unknown. Two
  optional beats extend it to ~12, and neither costs build time (the seeder parse loop
  already exists; the aggregate diagram is a drawing).

**Constraints from [Choose the game data source](05-choose-data-source.md) and
[Run the seeding pipeline](11-run-seeding-pipeline.md):**

- ~~**Seeding must be complete before hour 1**~~ — **REVERSED by
  [Run the seeding pipeline](11-run-seeding-pipeline.md).** Brett chose to emit nothing in
  advance, so **the entire seeding run happens inside hour 1**, against a recommendation to
  do it beforehand. See the block below; this is the largest single input this ticket
  receives and the plan must not treat hour 1 as "domain model then UI".
- **The solution has two projects**: the Blazor web app and an unreferenced seeder console
  project. Account for that in the hour-one setup, and note it interacts with the open
  repo-structure question in the map's fog.
- ~~**"Get one ugly screen rendering real data in hour one" is now much cheaper**, because real
  data already exists before the clock starts.~~ **STALE — corrected on resolution.** This was
  written while seeding was still scheduled *before* the sprint, and was invalidated by the
  reversal two bullets up: real data does **not** exist before the clock. The argument does not
  survive, so the ordering question was decided fresh rather than inherited. It was decided
  against screen-first anyway, on the separate ground that
  [ticket 04](04-verify-toolchain.md) had already retired the stack risk that screen-first
  exists to retire.
- **A known trap to pre-decide a response for**: the seed is a generated C# file. If it fails
  to compile mid-sprint, that is a build break across the whole app. Decide in advance
  whether the response is to fix it or to revert to the last committed seed.

**Constraints from [Run the seeding pipeline](11-run-seeding-pipeline.md)** — the pipeline is
**verified but deliberately not run**. Working artifact:
[research/seeding-runbook.md](../research/seeding-runbook.md).

- **Budget the seeding run inside hour 1: realistically 45–60 minutes.** It is a manual
  browser CSV download, ~10 `/thing` calls at 5s spacing, the id union, Brett's LINQ-to-XML
  parse, and three emit steps — **all three emits unproven**. Sequencing hour 1 as
  "domain model, then UI" would be wrong by roughly an hour.
- **The compile-order trap is the sharpest hazard on the whole map.** The emitted seed lands
  in `MeepleLedger/Data/`, inside the web project, so the moment it is generated **the entire
  web app stops building** until `Game`, `OwnedGame`, `Condition`, `Play` and `PlayerResult`
  exist. Order hour 1 so the domain model precedes the emit, or hour 1 opens on a broken
  build. Pre-decide the response to a seed that will not compile: **revert the generated file
  and re-emit**, never hand-patch 200 lines under time pressure.
- **The CSV download should happen the night before.** It needs a logged-in browser session —
  verified *not* reachable with the bearer token — and it is the one step that cannot be
  quickly retried. It is not code, so it costs nothing against Brett's reasoning for
  deferring the rest. Group it with the `GameCollection.Add` slide as night-before work.
- **Real numbers, replacing estimates**: the owned shelf is **28** games, not ~40 (no owned
  expansions — the filter changes nothing). So the catalog needs ~**172** ranked ids to reach
  200, with less overlap than ticket 05 assumed. Data is clean: no zero player counts across
  all 28.
- **The 202 retry queue is a non-event** — `202` then `200`, ~2s. Ticket 03 flagged it as the
  awkward endpoint; budget nothing for it.
- **`BGG_TOKEN` is a User-scope env var.** A newly-opened terminal picks it up; a terminal
  already open before it was set will not. Worth knowing at hour 1 when a `401` would look
  like a dead token.

The answer is the plan itself, written out in full — the artifact Brett works from on
sprint day.

## Resolution

**The plan is [research/sprint-plan.md](../research/sprint-plan.md)** — the full artifact, four
sessions with checkpoints, caps, tiers, commit points and a traps table. This resolution records
what was *decided* to produce it; the plan itself is the deliverable.

### The shape: five sessions, gated by checkpoint, not by clock

| | Session | Est. | Cap | Checkpoint |
|---|---|---|---|---|
| 0 | CSV download, `GameCollection.Add` slide | 20 | 30 | CSV in hand and gitignored; slide exists |
| 1 | Scaffold → domain model → seed emit → green build | 110 | 150 | `dotnet build` green with real seed in `Data/` |
| 2 | Storage impls, shared picker, Collection | 65 | 90 | Collection renders the 28 real owned games |
| 3 | Log a play, Play Log + "not owned" badge | 65 | 90 | A live-logged play appears in the Play Log, badged |
| 4 | Styling, rehearsal, *Statistics if ahead* | 35 | 60 | Six-step path clean; `demo-ready` tagged |
| | | **~5h00** | **~6h50** | |

**Not one sitting.** Split sessions so cut decisions get made rested — the ticket's own worry was
that "tired sprint-brain makes it badly," and a split simply removes the tired brain from the
decision. Cost is a ~5–10 min restart tax per session, absorbed by the caps. **Session 1 is the
one exception and must not be interrupted**, because it crosses the compile-order boundary and the
window between "seed emitted" and "domain model complete" is a broken build.

**The stopping rule is the load-bearing part**, more than the grouping: *a session ends at its
checkpoint or its cap, whichever comes first; the shortfall is paid out of the if-ahead tier,
never out of the next session.* Without it Session 2 silently eats Session 3 and the problem
surfaces at the demo.

### The budget does not close at five hours — said out loud rather than hidden

Costing every committed item against the map's own numbers came to **~290 of 300 minutes**: ten
minutes of slack across a five-hour sprint containing three unproven emit steps and an unfamiliar
framework. A plan with no slack is a plan that fails.

Resolved by **making the cut list the slack, and stating ~6.5 hours as the honest figure.** The
baseline *excludes* Statistics and Play Log filtering, so they are things **added back when
ahead** rather than lost when behind — which makes a cut a non-event instead of a mid-sprint
morale event. Estimates then sum to ~5h and caps to ~6h50.

The alternative — keeping everything in the baseline and calling it five hours — would have made
the number true only by quietly shrinking the deliverable. "This is ~6.5 hours of work, of which
the first five produce a completely demoable app" is both more useful on the day and more honest,
and it sits comfortably inside the guidelines' 8–12.

Two properties the sequencing was built to protect:

- **After Session 3 the presentation is deliverable**, even if Session 4 never happens — Session
  3's checkpoint *is* step 5 of the click path.
- **After Session 1 no unproven step survives** into a session that also contains UI work.

### Session 1 ends with nothing on screen — deliberately

Chose **domain model → full seed → green build**, no UI, over interleaving `Game`-only-then-a-screen.

The usual case for screen-first is risk retirement, and **that risk was already retired by
[Verify the toolchain end to end](04-verify-toolchain.md)** — scaffold, build, serve, hot reload,
and a breakpoint hit in an event handler, all on this machine. Paying sprint time to re-prove it
buys nothing, while interleaving would cross the build-breaking boundary twice, leave two of three
emits unproven, and build the Collection screen against `Game` only to partly rewrite it when
`OwnedGame` lands.

The real cost is **morale, not risk** — ~2 hours of typing with nothing to look at. Answered with a
scheduled **5-minute run-it-and-look-at-it beat** after scaffolding: the session's only visual
feedback, budgeted rather than stolen.

This also required correcting a stale premise in this ticket's own body — see the struck bullet
above.

### Bad seed data is filtered at emit time, not rejected at startup

The sharpest trap the map had *not* pre-decided. Two invariants can be tripped by seed data: the
seat invariant on `Play` (the ~172 ranked ids are a much weirder sample than the verified-clean 28
owned) and `PlayLog.Record`'s "every play includes you."

What makes it worse than a normal bug: [the store is a singleton seeded in its
constructor](06-storage-seam.md), so a violating row does not produce a bad row — **it throws
inside singleton construction and the app fails to start**, wrapped in a DI exception. On sprint
day that reads as "Blazor is broken," not "row 147 is bad."

**Decided: the seeder drops `maxplayers < 1` and asserts every play includes `TheGentleBean`
before writing.** Bad data never reaches the domain.

**Explicitly rejected: an internal load path that bypasses validation when seeding.** It would mean
the graded invariants are not the thing actually guarding the app's data — which directly undercuts
the "the domain model is the app" spine that [the demo narrative](10-demo-narrative.md) built the
entire talk on. *An invariant you can bypass is not an invariant.* The filter is ~4 lines of LINQ
`Where`, lives in the seeder where it costs the graded app nothing, and earns a Q&A sentence:
*validate at the boundary so the domain model can trust its inputs.* Accepted cost: the catalog may
come out at 196 rather than 200, which is immaterial — see next.

### The catalog is a range (~75–200), which makes the CSV failure a non-decision

The CSV download is the most fragile step on the map: not automatable (verified — the bearer token
returns an Angular shell), not quickly retryable, and it gates the catalog.

Checking what actually depends on "200" found that **nothing does**. What is load-bearing is
*catalog wider than shelf* — for closing [ticket 02](02-domain-model.md)'s unlisted-game gap by
width, for making plays-independent-of-ownership demonstrable, and for the search box being worth
having. **~75 games (28 owned + ~50 from `/hot`) satisfies all three**, and the click path is
untouched.

So the target is a **range**, `/hot` is a **pre-sanctioned fallback requiring no decision on the
day**, and the plan says plainly: *do not spend Session 1 fighting the download.* The fallback is
also ~10 minutes faster (4 API calls instead of 10). Accepted cost: a 75-game catalog makes the
search box less obviously *necessary* on stage — demo aesthetics, not function, and the talk never
leans on catalog size.

**Session 0 moves to at least two days before Session 1**, so a retry is free. The slide has no
failure mode and can stay genuinely night-before.

### Commit points, and one that is load-bearing

**`main`, no feature branch** — solo, no reviewer, and an examiner reading history sees a linear
story. Four checkpoint commits (green builds only), a `demo-ready` **tag**, and **one commit
immediately before each seed emit**.

That last one is not hygiene. The pre-decided response to a seed that will not compile is *revert
the generated file and re-emit, never hand-patch 200 lines* — and **that response only exists if
the pre-emit state is committed.** Accepted cost: history carries commits reading "before seed
emit." Worth it; a 30-second recovery versus a 20-minute one. A stash was rejected — it is exactly
the thing you lose track of at hour four.

Rules: never commit red, and **never end a session red** (it poisons the next session's start).

### Add-back order if the budget is really 8–12 hours

1. **Statistics** (15) · 2. **Play Log filtering** (10) · 3. **Game detail + per-game win rate**
(40) · 4. **Three invariant unit tests** (30) · 5. **Edit/delete a play** (30). Baseline plus all
five lands at ~8–9 hours.

**#3 repays a debt**: [the prototype ticket](07-prototype-screens.md) accepted "no home for a
per-game win rate" as the cost of dropping the game-detail screen. A win rate is a `GroupBy` and an
aggregate — data structures, methods and OOP design at once — and it gives the talk's loops beat
something richer to point at than the unopened seeder. Strictly **additive**: four-screen structure
and six-step click path both unchanged.

**#4 clears the testing fog** — resolved as *not in the five-hour sprint; the first tests are three
invariant tests (duplicate title, over-seated play, play without you), entering at add-back position
4.* Component tests are not on the list. That patch leaves the map's **Not yet specified** entirely
rather than surviving to map two.

### Traps table

Nine traps with pre-decided responses, all in the plan. The three worth naming here because they
would otherwise cost the most time: **a missing `@rendermode InteractiveServer` fails silently with
no error at all** and is therefore the *first* thing to check on any dead click; a **`401` from a
terminal opened before `BGG_TOKEN` was set** looks exactly like a dead token, so open a fresh
terminal before suspecting the token; and **throttling shows as `500`/`503`, never `429`**.

### The map is complete

This was the destination ticket. Every decision the sprint needs is made and written down, and no
tickets remain.
