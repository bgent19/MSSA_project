# 05 — `GameCatalog` and `GameCollection`

**What to build:** The world and the shelf — the catalog of all known titles, and the collection of
the ones I own, with the rule that I cannot own the same title twice.

**Blocked by:** 04 — `Game`, `OwnedGame` and `Condition`

**Status:** ready-for-brett

```csharp
class GameCatalog
    IReadOnlyList<Game> Games
    Search(term) · ByPlayerCount(n)

class GameCollection
    private Dictionary<string, OwnedGame> _games      // keyed by title
    Add(OwnedGame)            → throws if already owned
    Remove(name) · Search(term) · FilterByPlayerCount(n) · TotalGames
```

This ticket contains the single best piece of rubric evidence in the app. `GameCollection.Add` is
the method that goes on the slide.

- [ ] `GameCatalog` exposes its games as a read-only view, never a mutable list
- [ ] `GameCollection` stores games in a **private** `Dictionary` keyed by title
- [ ] `GameCollection.Add` throws when the title is already owned — a guard clause, not just the
      dictionary's behaviour
- [ ] Search matches on title **and** designer
- [ ] Filtering by player count uses the game's min and max
- [ ] All the searching and filtering lives here as methods, not in a component
- [ ] `dotnet build` is green
- [ ] Commit

## Watch out for

- **Write the guard clause even though the `Dictionary` key already half-enforces it.** The
  dictionary prevents a duplicate silently; the guard clause makes it a *stated business rule* that
  throws with a message. The rubric marks the second one.
- **Name it `GameCollection`, never `Collection`.** `Collection` collides with `System.Collections`
  and with the generic tutorial sense of "a list". It also reads as a set with its siblings:
  GameCatalog / GameCollection / PlayLog.
- **Do not expose the private `_games` dictionary.** The encapsulation is the point — if a component
  can reach the dictionary, it can bypass the invariant, and the invariant is the graded artifact.
- **Behaviour lives here, not in components.** When you build the Collection screen later it should
  call `Search` and `FilterByPlayerCount`, not write its own LINQ inline.
- **Keying by title means O(1) duplicate rejection** — that is the stated reason for choosing a
  `Dictionary` over a `List`, and it is worth being able to say out loud.
