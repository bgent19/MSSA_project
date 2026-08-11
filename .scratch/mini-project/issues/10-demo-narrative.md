# Design the demo and presentation narrative

Type: grilling
Status: closed
Assignee: claude + Brett (wayfinder session, 2026-08-11)
Blocked by: 05, 07 (closed)

## Question

What is said out loud during the graded presentation, and what does the app have to do to
back it up?

The presentation is graded and has been sitting in the fog since charting, deliberately —
it could not be sharpened until the app's behaviour was settled. It now is.

**Unblocked by [Prototype the screens and the demo click path](07-prototype-screens.md)**,
which fixed the four screens, the populated start, and a six-step click path. That click
path is the *mechanical* demo — which buttons get pressed. This ticket decides the *story*
laid over it: what claim each step is evidence for.

Still blocked on [Choose the game data source](05-choose-data-source.md), because a story
about real data and a story about twenty hand-written rows are different stories.

To settle:

- The through-line, in one sentence. What is the audience meant to conclude?
- Which of the six rubric fundamentals — branching, loops, methods, classes, OOP design,
  data structures — gets *named out loud*, and at which click? Invisible work scores nothing
  if nobody points at it. The `Dictionary`-keyed duplicate guard and the "played but not
  owned" row are the two strongest candidates.
- How long is the slot, and how much of it is live app versus slides or code walkthrough?
- Is any code shown on screen? A domain class with its invariant reads better than a
  component full of markup — decide which file, in advance.
- The failure plan. What is said and done if the app throws on stage? A populated start
  means most of the risk sits in the one live write.
- What gets said about what was *deliberately left out* — no auth, no database, no live API.
  Framed as a decision with a reason, this is evidence of judgement; unmentioned, it looks
  like an omission. The Out-of-scope section of the map is the source material.
- Does the narrative need anything the build does not currently produce? If so, that is a
  constraint on [Write the hour-by-hour sprint plan](08-sprint-plan.md) — and it must land
  there before the plan is written, not after.

Output: the talk track, written out beat by beat against the click path, plus any build
constraint it hands to the sprint plan.

## Resolution

**A 7-minute core with two marked optional beats extending it to ~12** — built as one talk,
not two, so the cut is made by dropping marked sections rather than improvising against a
clock. The guidelines `.docx` says nothing about the presentation at all — no slot length, no
format, no rubric for the talk — and the real number is still unknown, so 7 minutes is the
tightest version worth defending and everything above it is additive.

### The through-line

**"The domain model is the app."** I decided what a *play* is before I wrote a single screen,
and that one decision is why the app can do the thing you just watched it do.

Two other candidates were considered and demoted to *positions* rather than the spine:
"I built something I'll actually use" is the **hook**, because a populated real shelf earns
attention in fifteen seconds; "small on purpose" is the **close**, because that is where
omissions convert into demonstrated judgement. Neither is a claim about design work, which is
what the rubric's *OOP design* line actually rewards — so neither could carry the body.

Known risk, accepted: the spine is the most abstract of the three and needs the click path
standing behind it. The failure plan below is what covers that.

### The beat sheet

| Time | Beat | Said | Shown |
|---|---|---|---|
| 0:00–0:45 | **Hook (B)** | real shelf, real plays, pulled from BoardGameGeek | populated Collection, ~18 games |
| 0:45–4:15 | **Body (A)** | click path steps 1–6, with the fundamentals named below | the live app |
| 4:15–5:30 | **Code beat** | `GameCollection.Add` | a **slide**, not Visual Studio |
| 5:30–6:30 | **Omissions (C)** | no auth → no database → the API, reframed | app or slide |
| 6:30–6:50 | **Close** | "none of it is abandoned — it's the next map" | — |

**Optional beats, in priority order:** (1) the seeder's parse loop — the only real `foreach`,
plus batching and the API in one artifact; (2) the three-box aggregate diagram.

### Which fundamentals get named, and where

Six equal beats would sound like a checklist and will not fit. Tiered instead — three carry a
full beat anchored to a click, three get a clause in passing:

| Fundamental | Named at | Weight |
|---|---|---|
| **OOP design** | steps 4–5, the unowned play | full beat — the spine |
| **Data structures** | step 1 — the collection is `Dictionary`-keyed by title, which is *why* the duplicate guard is one lookup and not a scan | full beat |
| **Classes** | the code beat | full beat |
| **Branching** | clause, on the duplicate guard | passing |
| **Methods** | clause, on `PlayLog.Record` enforcing "every play includes you" | passing |
| **Loops** | clause, steps 2–3 | passing — see below |

**The loops gap, and the decided response.** The app is LINQ end to end — `Search`,
`FilterByPlayerCount`, `MostPlayed`, `RecentFirst` — so Brett never writes a loop where an
examiner can see it. The only honest `foreach` on this map lives in the **seeder**, which the
demo does not open. Rejected: writing a deliberate `foreach` in the app where LINQ belongs
(worse code to satisfy a checklist). Chosen: **say the sentence out loud** — *"the filtering
is LINQ rather than hand-written loops — same iteration, less code to get wrong"* — which
turns the gap into a stated choice and pre-empts the question instead of waiting for it. If
the slot turns out to be 12 minutes, optional beat #1 shows the seeder's parse loop and the
clause becomes a demonstration.

