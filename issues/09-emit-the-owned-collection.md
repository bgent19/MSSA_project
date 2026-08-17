# 09 — Emit the owned-collection seed

**What to build:** The 28 games actually on Brett's shelf, emitted as committed C# source, so that
the app opens on a real collection rather than invented rows.

**Blocked by:** 08 — Parse the XML and emit the catalog seed

**Status:** ready-for-brett

**Commit before the emit**, same as ticket 08, for the same reason.

The count is **28**, verified — not the ~40 an earlier ticket estimated. `own=1` with and without
`excludesubtype=boardgameexpansion` both return exactly 28, all of `subtype=boardgame`. There are no
owned expansions to filter. Do not go hunting for twelve missing games.

- [ ] The seed emits `OwnedGame` instances for the 28 owned titles
- [ ] Each `OwnedGame` references a `Game` **from the catalog seed**, not a freshly constructed
      duplicate
- [ ] No title appears twice — otherwise `GameCollection.Add` throws on its own seed data
- [ ] `DateAcquired`, `Condition` and `Notes` are populated plausibly
- [ ] `dotnet build` is green after the emit
- [ ] Eyeball the 28 rows — do they look like a real shelf?
- [ ] Commit

## Watch out for

- **The owned games and the catalog must share the same `Game` instances.** If the collection builds
  its own `Game` objects, the catalog and the shelf will disagree about what "Catan" is, and the
  "not owned" badge on the Play Log — the demo's whole point — will be wrong. The store's constructor
  depends on the catalog source precisely so this works out.
- **A duplicate title in the seed does not produce a duplicate row.** It throws inside singleton
  construction and the app fails to start. Same failure mode as ticket 08's bad rows.
- **28 clears the bar comfortably.** The prototype wanted ~18 games visible on the Collection screen,
  so there is no need to pad it.
