# PRD: MeepleLedger

**BLUF:** A Blazor Server web app that tracks the board games you own and logs the games
you play (kept deliberately independent, because you play plenty of games you don't own).

---

## 1. Problem & audience

I play board games at home, at cafés, and at conventions, and currently records none of it.
Existing trackers that exist (BGG) demand an account and record ownership as the primary fact, with plays
bolted to it. The single user of v1 is me, with multi-user functionality added in later versions.

## 2. What it does

Four screens, all reading one in-memory store that starts populated:

| Screen | Interactive | Job |
|---|---|---|
| **Collection** (landing) | yes | Browse the shelf; search by title/designer; filter by player count |
| **Log a play** | yes | Record a session: pick the game from the **catalog**, not the shelf |
| **Play Log** | yes | History, most recent first, with a per-row **"not owned"** badge |
| **Statistics** | static | most played, totals, played-but-not-owned |

## 3. The model

Seven classes, three collections: **GameCatalog** is the world, **GameCollection** is my shelf,
**PlayLog** is my history. Both point *into* the catalog; neither points at the other.

`Game` · `OwnedGame` · `GameCatalog` · `GameCollection` · `Play` · `PlayerResult` · `PlayLog`

Some edge cases I currently forsee:

1. **You can't own the same title twice**: `GameCollection.Add` (Dictionary keyed by title).
2. **A play can't seat more than the game allows**: `Play` ctor, upper bound only (playing solo is legal).
3. **Every play in your log includes you**: `PlayLog.Record`.

`Play` is highly variable: only game, date, and the user's presence are required. Scores,
winner, duration and location are optional, because real play logging is lossy and a model that
demands full data doesn't get used. `IsWinner` is a flag, not a computation over scores.

## 4. Potential for future work

- **Blazor Web App, `InteractiveServer`** add JavaScript, API layer, JSON boundary.
- **Authentication.**
- **Network at runtime.** Live API calls to BGG API so they stay in sync. I have
API access but for this project I am using a pre-loaded dataset I already have.
- **Azure Deployment.**
- **Curator Chatbot.**
- **Rules Explainer.**
- **Price Tracking.**
- **Manual Game Entry**

## 5. End state

- Clean build, and the six-step click path runs end to end: populated collection → search →
  player-count filter → log a play of an **unowned** game → it appears on top of the Play Log with
  a "not owned" badge → Statistics moves.
- Seed data present at first launch: **28 owned games (actual)**, a catalog of **~75–200 (actual)**, **~60–80 plays (synthetic)**
  across several months — volume enough that filtering looks like work rather than decoration.

