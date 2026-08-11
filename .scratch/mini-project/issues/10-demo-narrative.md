# Design the demo and presentation narrative

Type: grilling
Status: claimed (Brett)
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
