# 12 — The shared catalog search picker

**What to build:** A reusable component that lets you find a game by typing part of its title and
pick it — searching the **catalog**, not the shelf — so that choosing from hundreds of games is
typing rather than scrolling.

**Blocked by:** 11 — The storage seam

**Status:** ready-for-brett

Build it once here; ticket 14 consumes it.

**It searches the catalog.** That is what closes the unlisted-game gap **by width rather than by an
escape hatch** — a catalog wider than the shelf is what makes *plays are independent of ownership*
demonstrable at all. There is no manual game entry anywhere in this app, by decision.

- [ ] Typing part of a title narrows the list of games
- [ ] The list comes from the **catalog**, so unowned games are selectable
- [ ] Picking a game raises it to the parent component
- [ ] It is a reusable component, not logic inlined into one page
- [ ] Searching calls the catalog's own `Search` method rather than reimplementing LINQ inline
- [ ] The component declares `@rendermode InteractiveServer`

## Watch out for

- **`@rendermode InteractiveServer` is mandatory on anything with a button or an input.** The app
  was scaffolded with `-int Server`, which gives **per-page** interactivity, not global. Without the
  directive the box does nothing — **no error, no exception, no console output.** This is the first
  thing to check on *any* dead click, before anything else.
- **This is the first real component**, so price in the learning curve. It is also the moment the
  Blazor Server bet gets tested in practice, with sessions still in hand if something is wrong.
- **Do not reimplement search here.** `GameCatalog.Search` already exists; the component calls it.
  Behaviour lives on the domain classes — that is where the rubric looks.
