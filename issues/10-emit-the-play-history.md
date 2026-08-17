# 10 — Emit the play history

**What to build:** ~60–80 plays across the past ~12 months, emitted as committed C# source —
**including 3–5 plays of catalog games that are not in the collection.** That unowned handful is the
demo's spine.

**Blocked by:** 06 — `Play`, `PlayerResult` and `PlayLog`; 09 — Emit the owned-collection seed

**Status:** ready-for-brett

**Commit before the emit**, same as tickets 08 and 09.

Volume matters here. ~60–80 plays is enough that filtering and "most played" look like work rather
than decoration — filtering four rows is not filtering. Weight the data so favourites recur, because
`MostPlayed()` has nothing to say about a flat distribution.

- [ ] ~60–80 plays, spread over roughly the past 12 months
- [ ] **3–5 plays reference catalog games that are *not* in the owned collection**
- [ ] Play counts are weighted so some games clearly recur
- [ ] **Every single play includes `TheGentleBean` among its `Results`**
- [ ] No play has more results than its game's `MaxPlayers`
- [ ] Scores, winners, durations and locations vary — some plays have them, some don't
- [ ] Plays reference `Game` instances **from the catalog seed**
- [ ] The data is fixed, emitted source — **not generated at startup**
- [ ] `dotnet build` is green after the emit
- [ ] Commit

## Watch out for

- **The 3–5 unowned plays are load-bearing, not flavour.** They are the only reason the "not owned"
  badge has anything to show, and they are what makes *plays are independent of ownership*
  demonstrable at all. If you emit a history where every play is of an owned game, the demo's
  central beat has no evidence behind it.
- **Assert the owner is in every play before writing the file.** `PlayLog.Record` throws otherwise —
  and because the store seeds in its constructor, that means the app fails to start.
- **Leave some fields empty on purpose.** `Play` is permissive by design; a history where every play
  has a full score sheet quietly misrepresents the model and wastes the "real play logging is lossy"
  point.
- **Vary the locations.** Convention and café plays are why this app exists; a history that all
  happened at one table is less honest and less interesting to talk about.
- **Fixed data, not generated at startup.** Generating at startup would put the randomness inside
  the graded app and make every run of the demo different.
