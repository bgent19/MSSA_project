# 23 — Add a game to the collection from the picker

**What to build:** Put a new game on your shelf — find it in the catalog with the picker, set its
condition and acquisition date, and add it — with the app refusing a title you already own.

**Blocked by:** 12 — The shared catalog search picker; 13 — The Collection screen

**Status:** if-ahead — **not on the original add-back list.** See below.

## Why this ticket exists

The spec justifies the shared picker as *"one picker, reused by both the add-to-collection and
log-a-play flows"* — but the sprint plan never budgets an add-to-collection flow anywhere. So in the
baseline the picker has exactly **one** consumer, and that justification is half unspent.

This was surfaced rather than resolved silently. It is filed here — outside the baseline and outside
the pre-decided add-back order — so the gap is visible without quietly expanding a tight sprint. The
seed already supplies 28 owned games, so nothing in the demo needs this.

**It does have one real attraction:** it is the only place `GameCollection.Add` — the method on the
presentation slide — runs live in the app. Worth weighing against add-back item 3 if you get there.

- [ ] A game can be found in the catalog using the ticket 12 picker and added to the collection
- [ ] Condition is chosen from the enum's values as a dropdown
- [ ] An acquisition date can be set
- [ ] Adding a title already on the shelf is **refused with a clear message**, via
      `GameCollection.Add`'s own guard clause
- [ ] The new game appears on the Collection screen immediately
- [ ] The page declares `@rendermode InteractiveServer`
- [ ] Commit

## Watch out for

- **Let `GameCollection.Add` throw and surface it.** Do not pre-check for a duplicate in the
  component — that would move the business rule out of the domain class and put a second, drifting
  copy of it in the UI. Catching the guard clause's exception *is* the demonstration.
- **The added game must be a `Game` instance from the catalog**, not a new one, or the "not owned"
  badge on the Play Log will start lying.
- **There is still no manual game entry.** If a game is not in the catalog, it cannot be added — the
  unlisted-game gap is closed by catalog *width*, by decision. Do not add a free-text fallback here.
