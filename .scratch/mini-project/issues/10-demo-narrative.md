# Design the demo and presentation narrative

Type: grilling
Status: closed
Resolved: 2026-08-11
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

**One claim, seven beats, ten minutes, no slides — and the demo deliberately breaks once.**

Through-line, said in the first fifteen seconds and again in the last fifteen:

> **"I modelled a real hobby accurately, and the app fell out of the model."**

Everything below is evidence for that one sentence. Beats that are not evidence for it were
cut.

### Format

The guidelines document (`MiniProject Guidelines.docx`) says **nothing at all** about the
presentation — no slot length, no format, no slide requirement. Checked, not assumed. The
slot has not been announced, so the talk is designed for the **short, hostile case: ~10
minutes, live app only, no deck.** That case needs zero extra build and no slide time in the
sprint budget. If the slot turns out longer, see the add-back ladder at the end — the talk
grows, it never needs rewriting.

### The rubric callouts

Three fundamentals are **anchored** — named out loud at the exact click where they are on
screen — and two are **swept** in the closing sentence. Naming all six at their own click was
rejected: branching and loops are weak beats and the recitation starts to sound like a
checklist being read.

| Fundamental | Where it is named |
|---|---|
| **Data structures** | Beat 4, the guard trip — `Dictionary` keyed by title |
| **OOP design** | Beat 5, logging an unowned game — two aggregates, neither pointing at the other |
| **Classes + methods** | Beat 4b, on the code file — the invariant living on the class that owns the state |
| Branching, loops | Swept in the closing sentence |

### The talk track, beat by beat

**Beat 1 — Open on the Collection (~45s).** Roughly 18 games, real ones, no explanation
needed. *"This is my actual shelf — about forty games pulled from my BoardGameGeek account,
and about forty plays going back a year. It starts full on purpose; I'll explain why at the
end."* Establishes the through-line and pre-empts "did you type all that in?"

**Beat 2 — Search (~20s).** Type a designer's name; the list narrows live. Say nothing
clever; let it be fast.

**Beat 3 — Filter by player count (~20s).** Narrows again. *"Both of those are LINQ over the
collection — the same filter code the log-a-play picker reuses."*

**Beat 4 — Trip the guard (~30s). The deliberate break.** Add a game that is already on the
shelf. The app refuses with a readable message. *"That's not validation in the UI — the
collection is a `Dictionary` keyed by title, and it refuses duplicates itself. The screen
just reports what the model said."* **This is the data-structures anchor.** It is the only
enforced rule a user can trip, and watching it happen beats being told it exists.

**Beat 4b — The one code file (~45s).** Alt-tab to `GameCollection.Add`, already open,
already scrolled. Fifteen lines: the lookup, the branch, the throw. *"The rule lives on the
class that owns the state, not in the page. Nothing outside this class can put the collection
in a bad state."* **Classes and methods anchor.** One file only — a second file was
considered and cut for time.

**Beat 5 — Log a play of a game I don't own (~60s).** Open the form, pick from the **catalog**
— deliberately a game not on the shelf. *"Half my plays are at cafés and conventions, on
someone else's copy. So plays don't hang off ownership: the collection is one aggregate, the
play log is another, and neither points at the other. That was the decision the whole model
turned on, and it's the reason this form reads the catalog and not my shelf."* **OOP-design
anchor, and the peak of the talk.**

**Beat 6 — Save, land on the Play Log (~30s).** The new play is on top, with the **Owned?**
column reading **no**. *"There it is — a play of a game I don't own. The model allows it
because real life does."* The through-line, demonstrated rather than claimed.

**Beat 7 — Statistics, then close (~40s).** The counter moved. Then the closing sweep and the
omissions beat, below.

**The closing (~30s).** Three sentences, each an omission with its reason and its destination:

