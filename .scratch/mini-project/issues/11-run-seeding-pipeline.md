# Run the seeding pipeline and commit the seed data

Type: task
Status: open
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

### Resolve by recording

The final catalog count, the owned count after expansion filtering, the play count, how long
the whole run actually took (ticket 08 needs this for the budget), where the generated files
live, and anything that surprised us — especially the 202 behaviour and any field the domain
model wanted that `/thing` does not supply.
