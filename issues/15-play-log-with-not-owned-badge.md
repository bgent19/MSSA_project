# 15 — The Play Log, with the per-row "not owned" badge

**What to build:** Your history, most recent first — and each row says clearly whether you own that
game. A play you just logged appears at the top immediately, badged if it's a game you don't own.

**Blocked by:** 14 — Log a play

**Status:** ready-for-brett

**This is step 5 of the six-step click path. When it works, the presentation is deliverable** even if
nothing else gets built.

**Build the badge with the screen, not after it.** It is a build constraint, not a polish item. The
talk's spine — *plays are independent of ownership* — originally had its only visible proof on the
Statistics screen, which is first to cut. Moving the proof onto the screen where the live write lands
means step 5 proves the point on save.

- [ ] Plays render most-recent-first, via `PlayLog.RecentFirst()`
- [ ] Each row shows the game, the date, and who played
- [ ] Each row shows a **"not owned" badge** when the game is not in the collection
- [ ] The badge is a `Dictionary` lookup against the collection plus a conditional in the markup
- [ ] A play logged live in the running app appears at the top of the list immediately, correctly
      badged
- [ ] Plays of owned and unowned games sit in the **same** list
- [ ] The page declares `@rendermode InteractiveServer`
- [ ] Commit

## Watch out for

- **The badge must be right, not just present.** It is the visible proof of the app's central design
  decision, and an examiner will read it in about ten seconds. If it is wrong, the catalog and the
  collection are probably holding different `Game` instances — see ticket 09.
- **Do not make the Play Log consult ownership through the play.** A `Play` references `Game`, never
  `OwnedGame`; the screen asks the collection whether it holds that title. The shelf and the history
  still never speak to each other — the *screen* is what joins them.
- **The seeded unowned plays are what make this demoable before you log anything live.** That is
  deliberate: the spine's proof is seeded, so the live write is a flourish, not a load-bearing beam.
  If a live write fails on stage, the badge is already on screen.
- **Filtering the Play Log is not in this ticket.** It is an if-ahead item, so leaving it out is not
  a cut.
- **`@rendermode InteractiveServer`.**
