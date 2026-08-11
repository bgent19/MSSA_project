# Seeding runbook

Working asset for [Run the seeding pipeline](../issues/11-run-seeding-pipeline.md). The
pipeline itself was decided by [Choose the game data source](../issues/05-choose-data-source.md);
this is the operational detail for actually running it, plus the teaching Brett needs before
writing his half.

Facts about the API (auth, endpoints, XML shape, rate limits) are **not repeated here** —
they live in [board-game-data-sources.md](board-game-data-sources.md). This document assumes
that one is open alongside it.

## Status

- **Scaffolding: done.** `MeepleLedger.Seeder/` exists as a sibling of `MeepleLedger/`,
  listed in `MSSA_project.slnx`, `net10.0`, nullable enabled, zero package references. The
  whole solution builds with 0 warnings.
- **Token: stored**, Windows User-scope env var `BGG_TOKEN` (36-char UUID).
- **All three unproven steps: verified live** (2026-08-11) — see below. Two came back
  different from what the map assumed.
- **Remaining: Brett's parse + emit code.** No seed data generated or committed yet.

## Verified live — what the API actually does

Smoke-tested against the real account, not assumed. The corrections matter more than the
confirmations.

| Step | Expected | Actual |
|---|---|---|
| `/thing?id=13` | `200` | **`200`** — token live |
| `/collection` 202 queue | "the one endpoint with a retry loop", treated as a hazard | **`202` then `200` on the very next request, ~2s total.** A non-event |
| `/thing?id=<20>&stats=1` | works, unverified at batch size | **`200`, 20/20 items, ~298 KB.** All five domain fields present on all 28 owned games; **zero** missing values |
| CSV dump | needs an authenticated session | **Not reachable with the bearer token** — see below |
| Owned shelf | ~40 games ([ticket 09](../issues/09-verify-bgg-token.md)) | **28** |

### The CSV dump needs a browser, not the token

`GET /data_dumps/bg_ranks` with a valid bearer token returns **`200` with
`Content-Type: text/html`** — a generic Angular shell, no redirect, and no `.csv`, `.zip` or
`amazonaws` link anywhere in the 10 KB body. The bearer token authorizes the **XML API**; it
does not authorize site pages, which want a logged-in browser session.

So pipeline step 1 is **not automatable** and should not be attempted in code. Download it
by hand, once, from a logged-in browser, and point the seeder at the local file. Ticket 11
already sanctioned exactly this — do not spend twenty minutes fighting it.

`/xmlapi2/hot?type=boardgame` *does* work with the token and returns 50 items, but it is
**hotness, not rank**, and 50 is far short of the ~172 ranked ids needed to reach a
200-game catalog. It is a fallback for a demo-sized catalog, not a replacement for the dump.

### The owned shelf is 28, not ~40 — and expansions are not why

`own=1` **with and without** `excludesubtype=boardgameexpansion` both return exactly **28**,
all of `subtype=boardgame`. So [ticket 05](../issues/05-choose-data-source.md)'s prediction
that "Brett's owned count may come in slightly under 40" was right about the direction and
wrong about the cause: there are no owned expansions to filter. Ticket 09's "~40" was simply
an over-estimate.

Immaterial to the plan — the catalog target is ~200 either way, and
[the prototype](../issues/07-prototype-screens.md) wanted ~18 games on the Collection screen,
so 28 clears it comfortably. Worth knowing so nobody hunts for 12 missing games.

### Data quality across all 28 owned games

`maxplayers = 0`: **none**. `minplayers = 0`: none. `playingtime = 0`: none. One game seats
more than 10. So the seat invariant on `Play` is safe against this data — no defensive
handling needed for the owned shelf. **Re-run this check against the ~172 ranked ids**, which
are a much wider and weirder sample; a `maxplayers` of `0` there would make every play of
that game throw.

**4 of 20 games in the first batch had more than one designer — 20%, not a corner case.**
Zero had no designer link and zero listed `(Uncredited)` in this sample, but keep the
`FirstOrDefault ?? "Unknown"` guard for the ranked ids.

## Token handling

