# Write the hour-by-hour sprint plan

Type: grilling
Status: claimed
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
- **"Get one ugly screen rendering real data in hour one" is now much cheaper**, because real
  data already exists before the clock starts. That materially strengthens the argument the
  ticket already raised for that ordering over domain-classes-first.
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