> *"There's no login — ASP.NET Identity is mostly generated code and it demonstrates none of
> the six things this is graded on. There's no database — storage sits behind one interface,
> `IMeepleStore`, so swapping it is two files and one line of `Program.cs`. And there's no
> live API call: the BoardGameGeek API returns 401 without a registered token now, so it runs
> once at build time and the demo can't fail on the venue's wifi. Behind all of it: branching
> and loops in the guards and the seeder, methods and classes throughout, LINQ over the
> collection, a `Dictionary` at the centre. The model is the app."*

Framed as three decisions with reasons, this reads as judgement. Left unmentioned, the same
three read as gaps. Source material is the map's Out-of-scope section, and it doubles as the
trailer for map two.

### The failure plan

**Restart and narrate. No recording, no screenshots.** The store is **seeded in its
constructor** ([Design the storage seam](06-storage-seam.md)), so a restart lands on a fully
populated app — the only thing lost is the one play logged live, about twenty seconds of
redo. Pre-decided line, said calmly and moved past:

> *"That's the app throwing — let me restart it; it seeds itself, so we lose nothing but the
> play I just logged."*

Most of the stage risk sits in exactly one place: the single live write in beat 5. Beat 4 is a
*controlled* failure and must not be confused with a real one — hence the requirement below
that the duplicate guard surfaces a message rather than an unhandled exception page.

**Accepted risk:** a build that will not compile has no cover. Rejected the screen recording
and the screenshot set as insurance not worth their rehearsal time, given the demo runs from
a committed, working build.

### Handed to the sprint plan as build constraints

These are the things the *narrative* needs that the build would not otherwise produce.
They land on [Write the hour-by-hour sprint plan](08-sprint-plan.md) before it is written:

1. **The duplicate-add path must surface a readable message on the Collection screen** — a
   `try`/`catch` around `Add` and a string field, roughly ten lines. An unhandled exception
   page kills beat 4 instead of making it.
2. **The add-to-collection form is now non-cuttable.** It is the only way to trip the guard,
   and it lives on the hour-one Collection screen. It cannot be traded away for time.
3. **The Play Log needs an `Owned?` column** — one boolean lookup against the collection per
   row. This is the fix for a conflict this ticket surfaced: the through-line's payoff was
   sitting on the **Statistics** screen, which ticket 07 put **first on the cut list**. Moving
   the proof onto Play Log means the payoff survives two more rounds of cutting and
   **ticket 07's cut order stands unchanged** (Statistics → Play Log filtering → Play Log).
4. **The seed must contain a re-add target** — a game on the shelf that beat 4 can attempt to
   add again. Free, but it must be a *known* title, chosen before stage, not hunted for live.
5. **At least one honest, explicit loop must exist in Brett's own code.** LINQ covers "loops"
   only arguably, and a grader with a checklist may want a `for`/`foreach` to point at. The
   seeder's emit code or a play-log aggregation is the natural home. Cheap to satisfy
   deliberately, awkward to retrofit.
6. **The sprint must end on a runnable, committed build** — see rehearsal, below.
7. **No slide deck in the budget.** The talk is live-app-only by design.

### Rehearsal

**Outside the five hours** — the budget is coding effort, and the talk gets its own evening.
Free against the sprint, with one condition attached, which is constraint 6 above: rehearsal
requires something to rehearse *with*, so the sprint must end with a runnable app committed,
not a working tree mid-edit. Anything rehearsal exposes gets fixed on that separate evening,
outside the budget too.

Rehearsal must include **deliberately killing the app once** and running the restart line, so
the failure plan is tested rather than merely written down. It must also include the beat-4b
alt-tab, which is the fumble most likely to cost thirty seconds live.

### The add-back ladder, if the slot is longer than ten minutes

Ordered. Add from the top; nothing below changes the talk above.

1. **Show `IMeepleStore`** after the closing's database sentence — the seam is the strongest
   evidence of design intent and the closing already points at it.
2. **A second code file** — `Play`'s seat invariant, the other place a rule lives on the class
   that owns the state.
3. **Per-game win rate**, if the build recovered it (ticket 07 cut its home, the game-detail
   screen).
4. **A six-fundamentals slide** the grader can read while you talk — the first thing that
   pulls a deck into an otherwise deck-free talk, so it goes last.
