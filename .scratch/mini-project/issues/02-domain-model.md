# Model the domain: games, collections, and plays

Type: grilling
Status: closed
Assignee: claude (wayfinder session, 2026-08-10)
Blocked by: —

## Question

What are the types in this app, what does each one own, and what are they called?

This is the highest-value ticket on the map. The rubric grades classes, OOP design, and
data structure use — which means the domain model *is* the graded artifact. Everything
else on this map is in service of it.

Questions to settle, one at a time, via `/grilling` and `/domain-modeling`:

- What is a **Game**? A title in the abstract, or the specific copy Brett owns? These
  diverge fast — a game has a designer and a player count; a copy has a condition and a
  purchase price.
- What is a **Collection**? A first-class object, or just "the games this user owns"? Does
  it need behaviour — filtering, statistics — or is it a list?
- What is a **Play**? What must be recorded: date, duration, who played, scores, who won?
  Which of those are required and which optional?
- Who is a **Player**? Plays involve people who are not the app's user and never will be.
  Is a player an entity, or a name on a play record?
- Where does behaviour live? A `Play` that can compute its own winner is a better OOP
  showcase than a bag of properties operated on from a component.
- Which relationships are navigable in which direction, and what does that imply for
  the data structures (`List<T>` versus `Dictionary<TKey, TValue>`)?
- What invariants must always hold? (A play can't have zero players; a winner must be one
  of the players; a game can't be in a collection twice.) These are where encapsulation
  earns its keep — and they are the most demo-able evidence of OOP design.
- What is deliberately *not* modelled? Expansions, variants, house rules, ratings.

Output: named types with their fields, behaviours, and relationships, recorded in the
answer, plus any ubiquitous-language terms worth pinning via `/domain-modeling`.

## Resolution

**Seven types across three aggregates.** The load-bearing shape, in one line: *the catalog
is the world, the collection is my shelf, the log is my history* — and the collection and
the log never speak to each other.

```
GameCatalog     — all known titles      (the world)
GameCollection  — the ones I own        (my shelf)
PlayLog         — the ones I've played  (my history)
```

Both `GameCollection` and `PlayLog` point *into* the catalog; neither points at the other.
That independence is the single most consequential decision here — see "Plays are
independent of ownership" below.

### The types

```csharp
class Game                    // a title, in the abstract
    Name, Designer, MinPlayers, MaxPlayers, PlaytimeMinutes

class OwnedGame               // my copy of a title
    Game Game
    DateAcquired, Condition, Notes

class GameCatalog
    IReadOnlyList<Game> Games
    Search(term) · ByPlayerCount(n)

class GameCollection
    private Dictionary<string, OwnedGame> _games      // keyed by title
    Add(OwnedGame)            → throws if already owned
    Remove(name) · Search(term) · FilterByPlayerCount(n) · TotalGames

class Play                    // one session at one table
    Game Game                 // required
    DateTime PlayedOn         // required
    int? DurationMinutes      // optional
    string? Location          // optional
    List<PlayerResult> Results
    Winners  => Results.Where(r => r.IsWinner)
    HasWinner
                              → throws if Results.Count > Game.MaxPlayers

class PlayerResult
    string PlayerName
    int?  Score               // optional
    bool  IsWinner            // optional

class PlayLog
    string OwnerName          // whose log this is
    private List<Play> _plays
    Record(Play)              → throws unless OwnerName is among the Results
    ForGame(game) · RecentFirst() · MostPlayed()
```

`Condition` is an **enum**, not a string — `{ Mint, Good, Played, Worn }` — so bad values
are unrepresentable, it renders as a dropdown, and it yields a `switch` for free.

### The three invariants

Each lives on the class that owns the relevant state; nothing outside can violate one.

1. **You can't own the same title twice** — `GameCollection.Add`. The `Dictionary` key
   half-enforces it; the guard clause makes it a stated business rule.
2. **A play can't seat more than the game allows** — `Play` constructor. **Upper bound
   only**: `MinPlayers` is deliberately *not* checked, because logging just yourself is
   always legal (see below).
3. **Every play in your log includes you** — `PlayLog.Record`. The rule is about the *log*,
   not the play, so the log enforces it and `Play` stays ignorant of which log it's filed
   in.

### Decisions behind the shape

- **`Game` vs `OwnedGame` split.** A title and a copy diverge fast (a title has a designer;
  a copy has a condition). The split is what makes a browsable catalog possible.
- **`Play` is permissive, not strict.** Required: the game, the date, and the owner's
  presence. Optional: other players, scores, winner, duration, location. This came directly
  from Brett — real play logging is lossy, and a model that demands full data won't get
  used.
- **`Winner` is a flag, not a computation.** `PlayerResult.IsWinner` rather than
  `MaxBy(Score)`, because scores are optional and plenty of games are won without numbers
  (co-op, or nobody counted). Multiple winners fall out for free. Accepted cost: the flag
  can disagree with the scores; nothing reconciles them.
- **Plays are stored in a `PlayLog`, not on `OwnedGame`.** Deleting a game from the
  collection must never take its history with it, and the global recent-plays feed is the
  strongest demo beat.
- **`GameCollection`, not `Collection`.** `Collection` collides with `System.Collections`
  and with the generic tutorial sense of "a list". Reads as a set with its siblings:
  GameCatalog / GameCollection / PlayLog.
- **Behaviour lives on the domain classes,** not in components. Every filter, search, and
  stat above is a method on an aggregate. Components call them; they don't reimplement
  them in LINQ inline.

### Plays are independent of ownership

**You can log a play of a game you don't own.** Brett raised conventions, events, game
cafés, and friends' copies as *frequent* cases, not edge cases. Consequences, to be
preserved by every later ticket:

- `Play` references `Game`, never `OwnedGame`.
- `PlayLog.Record` checks the owner is present; it does **not** check the collection.
- A play survives removing the game from the collection.

Related: the log is one person's record precisely *because* most people at the table won't
be using the app. `OwnerName` is that person, and it becomes the account name when auth
arrives in map two — nothing else moves.

### Deliberately not modelled

Expansions and variants (a whole extra relationship, zero rubric value), house rules and
per-play notes, personal ratings (`PlayLog.MostPlayed()` already answers "what do I
actually like"). `Play.Location` was **kept in** — conventions matter to Brett, so where a
play happened carries real meaning.

### Rubric coverage

The six graded fundamentals land as: **classes/OOP** (seven types, three aggregates),
**encapsulation** (private `_games` / `_plays` behind read-only views), **data structures**
(`Dictionary` keyed by title for O(1) duplicate rejection, `List` for ordered history),
**methods** (all behaviour on the domain classes), **branching** (three guard clauses plus
the `Condition` switch), **loops/LINQ** (Search, Where, OrderBy, GroupBy in MostPlayed).

### Open gap handed downstream

With no ad-hoc game creation, **a convention title missing from the catalog cannot be
logged at all**. Whether that bites depends entirely on whether the catalog is ~20 mock
rows or a live API — recorded as a constraint on
[Choose the game data source and seeding strategy](05-choose-data-source.md), which must
now answer for it. Not resolved here; this ticket decides types, not sources.

No fog graduated: every question this answer raised belongs to a ticket that already
exists.
