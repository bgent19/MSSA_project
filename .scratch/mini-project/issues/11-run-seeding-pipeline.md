# Run the seeding pipeline and commit the seed data

Type: task
Status: claimed
Blocked by: 05, 12 (closed)
Blocks: 08

## Question

Nothing to decide — [Choose the game data source](05-choose-data-source.md) settled the
what. This is the manual work of actually doing it, and it must happen **before sprint hour
1**, because the app has no data until it does.

It earns a ticket for the same reason [Verify the toolchain](04-verify-toolchain.md) did:
**the pipeline has never been executed end to end.** Three steps in it are unproven, and
discovering any of them on sprint day would cost the sprint:

- The **CSV dump** requires being logged in with an approved application. Ticket 03 confirmed
  an unauthenticated client gets the HTML page rather than the file, but nobody has yet
  downloaded it successfully *with* the token.
- **`/collection` is the one endpoint with the 202-retry queue.** Its behaviour for
  `TheGentleBean` specifically is unobserved.
- The **LINQ-to-XML parse** was verified by ticket 03 against a single `/thing` response, not
  against a batch of 20 with `stats=1`, and not against the full field set the domain model
  needs.

### The pipeline (from ticket 05)

1. Download the CSV dump, filter out `is_expansion`, take top ids by rank.
2. `GET /collection?username=TheGentleBean&own=1&excludesubtype=boardgameexpansion` →
   ~40 owned ids. Expect a `202`, wait, ask again.
3. Union and dedupe both id sets. Expect heavy overlap, so pull enough ranked ids to reach
   **~200 unique** after the merge.
4. `GET /thing?id=<20 ids>&stats=1`, batched 20 at a time, 5 seconds between calls (~10
   calls).
5. Parse with LINQ-to-XML, emit the `static readonly List<Game>` C# class.
6. Generate ~60-80 plays over the past ~12 months, weighted so favourites recur, **including
   3-5 plays of catalog games that are not in the collection**. Emit as fixed C# data.
7. Commit the generated files. Hand-curate the ~40 owned rows by eye.

### Where the seeder lives (from [ticket 12](12-solution-structure.md))

`MeepleLedger.Seeder/`, a **sibling** of `MeepleLedger/` — never nested, or the web project's
globbing tries to compile it. Namespace `MeepleLedger.Seeder`. **Listed in `MSSA_project.slnx`**
(one line) so an examiner can open the parsing code.

**No project reference in either direction.** The seeder emits C# *source text* and never
constructs a `Game`, so it does not need the domain types; the emitted files compile inside the
web project. Step 5 writes to `MeepleLedger/Data/` — namespace `MeepleLedger.Data`.

### Division of labour

Per the coaching contract: Claude scaffolds the console project (ceremony). **Brett writes
the LINQ-to-XML parsing and the emit code** — those are the graded lines, and the whole
reason ticket 05 chose to commit the seeder rather than throw it away.

### Do not commit the token

It goes in the seeder's environment or .NET user-secrets. Since seeding is build-time only,
the token never needs to reach the web app's configuration at all. If it leaks, revoke and
regenerate at `https://boardgamegeek.com/applications`.

## Progress (wayfinder session, 2026-08-11)

Working asset: **[research/seeding-runbook.md](../research/seeding-runbook.md)** — the
operational checklist, the token handling, and the teaching for Brett's half. It does not
repeat the API facts; read it alongside
[board-game-data-sources.md](../research/board-game-data-sources.md).

**Done (ceremony, per the coaching contract):** `MeepleLedger.Seeder/` scaffolded as a
sibling of `MeepleLedger/`, `net10.0`, nullable on, **zero package references**; added to
`MSSA_project.slnx`. Whole solution builds, **0 warnings** (commit `f4c1e74`).

**Token handling decided: environment variable `BGG_TOKEN`, not user-secrets.** User-secrets
would cost the seeder two NuGet references to solve a problem it does not have — one person,
one machine, run by hand. Zero dependencies wins. User-secrets is the right lesson when map
two puts a connection string in the *web app's* config. Note `Authorization` is a restricted
header: it needs `DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(...)`,
not `.Add(...)`.

**Not started:** every step that touches the API, and all of Brett's parsing/emit code.

### Two findings the ticket did not anticipate

**1. The research doc's parse sketch does not match the domain model.** It was written
against a hypothetical `Game` before [ticket 02](02-domain-model.md) settled the real one.
The seed must emit exactly five properties and **discard** `BggId`, `Year`, `Description`,
`ImageUrl`, `Categories`. Conversely `Designer` — which the domain model wants and the
sketch never fetched — is a `<link type="boardgamedesigner">` and there are often **several
per game**. Recommendation: take the first only. Two edge cases that will otherwise throw
partway through a 200-game run: `(Uncredited)` is a real value, and some games have no
designer link at all, so `.First()` must be `.FirstOrDefault() ?? "Unknown"`.

