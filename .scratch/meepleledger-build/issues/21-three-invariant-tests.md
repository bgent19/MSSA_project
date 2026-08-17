# 21 — Three invariant tests

**What to build:** Three tests, one per domain invariant, that **prove** the through-line rather than
asserting it — the domain model really is what guards the app's data.

**Blocked by:** 06 — `Play`, `PlayerResult` and `PlayLog`

**Status:** if-ahead — **add-back item 4.** Not baseline sprint work.

Estimated ~30 minutes, which **includes standing up a test project from nothing** — there is no test
project and no test framework in this repository yet. That setup cost is why this sits at position 4
rather than position 1.

Three tests. Nothing else.

1. **You can't own the same title twice** — `GameCollection.Add` rejects a second `OwnedGame` whose
   title is already on the shelf.
2. **A play can't seat more than the game allows** — the `Play` constructor rejects more
   `PlayerResult`s than `Game.MaxPlayers`.
3. **Every play in your log includes you** — `PlayLog.Record` rejects a `Play` whose results don't
   contain the owner.

- [ ] A test project exists and runs
- [ ] Exactly those three tests, each asserting through the public method that enforces the rule
- [ ] Each test constructs the aggregate directly — no DI, no Blazor, no store
- [ ] All three pass
- [ ] Commit

## Watch out for

- **The seam is the domain aggregates, and it already exists.** `GameCatalog`, `GameCollection` and
  `PlayLog` are plain C# with no Blazor, no DI and no I/O — a test constructs them directly. Nothing
  needs extracting or refactoring to make this testable.
- **Do not write a test asserting a *lower* player bound.** The seat check is an upper bound only,
  because a solo play is legal. A test for a minimum would encode a rule the model deliberately does
  not have — and it would pass only if you broke the model.
- **Do not test the private `_games` dictionary or `_plays` list.** The point of the encapsulation is
  that nothing can see them. Test external behaviour only.
- **Do not test search and filter.** They are LINQ over an in-memory list; the failure mode is a
  compile error, not a wrong answer.
- **No component tests.** bUnit is not on this list and is not in scope.
- **Three tests is the scope.** The value here is proving the invariants, not coverage.
