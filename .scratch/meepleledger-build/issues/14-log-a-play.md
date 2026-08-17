# 14 — Log a play

**What to build:** Record a session at a table — pick the game, set the date, optionally add who was
there, their scores, who won, how long it ran and where — and have it saved. Crucially: **pick the
game from the catalog, so you can log a game you don't own.**

**Blocked by:** 11 — The storage seam; 12 — The shared catalog search picker

**Status:** ready-for-brett

The hardest screen in the app. Budget accordingly.

- [ ] The game is chosen using the picker from ticket 12, which searches the **catalog**
- [ ] A game that is not in the collection can be selected and logged
- [ ] Only the game and the date are required to submit
- [ ] Other players can optionally be added, each with an optional score
- [ ] A winner can be marked **without entering any scores**
- [ ] More than one winner can be marked
- [ ] Duration and location are optional
- [ ] Submitting calls `PlayLog.Record(...)` directly — there is no save step
- [ ] Trying to seat more players than the game allows is rejected with a clear message
- [ ] The page declares `@rendermode InteractiveServer`
- [ ] Commit

## Watch out for

- **The picker searches the catalog, not the collection.** If it searches the shelf, the demo's
  central beat — logging a game you don't own — becomes impossible, and the whole domain model's
  most consequential decision goes unproven.
- **Every play must include you.** `PlayLog.Record` throws otherwise. Make sure the form puts
  `TheGentleBean` in the results rather than relying on the user to type their own name every time.
- **Solo plays must work.** The seat check is an upper bound only — do not add a "you need at least
  N players" validation to the form that the domain model deliberately does not have.
- **A winner with no score is normal, not an edge case.** Co-op wins and uncounted games are why
  `IsWinner` is a flag rather than a computation over scores.
- **`@rendermode InteractiveServer`** — a form is the most painful place to discover this missing.
- **Let the domain throw and catch it.** The seat invariant lives on the `Play` constructor; the form
  should surface that failure, not duplicate the rule.
