# 19 — Play Log filtering

**What to build:** Narrow the play history — by game, and by whether you own it — so that finding a
particular evening in 60–80 plays is typing rather than scrolling.

**Blocked by:** 15 — The Play Log, with the per-row "not owned" badge

**Status:** if-ahead — **add-back item 2.** Not baseline sprint work.

Estimated ~10 minutes. LINQ over a real collection, visible in the demo.

- [ ] The play log can be filtered by game
- [ ] The play log can be filtered to show only games you don't own
- [ ] Filtering calls `PlayLog.ForGame(...)` where it applies, rather than reimplementing it
- [ ] The filtered list still shows the "not owned" badge correctly
- [ ] The page still declares `@rendermode InteractiveServer`
- [ ] Commit

## Watch out for

- **This is worth having mainly because there is enough seed data for it to look like work.**
  Filtering 60–80 plays is filtering; filtering four rows is decoration. That volume is the reason
  the seed is the size it is.
- **Do not let this pull logic out of `PlayLog`.** If a new query shape is needed, it belongs on the
  aggregate as a method, not inline in the component.