**Environment variable, not user-secrets.** `BGG_TOKEN`, read with
`Environment.GetEnvironmentVariable("BGG_TOKEN")`.

User-secrets is the more idiomatic .NET answer and is worth learning, but it costs the
seeder two NuGet package references (`Microsoft.Extensions.Configuration.UserSecrets` and
`.Binder`) plus a `UserSecretsId`, to solve a problem this project does not have: the seeder
is run by hand, a handful of times, by one person, on one machine. Zero dependencies is worth
more here. User-secrets is the right tool when map two puts a connection string in the web
app's configuration — that is the moment to learn it.

Set it for the session before running:

```powershell
$env:BGG_TOKEN = "<the token>"
dotnet run --project MeepleLedger.Seeder
```

The token must never reach a commit, and never reaches the web app's configuration at all —
seeding is build-time only. If it leaks: revoke and regenerate at
`https://boardgamegeek.com/applications`.

Every request needs the header. `HttpClient` will not let you set `Authorization` via
`DefaultRequestHeaders.Add`, because it is a restricted header — use:

```csharp
http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", token);   // using System.Net.Http.Headers;
```

## The field gap — decide this before parsing

The parse sketch in the research doc was written against a **hypothetical** `Game`, before
[the domain model](../issues/02-domain-model.md) was settled. The two do not line up, and the
difference is not cosmetic. The real target is:

```csharp
class Game
    Name, Designer, MinPlayers, MaxPlayers, PlaytimeMinutes
```

Five properties. So the parse **discards** `BggId`, `Year`, `Description`, `ImageUrl` and
`Categories` — everything the research sketch collected beyond the five. Do not carry them
into the emitted seed "in case they are useful": an unused property on the graded domain
class is dead weight a grader can see, and adding one later is a one-line change.

Against that, one field the domain model wants that the research sketch never fetched:

**`Designer` is a `link`, not an element, and there are often several.**

```xml
<link type="boardgamedesigner" id="10" value="Gavan Brown"/>
<link type="boardgamedesigner" id="27" value="Matt Tolman"/>
```

`Game.Designer` is a single `string`. Three honest options, in preference order:

1. **First designer only.** One line, reads fine in a table cell, and the collection screen
   shows one name per row. Loses co-designers.
2. **Join with `", "`.** Truthful, but some games have five designers and the cell wraps to
   three lines, which will look bad on the Collection screen that
   [the prototype](../issues/07-prototype-screens.md) settled.
3. Change the domain model to `List<string> Designers` — **rejected**: the model is closed,
   nothing in the demo reads designers plurally, and reopening a settled ticket to gain a
   line-wrap problem is a bad trade.

Take option 1 unless the eyeball pass on the ~40 owned rows says otherwise.

Two edge cases that *will* appear in the top 200 and would otherwise throw:

- Some games list **`(Uncredited)`** as the designer. That is a real BGG value, not a null.
  It renders fine; leave it.
- A few have **no designer link at all**. `.First()` throws on those —
  use `.FirstOrDefault()?.Attribute("value")?.Value ?? "Unknown"`. This is exactly the kind
  of thing that surfaces at game 147 of 200, after the first nine API calls have already
  been spent.

## The compile-order problem — read before running step 5

The seeder emits C# source into `MeepleLedger/Data/`, **inside the web project**, so it is
compiled with the web app. But `Game`, `OwnedGame`, `Play`, `PlayerResult` and `Condition`
do not exist yet — they are hour-one work.

So the moment the generated file lands, **`MeepleLedger` stops building** until the domain
classes exist. Committing that state leaves the repo broken at the start of sprint hour 1,
which is the one hour that can least afford it, and it means the seed's correctness is
unverified until the sprint has already started. A generated 200-line file that does not
compile is a bad thing to discover with the clock running.

**The fix, and it is small:** write `Domain/Game.cs` — five properties and a constructor —
*before* running the emit step. Then `dotnet build` proves the seed compiles while there is
no clock. This is Brett's code, and it is the first thing hour one would have done anyway;
this ticket just pulls ~10 minutes of it forward, which is precisely what a
before-the-sprint task ticket is for.

