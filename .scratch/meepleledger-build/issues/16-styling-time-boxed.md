# 16 — Styling, hard time-box

**What to build:** The app looks presentable on the display it will actually be shown on. Nothing
more.

**Blocked by:** 15 — The Play Log, with the per-row "not owned" badge

**Status:** ready-for-brett

**The box is the box.** ~20 minutes, hard stop.

Styling is cheap for you and therefore the most tempting way to spend time you do not have, and it
scores **nothing** on the rubric — which marks branching, loops, methods, classes, OOP design and
data structure use. Every minute here is a minute not spent on something graded.

- [ ] The four screens are legible and not visibly broken
- [ ] The "not owned" badge is clearly visible at presentation distance
- [ ] Tables do not overflow or wrap badly on the presentation display
- [ ] Navigation between screens is obvious
- [ ] You stopped at the time box
- [ ] Commit

## Watch out for

- **Styling cannot overrun into anything else** — that is the entire reason it is time-boxed rather
  than merely scheduled last. If you are behind, this is not where you catch up.
- **Check it on the actual presentation display**, not just your monitor. A badge that reads fine at
  desk distance can vanish on a projector.
- **Long designer names may wrap.** Only one designer is stored per game precisely to avoid a cell
  wrapping to three lines on the Collection screen — if it still wraps, truncate in the markup rather
  than reopening the domain model.
