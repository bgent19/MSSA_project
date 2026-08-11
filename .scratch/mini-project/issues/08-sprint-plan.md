# Write the hour-by-hour sprint plan

Type: grilling
Status: open
Blocked by: 01, 04, 06, 07 (closed), 05, 10

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

Also now blocked on [Design the demo and presentation narrative](10-demo-narrative.md), which
may hand back a build constraint — anything the talk track needs that the plan would not
otherwise produce.

The answer is the plan itself, written out in full — the artifact Brett works from on
sprint day.
