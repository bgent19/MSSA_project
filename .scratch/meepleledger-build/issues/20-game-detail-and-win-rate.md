# 20 — Game detail screen with per-game win rate

**What to build:** Click a game and see everything about it in one place — its details, every play of
it, and your win rate — so that "do I actually like this game, and am I any good at it?" has an
answer.

**Blocked by:** 15 — The Play Log, with the per-row "not owned" badge

**Status:** if-ahead — **add-back item 3.** Not baseline sprint work.

Estimated ~40 minutes. **The strongest rubric addition on the add-back list**, and it repays a debt:
the prototype ticket explicitly accepted "no home for a per-game win rate" as the cost of dropping
this screen.

A win rate is a `GroupBy` and an aggregate over the play log — **data structures, methods and OOP
design at once** — and it gives the talk's loops beat something richer to point at than the unopened
seeder.

- [ ] Clicking a game from the Collection or the Play Log opens its detail screen
- [ ] The screen shows the game's details and whether you own it
- [ ] It lists every play of that game, via `PlayLog.ForGame(...)`
- [ ] It shows a win rate computed from the plays where you were marked as a winner
- [ ] The win-rate computation lives on a domain class, not in the component
- [ ] The screen handles a game with zero plays without dividing by zero
- [ ] Commit

## Watch out for

- **This is strictly additive.** It does not change the four-screen structure and it does not change
  the six-step click path. The demo stays the demo — do not restructure the talk around it.
- **A game with no plays must not crash.** Win rate over zero plays is the obvious bug here.
- **`IsWinner` is a flag, and it can disagree with the scores.** That was an accepted cost of the
  domain model. Compute the win rate from the flag; do not start reconciling it against scores now.
- **Put the computation on the domain class.** The whole value of this ticket is that it is more
  graded evidence — which it only is if the code lives where the grader looks.
