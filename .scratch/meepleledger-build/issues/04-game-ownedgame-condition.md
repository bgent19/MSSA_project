# 04 — `Game`, `OwnedGame` and `Condition`

**What to build:** The two most basic domain types and the enum that goes with them — a **title in
the abstract** and **my copy of a title** — so that everything downstream has something to refer to.

**Blocked by:** 03 — Tidy the scaffold into Domain, Storage and Data

**Status:** ready-for-brett

```csharp
class Game                    // a title, in the abstract
    Name, Designer, MinPlayers, MaxPlayers, PlaytimeMinutes

class OwnedGame               // my copy of a title
    Game Game
    DateAcquired, Condition, Notes

enum Condition { Mint, Good, Played, Worn }
```

*(Shape settled by the domain-model grilling session; reproduced because it encodes the decision more
precisely than prose.)*

- [ ] `Game` has exactly those five properties — no more
- [ ] `OwnedGame` holds a `Game` rather than duplicating its fields
- [ ] `Condition` is an enum, not a string
- [ ] `dotnet build` is green
- [ ] Commit

## Watch out for

- **Exactly five properties on `Game`.** The BGG API supplies plenty more — `BggId`, `Year`,
  `Description`, `ImageUrl`, `Categories` — and it is tempting to keep them "in case". Don't. An
  unused property on the graded domain class is dead weight a grader can see, and adding one later
  is a one-line change.
- **Why the split exists at all:** a title and a copy diverge fast. A title has a designer; a copy
  has a condition and a purchase date. This split is what makes a browsable catalog possible, and
  it is what lets you log a play of a game you don't own.
- **`Condition` is an enum so that bad values are unrepresentable.** It also renders as a dropdown
  for free and gives you a `switch` — which is branching evidence the rubric marks.
