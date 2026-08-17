# MeepleLedger build tickets

Twenty-three tickets broken out of [the spec](../../mini-project/SPEC.md). Numbered in dependency
order — blockers before the things they block.

**Status vocabulary:** `ready-for-brett` (baseline sprint work) and `if-ahead` (the pre-decided
add-back tier — build only if ahead of schedule; not building them is a non-event, not a cut).

These are **build** tickets. The twelve tickets in [`../../mini-project/issues/`](../../mini-project/issues/)
are a different artifact: closed *decision* tickets from the wayfinder map, kept for the arguments
behind every choice these tickets assume.

## Baseline

| # | Ticket | Blocked by |
|---|---|---|
| 01 | [Download the BGG rank dump by hand](01-download-bgg-rank-dump.md) | — |
| 02 | [Make the `GameCollection.Add` slide](02-make-the-code-beat-slide.md) | — |
| 03 | [Tidy the scaffold into Domain, Storage and Data](03-tidy-the-scaffold.md) | — |
| 04 | [`Game`, `OwnedGame` and `Condition`](04-game-ownedgame-condition.md) | 03 |
| 05 | [`GameCatalog` and `GameCollection`](05-gamecatalog-and-gamecollection.md) | 04 |
| 06 | [`Play`, `PlayerResult` and `PlayLog`](06-play-playerresult-playlog.md) | 04 |
| 07 | [Fetch the game data from BGG](07-fetch-and-save-raw-bgg-data.md) | 01 |
| 08 | [Parse and emit the catalog seed](08-parse-and-emit-the-catalog.md) | 04, 07 |
| 09 | [Emit the owned-collection seed](09-emit-the-owned-collection.md) | 08 |
| 10 | [Emit the play history](10-emit-the-play-history.md) | 06, 09 |
| 11 | [The storage seam](11-storage-seam.md) | 06, 09, 10 |
| 12 | [The shared catalog search picker](12-shared-catalog-picker.md) | 11 |
| 13 | [The Collection screen](13-collection-screen.md) | 11 |
| 14 | [Log a play](14-log-a-play.md) | 11, 12 |
| 15 | [The Play Log, with the "not owned" badge](15-play-log-with-not-owned-badge.md) | 14 |
| 16 | [Styling, hard time-box](16-styling-time-boxed.md) | 15 |
| 17 | [Rehearse and tag `demo-ready`](17-rehearse-and-tag-demo-ready.md) | 02, 16 |

## If-ahead

In the sprint plan's pre-decided add-back order. Deciding this order **in advance** is the entire
point — the decision has to already be made before the moment it is needed.

| # | Ticket | Blocked by | Est. |
|---|---|---|---|
| 18 | [Statistics screen](18-statistics-screen.md) | 15 | 15 min |
| 19 | [Play Log filtering](19-play-log-filtering.md) | 15 | 10 min |
| 20 | [Game detail + per-game win rate](20-game-detail-and-win-rate.md) | 15 | 40 min |
| 21 | [Three invariant tests](21-three-invariant-tests.md) | 06 | 30 min |
| 22 | [Edit and delete a play](22-edit-and-delete-a-play.md) | 15 | 30 min |
| 23 | [Add a game to the collection](23-add-a-game-to-the-collection.md) | 12, 13 | — |

Ticket 23 is **not** on the original add-back list — it was surfaced while slicing, because the
picker's stated dual purpose has only one consumer in the baseline. See the ticket for the reasoning.

## The three things to hold on to

1. **Domain model before seed emit.** The generated seed lands *inside* the web project, so emitting
   it before the domain types exist breaks the whole build. That is why 04 gates 08.
2. **Commit before every emit.** Not hygiene — the pre-decided response to a seed that won't compile
   is "revert and re-emit", and that response does not exist without the commit. Never a stash.
3. **After 15, the presentation is deliverable.** Ticket 15's checkpoint *is* step 5 of the six-step
   click path. Everything after it is polish.

## Two traps that cost the most time

- **A dead click is a missing `@rendermode InteractiveServer`.** The app is scaffolded `-int Server`,
  which is *per-page* interactivity. No error, no exception, no console output. Check this first,
  every time.
- **The app failing to start with a DI exception is a bad seed row**, not a broken framework. The
  store seeds in its constructor, so a row that violates an invariant throws during singleton
  construction. Fix the seeder's emit-time filter and re-emit.