**2. The emit step breaks the build — this is the one worth deciding before running.** The
seed lands in `MeepleLedger/Data/`, inside the web project, so it compiles with the web app.
But `Game`, `OwnedGame`, `Play`, `PlayerResult` and `Condition` are hour-one work and do not
exist. So the generated file cannot compile, and committing it leaves the repo **broken at
the start of hour 1** with the seed's correctness unverified until the clock is already
running.

The fix is to write the domain model *before* the emit step, so `dotnet build` proves the
seed compiles with no clock running. The catalog needs only `Game`; the collection needs
`OwnedGame` + `Condition`; the plays need `Play` + `PlayerResult` — which is most of the
model, so it is close to all-or-nothing. Preferred: **write the whole domain model now**,
leaving hour one UI-only. The honest counter-argument — that this makes the 5-hour figure a
less truthful measure of the sprint — is real, and belongs in
[the sprint plan](08-sprint-plan.md) as a recorded fact rather than a reason to take the
riskier path. **This is a live constraint on ticket 08 regardless of which way it goes.**

### Token: stored

Windows **User-scope environment variable `BGG_TOKEN`** (36-char UUID). Chosen over pasting
it inline so it never enters a transcript or a commit; readable from any newly-opened shell,
and via `[Environment]::GetEnvironmentVariable('BGG_TOKEN','User')`.

### All three unproven steps: verified live (2026-08-11)

This ticket existed because the pipeline had never been executed end to end and three steps
were unproven. **All three are now retired**, and two came back different from the map's
assumption. Detail and the corrected checklist are in the
[runbook](../research/seeding-runbook.md).

1. **The CSV dump is not reachable with the bearer token.** `GET /data_dumps/bg_ranks` with
   a valid token returns `200 text/html` — a generic Angular shell, no redirect, no `.csv`
   or `.zip` link in the body. The token authorizes the *XML API*, not site pages, which
   want a logged-in browser session. **Step 1 is a manual browser download**, and the seeder
   reads a local file. `/hot` works with the token but returns 50 *hot* (not ranked) items —
   a fallback for a demo-sized catalog, not a replacement.
2. **The 202 retry queue is a non-event.** `202`, then `200` on the very next request, ~2s
   total. Ticket 03 flagged it as the pipeline's one awkward endpoint; it is not. Write the
   loop anyway — it is correct and a cold queue may be slower — but budget nothing for it.
3. **`/thing` batched at 20 with `stats=1` works exactly as hoped.** `200`, 20/20 items,
   ~298 KB per batch (so ~1.5 MB for 200 games). Across **all 28** owned games, every one of
   the five domain fields is present, with **zero** missing values.

### Two facts that correct the map

- **The owned shelf is 28 games, not ~40.** `own=1` returns 28 **with and without**
  `excludesubtype=boardgameexpansion`, all `subtype=boardgame`. So ticket 05 was right that
  the count would land under 40 and wrong about why — there are no owned expansions to
  filter; ticket 09's "~40" was an over-estimate. **Immaterial to the plan**: the catalog
  target is ~200 either way and [the prototype](07-prototype-screens.md) wanted ~18 on the
  Collection screen. It does mean step 3 needs ~172 ranked ids rather than ~160, with less
  overlap than assumed.
- **Multiple designers hit 4 of 20 games — 20%, not a corner case.** That promotes the
  Designer decision above from a footnote to a real choice. Zero games had *no* designer and
  zero listed `(Uncredited)` in the owned sample, but keep the
  `FirstOrDefault() ?? "Unknown"` guard for the ranked ids, which are a wider sample.

**Data quality across the owned shelf is clean**: no `maxplayers = 0`, no `minplayers = 0`,
no `playingtime = 0`. The seat invariant is safe against this data. **Re-run that check over
the ranked ids** — a `maxplayers` of `0` there would make every play of that game throw.

### Still to do

Brett's half, per the coaching contract: the LINQ-to-XML parse and the emit code, plus the
domain model that must exist first (see the compile-order finding above). Nothing is blocked
on access any more.

### Resolve by recording

The final catalog count, the owned count after expansion filtering, the play count, how long
the whole run actually took (ticket 08 needs this for the budget), where the generated files
live, and anything that surprised us — especially the 202 behaviour and any field the domain
model wanted that `/thing` does not supply.
