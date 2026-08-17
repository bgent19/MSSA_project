# 13 — The Collection screen

**What to build:** The landing page. Open the app and see the 28 games you actually own, search them
by title or designer, and filter them by player count — so that "what can five of us play tonight?"
is answerable without doing arithmetic on every box.

**Blocked by:** 11 — The storage seam

**Status:** ready-for-brett

This is the first moment the whole stack is yours end to end: your domain model, your storage seam,
your component, your 28 games.

- [ ] The Collection screen is the **landing page** — opening the app costs zero clicks to reach it
- [ ] It renders all 28 real owned games from seed data on first load
- [ ] Each row shows title, designer, player count and playtime
- [ ] A search box filters by title **and** designer
- [ ] A player-count filter narrows the list
- [ ] Search and filter call `GameCollection`'s own methods — no LINQ reimplemented in the component
- [ ] The page declares `@rendermode InteractiveServer`
- [ ] Navigating away and back, and pressing F5, both leave the collection intact
- [ ] Commit

## Watch out for

- **`@rendermode InteractiveServer` or the search box silently does nothing.** No error, no
  exception, no console output. First thing to check on any dead click.
- **If F5 empties the collection, the store is registered `AddScoped`.** Change it to
  `AddSingleton`. This is the failure that would otherwise happen in front of the examiner.
- **The screen calls domain methods; it does not contain the logic.** `Search` and
  `FilterByPlayerCount` already exist on `GameCollection`. Writing the LINQ inline here would move
  the graded evidence out of the graded classes.
- **Styling is not this ticket.** It is time-boxed and it comes last, because it is cheap for you
  and therefore tempting, and it scores nothing on the rubric.
