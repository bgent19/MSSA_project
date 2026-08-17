# 06 — `Play`, `PlayerResult` and `PlayLog`

**What to build:** The history — one session at one table, the people who sat at it, and the log
those sessions are filed into. This is where the app's central idea lives: **a play is independent
of ownership.**

**Blocked by:** 04 — `Game`, `OwnedGame` and `Condition`

**Status:** ready-for-brett

```csharp
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

- [ ] `Play` references `Game` — **never `OwnedGame`**
- [ ] Only the game and the date are required; duration, location, scores and winner are all optional
- [ ] The `Play` constructor throws when there are more results than `Game.MaxPlayers`
- [ ] `PlayLog` stores plays in a **private** `List`
- [ ] `PlayLog.Record` throws unless `OwnerName` appears among the play's results
- [ ] `RecentFirst()` and `MostPlayed()` are methods here, not LINQ in a component
- [ ] `dotnet build` is green
- [ ] Commit

## Watch out for

- **The seat check is an upper bound only.** Do *not* check `MinPlayers`. Logging a solo play is
  always legal, and a lower-bound check would encode a rule the model deliberately does not have.
- **`IsWinner` is a flag, not a computation over scores.** Scores are optional, and plenty of games
  are won without numbers — co-op wins, or nobody counted. Multiple winners fall out for free.
  Accepted cost, already decided: the flag can disagree with the scores and nothing reconciles them.
  Do not add reconciliation.
- **`PlayLog.Record` checks that *you* were there. It does not consult the collection.** This is the
  whole point — a play of a game you don't own must record cleanly. If you find yourself reaching
  for `GameCollection` in this file, stop: the shelf and the history never speak to each other.
- **The owner rule lives on the log, not on the play.** It is a rule about *whose log this is*, so
  `Play` stays ignorant of which log it is filed in.
- **A play must survive removing its game from the collection.** That is why plays live in a
  `PlayLog` rather than hanging off `OwnedGame` — selling a game must not erase the evenings you
  spent with it.
- **`Play` is permissive on purpose.** Real play logging is lossy, and a model that demands full
  data does not get used.