`Condition` (the enum) and `OwnedGame` are needed too if the emitted seed builds
`OwnedGame` instances for the collection rather than bare `Game`s — decide which the seed
emits before writing either. The catalog needs only `Game`; the collection needs
`OwnedGame`; the play history needs `Play` and `PlayerResult`. Realistically that is most of
the domain model, so either:

- **(a)** write the whole domain model now, and hour one becomes UI-only, or
- **(b)** emit the catalog only now (needs just `Game`), and emit collection + plays at the
  top of hour one once the rest exists.

**(a) is the better trade** and it should go to [the sprint plan](../issues/08-sprint-plan.md)
as a finding either way: the domain model is the graded artifact and the thing Brett most
wants unhurried, and (b) leaves two of the three emit steps unproven until sprint day, which
is the exact risk this ticket exists to retire. The counter-argument is real and worth
stating: writing the domain model outside the sprint makes the 5-hour figure less honest as a
measure of the sprint. That is a matter for the plan to record, not a reason to take the
riskier path.

## The checklist

Ordered. Steps 1–4 are the API run; 5–7 are emit and commit.

- [x] **0.** ~~Token in the environment; confirm `200` not `401` before writing any loop.~~
      **Done** — `BGG_TOKEN` is a User-scope env var, `/thing` returns `200`.
- [ ] **1.** **Download the CSV dump by hand**, from a logged-in browser, at
      `https://boardgamegeek.com/data_dumps/bg_ranks`. **Do not try to fetch it in code** —
      verified above that the bearer token does not work on it. Save it outside the repo or
      add it to `.gitignore`; it is large and it is an input, not a deliverable.
      Filter out `is_expansion = 1`; take the top ids by `rank`.
- [x] **2.** ~~`/collection` 202 retry loop.~~ **Done and it is a non-event** — `202` then
      `200` on the next request, ~2s. Still write the retry loop (it is correct, and a cold
      queue may be slower), but do not budget time for it. **28 owned ids**, saved.
- [ ] **3.** Union step 1 and step 2 ids, dedupe. With only **28** owned, you need ~**172**
      ranked ids to reach 200 unique; overlap will be less than ticket 05 assumed, because
      the shelf is smaller. Check the count before spending API calls.
- [ ] **4.** `GET /thing?id=<20 ids>&stats=1`, **20 ids per call, 5 seconds between calls**,
      ~10 calls. **Batch-of-20 is verified working** (`200`, 20/20 items, ~298 KB) — at that
      size expect ~1.5 MB of XML for 200 games, so stream or buffer sensibly. Throttling
      shows up as `500`/`503`, not `429` — if you see one, you went too fast; wait and redo
      that batch. **Save every raw response to disk** as you go, so a parsing bug does not
      cost another ten API calls. Re-run the zero-player-count check over the ranked ids.
- [ ] **5.** Parse with LINQ-to-XML (see the research doc's three shape gotchas: `value=`
      attributes, `type="primary"` names, double-encoded description — though the description
      is now discarded, so gotcha 3 may not apply). Emit
      `MeepleLedger/Data/SeededGames.cs`, namespace `MeepleLedger.Data`, a
      `static readonly List<Game>`. Then **`dotnet build`** — see the compile-order section.
- [ ] **6.** Emit the play history: **~60–80 plays** over the past ~12 months, weighted so
      favourites recur, **including 3–5 plays of catalog games not in the collection**.
      Fixed data, not generated at startup. That unowned handful is load-bearing — it is the
      demo's spine and the "not owned" badge on the Play Log has nothing to show without it.
      Every play must include `TheGentleBean` in its `Results`, or `PlayLog.Record` throws on
      its own seed data.
- [ ] **7.** Eyeball the ~40 owned rows. Commit the generated files. Verify with
      `git status` that no token and no raw-response dump is in the commit.

## What to record on resolution

The ticket asks for these by name, so collect them as you go:

- Final catalog count, owned count after expansion filtering, play count.
- **How long the whole run actually took** — [the sprint plan](../issues/08-sprint-plan.md)
  needs this number and must not assume it was free.
- Where the generated files live.
- The `202` behaviour: how many retries, how long.
- Anything the domain model wanted that `/thing` does not supply.
