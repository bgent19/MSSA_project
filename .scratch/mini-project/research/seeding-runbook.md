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
- **Pipeline: not yet run.** Blocked on the bearer token, which per
  [ticket 09](../issues/09-verify-bgg-token.md) exists but is not stored anywhere.

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

- [ ] **0.** Export `BGG_TOKEN` in the shell (above). Confirm with one `/thing?id=13` call
      that you get `200`, not `401`, *before* writing any loop.
- [ ] **1.** Download the CSV dump from `https://boardgamegeek.com/data_dumps/bg_ranks`.
      **Unproven step** — it needs an authenticated session, and it may be a browser download
      rather than an `HttpClient` one. If it is, just download it by hand and commit it, or
      point the seeder at the local file. Do not spend twenty minutes automating a
      once-ever download.
      Filter out `is_expansion = 1`; take the top ids by `rank`.
- [ ] **2.** `GET /collection?username=TheGentleBean&own=1&excludesubtype=boardgameexpansion`.
      **Expect `202` with a queued-message body, not an error** — sleep ~5s and re-request
      until `200`. This is the only endpoint that does this. Record how many retries it
      actually took.
- [ ] **3.** Union step 1 and step 2 ids, dedupe. Expect heavy overlap, so pull enough ranked
      ids that the merge lands at **~200 unique**. Check the count before spending API calls.
- [ ] **4.** `GET /thing?id=<20 ids>&stats=1`, **20 ids per call, 5 seconds between calls**,
      ~10 calls. Throttling shows up as `500`/`503`, not `429` — if you see one, you went too
      fast; wait and redo that batch. **Save every raw response to disk** as you go, so a
      parsing bug does not cost another ten API calls.
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
