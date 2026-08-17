# 18 — Statistics screen

**What to build:** A static summary screen — total games owned, total plays logged, most-played
games, and the games you've played but don't own — so that the collection tells you something about
yourself rather than just listing itself.

**Blocked by:** 15 — The Play Log, with the per-row "not owned" badge

**Status:** if-ahead — **add-back item 1.** Not baseline sprint work. Build this only if you are
ahead of schedule; not building it is a non-event, not a cut.

Estimated ~15 minutes. Cheap, and it completes the four-screen structure.

- [ ] A count of games owned
- [ ] A count of plays logged
- [ ] Most-played games, via `PlayLog.MostPlayed()`
- [ ] The games played but not owned
- [ ] The screen is **static** — no interactivity, so no `@rendermode` needed
- [ ] Commit

## Watch out for

- **This screen is deliberately static.** It is the only one of the four that is, and that is what
  makes it cheap.
- **The played-but-not-owned list used to be the spine's only visible proof.** It isn't any more —
  the "not owned" badge on the Play Log took that job precisely because this screen is first to cut.
  So build this for completeness, not because the demo needs it.
- **`MostPlayed()` already exists on `PlayLog`** as a `GroupBy` and an aggregate. Call it; don't
  rewrite it here.