### The code beat

**`GameCollection.Add`, on a slide.**

The spine is a claim about relationships *between* classes — collection and play log both
point at the catalog, neither points at the other — and **no single file shows that**; it
lives in the negative space between three files. `GameCollection.Add` was chosen anyway
because it is the densest fundamentals-per-line artifact on the map: `Dictionary` lookup,
`if` guard, invariant, method boundary — four rubric items in about ten lines.

The spine is carried instead by a **spoken structural sentence over the running app**, said
while the "not owned" badge is still on screen: *"nothing in my collection knows my play log
exists; they both point at the catalog, and that is the only reason I could log a game I
don't own."* Evidence behind you beats a box diagram. The diagram survives as optional
beat #2.

**Slide, not live Visual Studio.** VS on a projector is unreadable at default zoom, may
restore a panel or a build error the moment you switch, and is the likeliest place to stumble
visibly. The slide is the one artifact in the talk that cannot fail, and it lets the usings,
braces and constructor be deleted so the guard is the only thing on screen.

### The failure plan

Two facts from earlier tickets make this survivable, and both were lucky rather than planned
for this purpose:

- **A restart lands on a full app** — [Design the storage seam](06-storage-seam.md) seeds
  in the constructor, so a refresh costs only the live write.
- **The spine's proof exists before any click** — [Choose the game data
  source](05-choose-data-source.md) seeds 3–5 plays of unowned games. **The live write is a
  flourish, not a load-bearing beam.**

| Mode | Looks like | Pre-decided response |
|---|---|---|
| **Clicks do nothing, no error** | the `@rendermode` trap from [Verify the toolchain](04-verify-toolchain.md) | do **not** debug. Name it in one sentence — *"that's the render mode directive, the one trap I documented and still walked into"* — and **move immediately without pausing for a reaction**. Then point at a seeded unowned play. |
| **Unhandled exception** | Blazor error UI, or blank | refresh once. It restarts full. Second failure → go to the slide, finish on the code beat and C. |
| **Wrong-looking but working** | odd count, odd sort | say nothing, keep going. Nobody knows what the number was supposed to be. |

The one-sentence diagnosis was chosen over silence because it buys credibility with an
instructor; the **no pause** is what keeps it from becoming a debugging conversation against
a clock.

**Standing rules:** the app is running before the first word (never `dotnet run` on stage);
the exact six-step path is rehearsed once immediately beforehand on the same machine and
display; a known-good state is committed before walking in, so "revert and rerun" is real.

### Beat C — what was left out, and how it is said

Three of the five Out-of-scope entries, ordered weakest to strongest:

1. **No authentication** — *"Identity is almost entirely scaffolded code. It demonstrates
   none of the six things this project is graded on and would have eaten a fifth of my
   budget."*
2. **No database** — *"Storage sits behind an interface. Swapping to EF Core touches two
   files: the implementation and one line of startup. That was the point of the seam."*
3. **The BGG API — not an omission at all.** *"The app you just watched has no network
   dependency — not because I couldn't call the API, but because I did, once, offline, and
   committed the result. A live call on stage is a coin flip I had no reason to take."*
   [Survey board game data sources](03-survey-data-sources.md) established that BGG failure
   is **total** (`401`), not graceful, which is what makes this a design decision rather than
   a dodge. It is the most senior-sounding sentence available in the talk, and it is true.

Closing on **"none of this is abandoned; it's the next map"** converts the beat from *here is
what is missing* to *here is what is sequenced*.

**Deliberately not spoken: the Blazor WebAssembly / MVC / Razor Pages rejection.** It is a
*pre*-decision the app does not demonstrate, and it invites a "why not X" debate against a
clock. Held as a prepared Q&A answer instead.

### The build constraint handed to the sprint plan

**The Play Log needs a per-row "not owned" badge.**

Working the talk track surfaced a conflict between two already-settled decisions.
[Prototype the screens](07-prototype-screens.md) put the "Owned? no" row on **Statistics**
(step 6) *and* made Statistics **first on the cut list**. So the spine's only visible evidence
sits on the first screen to be cut — and the failure plan's fallback ("point at a seeded
unowned play in the Play Log") does not work either, because as specified the Play Log has no
ownership marker at all and an unowned play looks like every other row.

Rejected: promoting Statistics off the cut list (leaves the spine hostage to the most
expensive screen, and reorders a cut list that was deliberately pre-decided); rewording the
spine if Statistics is cut (means writing two talks and choosing between them while tired).

**Chosen: a `Dictionary` lookup per Play Log row against the collection, and a conditional in
the markup.** Cheapest of the three; the lookup is *on-message* rather than a tax; it moves
the proof onto the screen where the live write already lands, so **step 5 proves the point the
instant Brett hits save** rather than deferring it to step 6; and it makes the spine survive
both the cut list and a dead write path — the two independent ways it could have died.

Statistics keeps the "played but not owned" **count** as a summary and stays first on the cut
list, exactly as ticket 07 intended.
