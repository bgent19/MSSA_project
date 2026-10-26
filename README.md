# MeepleLedger

A Blazor Server web app for tracking a board game collection and the plays logged against it. The
game data comes from real [BoardGameGeek](https://boardgamegeek.com) (BGG) data.

The focus of the mini project is the class design in [MeepleLedger/Domain](MeepleLedger/Domain) and
the `MeepleLedger.Seeder` project that produces the app's data.

You need the .NET 10 SDK to build and run this.

## What's in the repo

| Path | What it is |
|---|---|
| [MeepleLedger/](MeepleLedger/) | The web app. `Domain/` has the classes, `Storage/` connects them to the app, `Data/` has the game data, `Components/` has the pages. |
| [MeepleLedger.Tests/](MeepleLedger.Tests/) | xUnit tests for the domain rules. |
| [MeepleLedger.Seeder/](MeepleLedger.Seeder/) | A separate console app that creates the files in `MeepleLedger/Data/`. |
| [MeepleLedger.Worker/](MeepleLedger.Worker/) | A console app that takes embed requests off Service Bus and writes new vectors to Cosmos DB (A-19). |
| [MeepleLedger.Functions/](MeepleLedger.Functions/) | The same embed worker as Azure Functions (isolated worker): a Service Bus trigger, plus an HTTP API that queues a re-embed (A-21). |
| [MeepleLedger.Events/](MeepleLedger.Events/) | A console app with the Event Grid subscribers for `PlayLogged`, a deliberately failing webhook, and a dead-letter reader (A-20). |
| [presentation/](presentation/) | HTML slides for the project presentation. |
| [infra/](infra/) | Scripts and policy files that create the Azure resources for the AI features. |
| `raw/`, `data/` | Working files for the seeder. Git ignores both, so they are empty after a fresh clone. |

`MSSA_project.slnx` ties the six projects together.

## Running the app

```
dotnet run --project MeepleLedger
```

Then open the URL it prints. The app gets its games from the three files in `MeepleLedger/Data/`.
Those files are committed to the repo, so the app runs right after a clone. You only need the seeder
below if you want to regenerate them.

### What you can do

| Page | Route | What it does |
|---|---|---|
| Collection | `/` | Lists the games you own. Search by title or designer, filter by player count. |
| Add a Game | `/collection/add` | Pick a game from the catalog and add it with its condition and date acquired. A game you already own is refused. |
| Game detail | `/games/{name}` | One game's details, its plays, and your win rate for it. |
| Log a Play | `/log` | Record a session: pick any catalog game (owned or not), then the date, players, scores, winners, duration and location. |
| Edit a Play | `/plays/{id}/edit` | The same form, filled in with an existing play. |
| Play Log | `/plays` | Every play, newest first. Filter by game or to games you don't own. Edit or delete a play. |
| Statistics | `/stats` | Games owned and plays logged, most played games, and games played but not owned. |
| Tonight | `/tonight` | Type an Ask ("something co-operative with cards"), set players, minutes and owned-only, and get ranked Recommendations with the reason for each. Offline it matches words, not meaning, and says so. With the Cosmos DB or PostgreSQL Provider it matches by meaning against each game's Blurb. |

**Changes are not saved.** The app keeps everything in memory and starts over from the seed data
each time it launches, so anything you add, edit or delete is gone when the app stops.

## How it's built

### Domain

The classes in [MeepleLedger/Domain](MeepleLedger/Domain) hold the rules. The pages call them
rather than enforcing anything themselves.

- `Game` — a catalog entry: name, designer, player count range, play time.
- `GameCatalog` — every game the app knows about (about 200), with search and player-count lookup.
- `OwnedGame` / `GameCollection` — the games you own, keyed by title. **You can't own the same
  title twice.**
- `Play` / `PlayerResult` — one session, and each player's score and whether they won. **A play
  can't have more results than the game's max players.**
- `PlayLog` — every play, belonging to one owner. **Every play must include the owner as a
  player**, both when recorded and when edited. It also answers most played, games played, and
  per-game win records.
- `WinRecord` — wins over plays for one game. Its rate is empty, not zero, for a game never played.

The log and the collection are separate: you can log a game you don't own, and deleting a play never
touches the collection.

### Storage

[MeepleLedger/Storage](MeepleLedger/Storage) sits between the domain and the pages. Pages only see
two interfaces, registered as singletons in `Program.cs`:

- `IGameCatalogSource` — implemented by `SeededCatalogSource`, which reads `CatalogSeed`.
- `IMeepleStore` — implemented by `InMemoryMeepleStore`, which builds the collection and play log
  from `CollectionSeed` and `LogSeed`.

The store is created at startup, so bad seed data fails the launch instead of the first page load.
Adding real persistence means writing a new `IMeepleStore`; the pages don't change.

## Running the tests

```
dotnet test
```

The tests in [MeepleLedger.Tests](MeepleLedger.Tests/) check the domain rules above: a duplicate
title is refused, a play can't exceed max players, a play without the owner can't be recorded or
edited in, an edit replaces the right play, and removing a play leaves the collection alone.

## Running the seeder

The seeder runs twice, and each run does a different job:

1. **Fetch** — download data from BGG and save it as XML files.
2. **Emit** — read those XML files and write them out as C# code.

They are split because fetching hits the BGG API and is slow. Once the XML is saved, you can rerun
the emit step as many times as you need without downloading anything again.

### Step 1: fetch

```
dotnet run --project MeepleLedger.Seeder
```

Before running this, you need:

- An environment variable named `BGG_USERNAME`, set to the BGG user whose collection you want.
- An environment variable named `BGG_TOKEN`, set to an API token from BGG.
- A file at `data/boardgames_ranks.csv`. **This file is not in the repo.** Download it in a browser
  while logged in to BGG, from `https://boardgamegeek.com/data_dumps/bg_ranks`, and save it into the
  `data/` folder. The seeder cannot download this for you, because the API token only works for the
  BGG API and not for pages on the website.

What this step does:

- Calls the BGG API for the games the user owns, and saves the response to `raw/collection-owned.xml`.
- Reads `data/boardgames_ranks.csv` and picks the highest ranked games, enough to bring the total
  catalog up to 200 games including the ones the user already owns.
- Calls the BGG API again for details on all 200 games, 20 at a time, and saves each response as
  `raw/thing-batch-NN.xml`.

The `raw/` folder is ignored by git. That is why step 2 needs step 1 to have been run on this
machine — the XML files never come down with a clone.

### Step 2: emit

```
dotnet run --project MeepleLedger.Seeder -- emit
```

This step needs step 1 to have finished, so that the XML files are sitting in `raw/`.

It reads that XML and writes three C# files into [MeepleLedger/Data/](MeepleLedger/Data/):

- `CatalogSeed.cs` — every game in the catalog.
- `CollectionSeed.cs` — the games the user owns.
- `LogSeed.cs` — the play history (This data is synthetic because I am bad at logging my plays in real life).

**Commit your work before running an emit.** These three files are real source code in the web
project, and the emit overwrites them. If a generated file doesn't compile, the fix is to throw it
away with git and emit again — which only works if everything else was already committed.

### Step 3: embed

```
dotnet run --project MeepleLedger.Seeder -- embed
```

This step needs step 2 to have written `data/blurbs.json`, the `raw/` XML from step 1, and the
`AzureOpenAI:Endpoint`, `AzureOpenAI:Key` and `AzureOpenAI:EmbeddingDeployment` user secrets.

It turns each game's name, categories, mechanics and Blurb into a vector, 20 games per request, and
writes them to `data/vectors.json` (about 6 MB, ignored by git). Running it again only embeds games
whose text has changed, so it is cheap to re-run. At the end it prints the tokens used and a cost
estimate — the whole catalog should cost well under one cent.

### Step 4: upload to Cosmos DB

```
dotnet run --project MeepleLedger.Seeder -- upload-cosmos
```

This step needs `data/vectors.json` from step 3, the `raw/` XML from step 1, and the Cosmos DB
container and user secrets from [Cosmos DB for NoSQL](#cosmos-db-for-nosql) below.

It upserts every game into the `games` container as one document: the same facts as
`CatalogSeed.cs`, plus the Blurb, its vector at `/embedding`, and `embeddingTextHash` (the hash of
the text the vector was made from, used by the [change feed](#watching-the-change-feed)). The
document id is the game's name.
Anything else in the container (like the hand-written test document) is deleted, so the container
always matches `data/vectors.json`. Running it again is safe. It prints the request charge (RU) of
the upload; the whole catalog is about 6,000 RU, a fraction of a cent on serverless.

## Azure resources

The app runs without any of these. They are only needed by the cloud Providers for the AI features.

### Azure OpenAI (embeddings)

```
pwsh infra/openai/provision-openai.ps1
```

Run it from the repo root after `az login`. It creates the `rg-meepleledger` resource group (if it
doesn't exist yet), an Azure OpenAI account called `meepleledger-openai`, and a deployment called
`game-embeddings` that runs `text-embedding-3-small` (1536 dimensions, 20K tokens per minute). It
then saves `AzureOpenAI:Endpoint`, `AzureOpenAI:Key` and `AzureOpenAI:EmbeddingDeployment` into the
Seeder's user secrets. Check it with `dotnet run --project MeepleLedger.Seeder -- smoke-embed`. A
brand-new deployment can return `DeploymentNotFound` for a few minutes.

### Cosmos DB for NoSQL

```
pwsh infra/cosmos/provision-cosmos.ps1
```

Run it from the repo root after `az login`. It creates the `rg-meepleledger` resource group (if it
doesn't exist yet), a **serverless** Cosmos DB account called `meepleledger-cosmos` with NoSQL vector
search turned on, and a `meepleledger` database with a `games` container and a `leases` container
(for the [change feed processor](#watching-the-change-feed)). It then saves `Cosmos:Endpoint`,
`Cosmos:Key`, `Cosmos:Database`, `Cosmos:Container` and `Cosmos:LeaseContainer` into the Seeder's
user secrets. Running it a second time skips anything that already exists.

The container is created with two policy files from [infra/cosmos/](infra/cosmos/):

- [games-vector-policy.json](infra/cosmos/games-vector-policy.json): vectors live at `/embedding`,
  are `float32`, use `cosine` distance and have 1536 dimensions (what `text-embedding-3-small`
  returns). **This can't be changed once the container exists.** To change it you have to delete the
  container and create it again.
- [games-indexing-policy.json](infra/cosmos/games-indexing-policy.json): a `quantizedFlat` vector
  index on `/embedding`, and a range index on only the three paths the Table filters on
  (`/minPlayers`, `/maxPlayers`, `/playtimeMinutes`). Everything else is excluded (`/*`). `id` is
  always indexed, so the owned-only filter still works. Indexing every path (the Cosmos default)
  made the Tonight query no cheaper and made each new document about 16% dearer to write (A-09).
  A query that filters on any other path, such as `c.name`, now scans every document.

Some of the choices, explained:

- **Serverless** because the project is small and mostly idle, and serverless only bills for what you
  use. It has a storage limit and no SLA, so it would be the wrong pick for a production workload.
- **`quantizedFlat` rather than `diskANN`.** `flat` can't go above 505 dimensions, so it isn't an
  option. Microsoft recommends `quantizedFlat` for 50,000 vectors or fewer. With fewer than 1,000
  vectors, Cosmos does a full scan for both types anyway.
- **Partition key `/id`.** Writes are upserts of one Game at a time, and every Ask searches the whole
  Catalog, so no partition key could narrow a search. `/id` gives each Game its own partition, so
  no partition gets more traffic than the others. With per-user data this would be a bad choice,
  because a query by owner would have to check every partition.

To check the setup by hand, paste [sample-game.json](infra/cosmos/sample-game.json) into
**Data Explorer → games → Items → New Item**. Save it, then read it back with
`SELECT * FROM c WHERE c.id = "Hand Written Test Game"`, and delete it after.

#### Using Cosmos DB from the app

Load the games with [step 4](#step-4-upload-to-cosmos-db), then start the app with the Cosmos
Search Index:

```
dotnet run --project MeepleLedger -- --SearchIndex:Provider=Cosmos
```

`SearchIndex:Provider` is `InMemory` in `appsettings.json`, so a plain `dotnet run` never needs
Azure. The web project shares the Seeder's user secrets ID, so the `Cosmos:*` and `AzureOpenAI:*`
secrets set by the provisioning scripts are read by the app too. A missing secret stops the app at
startup and names the setting.

With the Cosmos Provider, the Tonight screen embeds the Ask with the same `game-embeddings`
deployment and runs one query:

```sql
SELECT TOP @maxResults c.id, VectorDistance(c.embedding, @askVector) AS score
FROM c
WHERE (c.minPlayers <= 0 OR c.minPlayers <= @playerCount)
  AND (c.maxPlayers <= 0 OR c.maxPlayers >= @playerCount)
  AND (c.playtimeMinutes <= 0 OR c.playtimeMinutes <= @minutesAvailable)
  AND ARRAY_CONTAINS(@ownedNames, c.id)   -- only when "Only games on my shelf" is ticked
ORDER BY VectorDistance(c.embedding, @askVector)
```

The Table is in the `WHERE`, so Cosmos only ranks games that fit, and only the id and score come
back — never the 1536-number vector. The request charge of every search is written to the log
(about 5 to 12 RU per Ask).

To measure the request charges again (after changing the indexing policy or the consistency level):

```
dotnet run --project MeepleLedger.Seeder -- measure-ru
```

It prints the charge of the Tonight query and a few variations of it, a point read and the query at
each consistency level, a single-partition against a cross-partition query, and the cost of a write.
It refuses to measure while an indexing policy change is still being applied. It writes one test
document and deletes it again. The A-09 ticket records the numbers and what they mean.

This is the first Provider that needs NuGet packages in the web project: `Microsoft.Azure.Cosmos`,
plus `Newtonsoft.Json`, which the Cosmos SDK loads at runtime but leaves for you to reference.

#### Watching the change feed

```
dotnet run --project MeepleLedger.Seeder -- watch-cosmos [instance name]
```

This runs a change feed processor on the `games` container until you press Enter. When a Game is
added, or its Blurb, categories or mechanics are edited, it records that the Game needs a new
vector. It does **not** call the embeddings API: it publishes an embed request to the Service Bus
queue `embed-requests`, where `MeepleLedger.Worker` does the embedding (see
[the embed worker](#the-embed-worker-a-19)). Without the `ServiceBus:ConnectionString` secret it
writes a log line and adds the request to `data/embed-requests.jsonl` (ignored by git) instead.

- **How it tells an edit from a vector write.** `upload-cosmos` stores `embeddingTextHash` on every
  document: the hash of the text its vector was made from. The handler hashes the document's current
  text and only queues a request when the two differ. So re-running `upload-cosmos` while the
  processor is running shows 200 changes and queues nothing. Run `upload-cosmos` once after pulling
  this change, or every Game looks new.
- **Leases.** The processor is named `embed-requests` and saves how far it has read in the `leases`
  container (created by `provision-cosmos.ps1`). Stop it and start it again and it carries on from
  where it stopped. The first run starts from "now", not from the beginning, so it never queues the
  whole Catalog.
- **Instance name.** The instance name defaults to the machine name. Two processes with the same
  processor name share the work. A process with a different processor name would get every change
  again.
- **Deletes are invisible.** The change feed only carries creates and updates. Deleting a Game in
  Data Explorer does not reach the handler at all. To react to a delete you would mark the document
  as deleted (an update) instead of deleting it.

To try it, start the processor, then in **Data Explorer → games → Items** open a Game, change a word
in its `blurb` and save. Within a few seconds the processor prints the change and queues a request.

### Azure Database for PostgreSQL (pgvector)

```
pwsh infra/postgres/provision-postgres.ps1
dotnet run --project MeepleLedger.Seeder -- setup-postgres
```

The script creates a PostgreSQL 17 Flexible Server called `meepleledger-pg` in `rg-meepleledger`,
with a `meepleledger` database and a firewall rule for your current public IP. It then adds `VECTOR`
to the `azure.extensions` server parameter and saves `Postgres:ConnectionString` into the Seeder's
user secrets. Run the script again from a new network to update the firewall rule.

- **Sized on purpose.** Burstable `Standard_B1ms` (1 vCore, 2 GiB) with the minimum 32 GiB of
  storage and auto-grow off. 200 vectors of 1536 dimensions plus an HNSW index come to a few MB, so
  the smallest SKU is plenty. Storage can grow later but never shrink.
- **centralus, not eastus2.** The subscription this was built on cannot create Flexible Servers in
  eastus2, so the server lives in centralus while its resource group stays in eastus2.
- **Allow-listing is not installing.** The script only allow-lists the extension. `setup-postgres`
  connects with [Npgsql](https://www.npgsql.org/), runs `CREATE EXTENSION IF NOT EXISTS vector` in
  the `meepleledger` database, and prints `extversion` and the memory settings an HNSW build depends
  on (`maintenance_work_mem` and others).
- **It bills while it runs.** Unlike serverless Cosmos DB, a running server costs money every hour
  whether or not you use it. Stop it when you are done:
  `az postgres flexible-server stop --name meepleledger-pg --resource-group rg-meepleledger`.
  Azure starts a stopped server again by itself after seven days.

#### The games table and its vector index (A-12)

```
dotnet run --project MeepleLedger.Seeder -- upload-postgres
dotnet run --project MeepleLedger.Seeder -- measure-pgvector
```

`upload-postgres` applies [infra/postgres/games-schema.sql](infra/postgres/games-schema.sql) and
loads one row per Game from the raw XML, `data/blurbs.json` and `data/vectors.json` (the same Games
and vectors as `upload-cosmos`). Rows are upserted by name and strays are deleted, so it is safe to
run again. The schema file explains each column type: categories and mechanics are `text[]`, weight
is `real` (null = unrated), the Blurb is `text`, and the embedding is `vector(1536)`. B-tree indexes
cover the Table's filters (player count, playtime).

`measure-pgvector` runs the same nearest-neighbour searches with no vector index (the exact answer),
with IVFFlat and with HNSW, and prints build time, index size, server execution time and recall
against the exact answer as a Markdown table. It leaves the HNSW index in place.

- **The operator class must match the operator.** The index is `vector_cosine_ops`, so queries must
  order by `<=>` (cosine distance). A query using `<->` (L2) still works but silently scans the table.
- **IVFFlat learns from the rows present at build time**, so it is only ever built after loading.
  HNSW has no such step, which is why the schema file can create it on an empty table.
- **At 200 rows the planner prefers a sequential scan** to any index. The measurement turns
  `enable_seqscan` off for its own session and checks every plan, so an index row really is an
  index scan. Build time and index size show the difference between the two; latency barely can.

#### Using PostgreSQL from the app (A-13)

Load the games with `upload-postgres`, then start the app with the PostgreSQL Search Index:

```
dotnet run --project MeepleLedger -- --SearchIndex:Provider=Postgres
```

It needs the `Postgres:ConnectionString` and `AzureOpenAI:*` user secrets. The Tonight screen
embeds the Ask with the same `game-embeddings` deployment and runs one query, with the vector
passed as a parameter (the Pgvector type mapping), never pasted into the SQL:

```sql
SELECT g.name, g.blurb, g.embedding <=> @askVector AS distance
FROM games g
JOIN unnest(@ownedNames) AS owned(name) ON owned.name = g.name   -- only when "Only games on my shelf" is ticked
WHERE (g.min_players <= 0 OR g.min_players <= @playerCount)
  AND (g.max_players <= 0 OR g.max_players >= @playerCount)
  AND (g.playtime_minutes <= 0 OR g.playtime_minutes <= @minutesAvailable)
ORDER BY g.embedding <=> @askVector
LIMIT @maxResults
```

Similarity and the Table are the same query: the Table is the `WHERE`, and owned-only is a join
against the Collection (sent as an array, because the Collection lives in the app). Each
Recommendation's Reason quotes the sentence of the Blurb that shares the most words with the Ask,
or the Blurb's opening sentence when none do.

- **The recall trap.** HNSW walks the graph first and applies the `WHERE` afterwards, so a
  selective Table can leave fewer than ten rows even though more Games fit. The app raises
  `hnsw.ef_search` to `Postgres:EfSearch` (100, in `appsettings.json`) for the query, and when an
  answer still comes back short it runs the query again as an exact scan (`enable_indexscan` off)
  and logs a warning if that found more. Both settings are `SET LOCAL`, so they end with the query.
- **Look at the plan.** `explain-postgres` runs `EXPLAIN (ANALYZE, BUFFERS)` on the same query for
  three Tables (wide, selective, owned-only), each four ways: the planner's choice, HNSW forced at
  `ef_search` 40 and 100, and exact. Forcing HNSW takes `enable_bitmapscan` off as well as
  `enable_seqscan`: with a `WHERE`, the planner otherwise goes round the vector index with a bitmap
  scan on the B-tree indexes. At 200 rows the planner's own choice is always an exact plan (seq or
  bitmap scan plus a top-N sort, about 1 ms), which is the right call at this size. Forced, HNSW
  returns 0 of the 2 Games that fit the selective Table (1 player, 30 minutes) at `ef_search` 40
  and still 0 at 100, which is why the app falls back to an exact scan rather than relying on
  `ef_search` alone. It prints how many Games fit each Table and how many rows each
  run returned, so a forced-HNSW run that returns fewer than the exact one is the recall trap seen
  directly. The query text is a copy of `PostgresGameSearchIndex`'s; keep the two in step.

```
dotnet run --project MeepleLedger.Seeder -- explain-postgres
```

#### Connection pooling (A-14)

Each PostgreSQL connection is a separate server **process** with its own memory, not a thread, so
connections are expensive to open (TCP, TLS, authentication, a fork on the server) and few in number.
A B1ms server allows 50, and Azure keeps 15 of those for itself. So the app never opens one per Ask:

- **One `NpgsqlDataSource` for the whole app**, registered as a singleton in `Program.cs`. It owns the
  pool and is thread-safe. A data source built per request would start a new, empty pool every time:
  modern-looking code with no pooling at all.
- **The pool size is set, not defaulted.** `Postgres:MinPoolSize` (2) keeps a couple of connections
  warm; `Postgres:MaxPoolSize` (20) caps the app well below the 35 user connections, leaving room for
  the Seeder, `psql` and a second app instance. Asks beyond 20 at once wait for a free connection.
- **Every command has a timeout.** `Postgres:CommandTimeoutSeconds` (5) is added to the connection
  string; the Tonight query takes about a millisecond on the server, so 5 seconds means something is
  wrong, and the Ask fails instead of holding a connection forever.
- **The repeated commands are prepared** (`Postgres:PrepareStatements`, on by default): parsed and
  planned once per pooled connection rather than once per Ask. The exact-scan fallback is not
  prepared, because a prepared statement can keep a plan made before `enable_indexscan` was turned
  off. Postgres does not plan it again when a planner setting changes.

These settings live in `appsettings.json` and are added on top of the secret connection string.
`load-postgres` measures them: 50 Asks at once, 5 rounds, three ways. It reads the same
`appsettings.json`, and uses Games' own vectors as the Asks, so it makes no embeddings calls.

```
dotnet run --project MeepleLedger.Seeder -- load-postgres
```

Measured from a home connection to the `centralus` server (per-Ask times include waiting for a
connection and the network; "later" is rounds 2 to 5):

| Run | Round 1 p50 | Later p50 | Later p95 | Asks/s | Failed | Connections left open |
|---|---|---|---|---|---|---|
| No pooling | 2130 ms | 1952 ms | 2406 ms | 20 | 0 of 250 | 0 |
| Pooled (min 2, max 20) | 1180 ms | 262 ms | 410 ms | 117 | 0 of 250 | 20 |
| Pooled + prepared | 1188 ms | 260 ms | 408 ms | 116 | 0 of 250 | 20 |

- **Pooling is the big win:** about 7x lower latency and 6x the throughput, because the connection
  handshake is paid once instead of on every Ask. Round 1 is slow for both pooled runs because it
  opens the connections.
- **Prepared statements changed nothing measurable here.** At 200 rows, parsing and planning take
  microseconds and the round trip to Azure dominates. They are kept on because they cost nothing,
  and they would matter for a heavier query or a closer client.
- **No PgBouncer.** Azure's built-in PgBouncer helps when many *clients* each hold a pool and
  together pass `max_connections`. One app with a pool of 20 does not. It also isn't available
  on the Burstable tier. Revisit this when the app runs as several replicas (A-26/A-27):
  6 replicas x 20 would pass 35. If PgBouncer goes in front, in transaction mode an Ask can land on a
  different server connection from the one its statement was prepared on, so set
  `Postgres:PrepareStatements` to `false` first.

### Azure Managed Redis (A-15)

```
pwsh infra/redis/provision-redis.ps1
dotnet run --project MeepleLedger.Seeder -- ping-redis
```

The script installs the `redisenterprise` az CLI extension, then creates an Azure Managed Redis
instance called `meepleledger-redis` in `rg-meepleledger` with the RediSearch module, and saves `Redis:ConnectionString` (StackExchange.Redis format) into the Seeder's user
secrets. `ping-redis` connects over TLS with [StackExchange.Redis](https://stackexchange.github.io/StackExchange.Redis/),
checks `PING` returns `PONG`, and runs `MODULE LIST` to confirm the search module is loaded.

| Setting | Value | Why |
|---|---|---|
| Product | Azure **Managed** Redis (`az redisenterprise`) | Not Azure Cache for Redis (`az redis`). Different product, different tiers |
| Region | centralus | eastus2 has no Balanced_B0 capacity for this subscription; the resource group stays in eastus2 |
| SKU | `Balanced_B0`, 0.5 GB | Smallest SKU. Balanced includes RediSearch; Flash Optimized does not |
| Clustering policy | `EnterpriseCluster` | The only policy RediSearch works with |
| Eviction policy | `NoEviction` | Required by RediSearch |
| Client protocol | `Encrypted`, TLS 1.2+, port 10000 | TLS only: there is no plaintext port |
| High availability | Disabled | Dev/test only, about half the price. Can be turned on later, never off |
| Auth | Access keys enabled | Managed Redis defaults to Entra ID only; a key keeps it a plain secret |

- **Modules, clustering policy and eviction policy are fixed at creation.** Getting one wrong means
  deleting and recreating the instance, so the script reads them back and warns if they differ.
- **It is clustered, even though it looks like it isn't.** The Enterprise policy puts a proxy in front
  of the shards, so the client sees one standalone server (`ping-redis` prints `Standalone`). Multi-key
  commands other than `DEL`, `MSET`, `MGET`, `EXISTS`, `UNLINK` and `TOUCH` can still fail with
  `CROSSSLOT`, which A-16 and A-17 have to key around.
- **It bills every hour it exists.** There is no serverless option and no stop. If you are pausing
  for more than a day, delete it and run the script again later:
  `az redisenterprise delete --cluster-name meepleledger-redis --resource-group rg-meepleledger --yes`.

### Recommendation cache (A-16)

```
dotnet run --project MeepleLedger -- --SearchIndex:Provider=Cosmos --RecommendationCache:Enabled=true
dotnet run --project MeepleLedger.Seeder -- invalidate-cache
```

`CachedGameSearchIndex` is a decorator over `IGameSearchIndex`: `Program.cs` wraps whichever Provider
is live in it when `RecommendationCache:Enabled` is `true` (default `false`, so a fresh clone needs no
Redis). It shares one singleton `ConnectionMultiplexer` and needs the `Redis:ConnectionString` secret.
Each Ask logs `Recommendation cache HIT` or `MISS` with the time it took.

| Concern | Choice | Why |
|---|---|---|
| Key | `tonight:entry:<provider>:g<generation>:<sha256 of Ask>:players=..:minutes=..:owned=..:max=..` | Every Table field. Leaving out `owned` would answer a shelf-only Ask with Games you don't own. The Provider is in it so a Cosmos answer is never served to the Postgres app |
| TTL | 60 min (`RecommendationCache:TtlMinutes`) | Invalidation handles the known changes, so the TTL is a backstop for unseen ones (a hand edit in Postgres). A game night is planned within an evening. The instance is `NoEviction`, so the TTL is what stops it filling up |
| Collection changes | Tag sets `tonight:tag:owned-only` and `tonight:tag:game:<name>` | `GameCollection.Changed` fires on add/remove; every owned-only entry and every entry listing that Game is deleted. No `KEYS` |
| Blurb edits | `INCR tonight:generation` | A new vector can move a Game into any answer, so every entry is affected. `watch-cosmos` bumps it on any Game change; `invalidate-cache` does it by hand (Postgres has no change feed) |
| Redis down | Fail open | `AbortOnConnectFail=false`, `BacklogPolicy.FailFast`, 1 s timeouts: errors are logged and the Ask goes to the Provider |

- **A hit costs two round trips** (the generation, then the entry): about 90 ms from here to
  centralus. That beats a vector Provider (an embeddings call plus a query) and loses to the
  InMemory Provider (about 4 ms). Don't turn the cache on in front of InMemory.
- **Clustered:** nothing uses `MULTI` or a cross-slot multi-key command other than `DEL`.

### Redis as a Search Index (A-17)

```
dotnet run --project MeepleLedger.Seeder -- upload-redis
dotnet run --project MeepleLedger -- --SearchIndex:Provider=Redis
```

`upload-redis` writes each Game as a hash at `game:<name>` (the same rows as `upload-postgres`) and
builds the RediSearch index `idx:games` over them. `RedisGameSearchIndex` embeds the Ask and runs one
`FT.SEARCH`. It needs the `Redis:ConnectionString` and `AzureOpenAI:*` secrets, and shares the
cache's singleton `ConnectionMultiplexer`.

```
FT.CREATE idx:games ON HASH PREFIX 1 game: SCHEMA
  name TAG SEPARATOR | CASESENSITIVE
  min_players NUMERIC  max_players NUMERIC  playtime_minutes NUMERIC
  embedding VECTOR FLAT 6 TYPE FLOAT32 DIM 1536 DISTANCE_METRIC COSINE

FT.SEARCH idx:games
  "(@min_players:[-inf $players] (@max_players:[-inf 0] | @max_players:[$players +inf])
    @playtime_minutes:[-inf $minutes] @name:{Azul|Sea\ Salt\ \&\ Paper})=>[KNN 10 @embedding $askVector AS distance]"
  PARAMS 6 players 3 minutes 60 askVector <6144 bytes>
  SORTBY distance ASC  LIMIT 0 10  RETURN 3 name blurb distance  DIALECT 2
```

- **The vector is raw bytes:** 1536 little-endian float32 values, written and queried as a binary
  blob. `upload-redis` ends with a self-check: a Game's own vector must find that Game first.
- **The Table is the filter in front of `=>`**, so KNN only ranks Games the Table admits. The
  `@name:{...}` tag list is only there when "Only games on my shelf" is ticked. `k` is written into the
  query string; the numbers and the vector go in `PARAMS`. `DIALECT 2` is needed for both.
- **FLAT, not HNSW.** At 200 vectors an exact scan costs microseconds, and it cannot lose Games to a
  filter the way HNSW did in Postgres (A-13), so there is no fallback query.
- **Tag escaping:** ASCII punctuation and spaces in a name get a backslash. Non-ASCII characters
  must not (`Dune: Imperium – Uprising` with a backslash before the en dash matches nothing). The
  separator is `|` because the default, a comma, would split `Air, Land, & Sea`.
- **`FT.CREATE` is not idempotent**, so `upload-redis` drops the index (without `DD`, which would
  delete the hashes too) and creates it again on every run. It also deletes stray `game:*` keys
  (found with `SCAN`) and bumps `tonight:generation` so cached Recommendations are dropped.
- **RESP2 is pinned.** StackExchange.Redis 3 negotiates RESP3, and then `FT.SEARCH` answers with a
  map instead of the flat array the code reads. Both connections set `Protocol = RedisProtocol.Resp2`.
- **Clustered:** with the `EnterpriseCluster` policy the search module runs distributed. Each shard
  indexes its own hashes and `FT.SEARCH` is fanned out and merged, so no hash tags are needed.

#### Comparing the three Providers (A-17)

```
dotnet run --project MeepleLedger.Seeder -- compare-providers
```

This sends the same Ask vector and Table (3 players, 60 minutes, top 10, whole Catalog) to all three
stores: one warm-up, then 20 timed runs each, from a home connection to centralus/eastus2. The
Azure OpenAI account had been deleted when this was measured, so a Game's own vector (Pandemic)
stood in for the Ask, as `load-postgres` does. The stores do the same work either way.

| Provider | p50 | p95 | Cost per Ask | Billing |
|---|---|---|---|---|
| Cosmos DB (serverless) | 48 ms | 120 ms | 6.70 RU (about $1.70 per million Asks at $0.25 per million RU) | Per request, plus storage. Nothing when idle |
| PostgreSQL (B1ms, pgvector) | 49–60 ms | 55–63 ms | nothing extra | Hourly while the server runs (about $12/month compute), can be stopped |
| Redis (Managed, Balanced B0) | 36–44 ms | 37–46 ms | nothing extra | Hourly while it exists. No stop |

Ranges are across two runs. All three returned the same top 10. Every Provider also pays the
embeddings call on top (not measured here). Every number is mostly the ~40 ms round trip; the store's
own work is a few milliseconds. At 200 Games, latency does not separate the three. Durability and billing do.
Redis holds everything in memory with high availability off, so a restart loses the index and it
must be reloaded from the other stores. Cosmos and Postgres pay for durable storage.

**When to choose each:**
- **Cosmos DB:** when the vectors live alongside operational documents that are already in Cosmos,
  traffic is spiky or low (serverless bills nothing when idle), and you want a change feed to keep
  the embeddings current.
- **PostgreSQL:** when the data is relational and the vector search needs joins and SQL filters
  against it, and you want one durable, transactional store whose plans you can inspect and tune.
- **Redis:** when the lowest, steadiest latency at high query rates matters more than durability, and
  the vectors are a copy of data held durably elsewhere. You pay for the whole dataset in memory, by
  the hour, whether or not anyone asks.

### Azure Service Bus (A-18)

```
pwsh infra/servicebus/provision-servicebus.ps1
```

The script creates a Standard tier namespace called `meepleledger-bus` in `rg-meepleledger` (eastus2),
with one queue and one topic, and saves `ServiceBus:ConnectionString` into the Seeder's user secrets.
The queue is for *work I want done*: one embed request, handled by exactly one worker (A-19, A-21,
A-27). The topic is for *something happened*: every subscription whose filter matches gets its own
copy of the event.

| Setting | Value | Why |
|---|---|---|
| Tier | Standard | Basic has queues but no topics or subscriptions |
| Queue | `embed-requests` | Work queue for the embedding pipeline |
| Max delivery count | 5 (default 10) | Any receive that does not Complete counts. After 5 the message is dead-lettered (`MaxDeliveryCountExceeded`). Throttling is already retried inside one attempt, so 5 failed attempts means a poison message, and 10 would only make it slower to show up |
| Lock duration | 1 minute | One embed is a few seconds. A crashed worker's message goes back to the queue within a minute. The cap is 5 minutes: long work renews the lock (A-19) |
| Message TTL | 1 day | A request nobody handled in a day means the worker is down |
| Dead-letter on expiration | On | An expired request moves to the dead-letter queue (`TTLExpiredException`) where it can be resubmitted, instead of being dropped and leaving a stale vector |
| Topic | `meeple-events` | Domain events. The event name goes in the message Subject (`Label` in the CLI, `sys.Label` in SQL filters) |
| Subscription `embedding` | Correlation filter: `Label = BlurbChanged` | A new Blurb means a new vector. An exact match on one property is the cheapest filter |
| Subscription `recommendation-cache` | SQL filter: `sys.Label IN ('BlurbChanged', 'GameAdded', 'GameRemoved')` | Anything that changes which Games a Recommendation can hold. `PlayLogged` is left out on purpose: it goes over Event Grid (A-20) |
| Auth | Rule `meepleledger-app`, Send + Listen | `RootManageSharedAccessKey` can also create and delete entities, which no client needs |

Both subscriptions use the same delivery count, lock, TTL and dead-letter settings as the queue.

- **A subscription with no rule of its own gets everything.** A new subscription comes with a
  `$Default` rule (`1=1`), and adding a second rule does not replace it: rules are OR'ed. The script
  adds its own rule and then deletes `$Default`, and warns if `$Default` ever comes back.
- **Re-running is safe** and re-applies the queue and subscription settings, so the portal can't
  quietly drift from this file. Changing a filter means deleting the old rule by hand first: the
  script only adds a rule that isn't there.
- **Check it before writing code:** in the portal, open `meepleledger-bus` > Queues > `embed-requests`
  > Service Bus Explorer, send a message, then *Peek from start*. A peek does not lock or remove the
  message. On the topic, send one message with Label `BlurbChanged` and one with `PlayLogged`: the
  first should land in both subscriptions, the second in neither.
- **It bills every hour it exists** (Standard has a small base charge) and has no stop. A-33 deletes
  `rg-meepleledger`, which takes it with it; to remove only the namespace:
  `az servicebus namespace delete --name meepleledger-bus --resource-group rg-meepleledger`.

### The embed worker (A-19)

```
dotnet run --project MeepleLedger.Worker                                  # run until Enter
dotnet run --project MeepleLedger.Worker -- send <game id>                # queue one embed request by hand
dotnet run --project MeepleLedger.Worker -- send-bad [missing|json|version]   # queue a poison message
dotnet run --project MeepleLedger.Worker -- dlq [list|resubmit|purge]     # the dead-letter queue
```

The worker runs a Service Bus processor on `embed-requests`. For each request it reads the Game from
Cosmos as it is now, embeds its text with the `game-embeddings` deployment, and patches `/embedding`
and `/embeddingTextHash` back onto the document. It reads the `ServiceBus:*`, `AzureOpenAI:*` and
`Cosmos:*` user secrets (the same secrets ID as the Seeder), never a settings file.

Each message ends one of three ways, and the handler settles it itself (`AutoCompleteMessages` is off):

| Outcome | When | What Service Bus does |
|---|---|---|
| Complete | The vector was written, or was already up to date | Removes the message |
| Abandon | Anything that might work next time: a failed embeddings call, a Cosmos error, a Game not found, the Game edited between read and write (412) | Puts it back and adds one to its delivery count. After the 5th delivery it moves it to the dead-letter queue (`MaxDeliveryCountExceeded`) |
| Dead-letter | The message itself is wrong: not JSON (`BadPayload`) or an unknown schema version (`UnsupportedSchemaVersion`) | Moves it to the dead-letter queue now, with that reason |

- **Throwing does not dead-letter.** An exception in the handler only abandons the message, so a bad
  message would come back 4 more times. Messages that can never work are dead-lettered explicitly.
- **Duplicates are normal.** Service Bus delivers at least once. If the stored hash already matches
  the Game's text the worker completes the message without calling the embeddings API, so a second
  copy costs one Cosmos read.
- **The patch is conditional.** It only applies if the document's ETag is still the one the worker
  read. A whole-document upsert would put the old Blurb back over an edit made in between.
- **Locks are renewed** by the processor for up to 5 minutes, so a slow call does not hand the message
  to a second worker. Up to 4 messages are handled at once.
- **Schema version** 1 is in the body (`schemaVersion`) and in an application property of the same name.
- **The dead-letter queue never expires** and counts against the queue's size. The worker prints how
  many messages are in it when it starts. `dlq` lists each one with its reason, description and the
  last error. `dlq resubmit` puts them back on the queue, except ones the worker could not read.
  `dlq purge` deletes them.

To see the poison path, run the worker and `send-bad` in another terminal. A request for a Game that
does not exist fails 5 times about 5 seconds apart, then shows up in `dlq` as `MaxDeliveryCountExceeded`.
`send-bad json` is dead-lettered on the first delivery.

End to end: run `watch-cosmos` and the worker side by side, then edit a Blurb in Data Explorer. The
watcher publishes a request, the worker re-embeds the Game and prints how similar the new vector is to
the old one, and the watcher then sees the vector write and prints `text unchanged, vector is up to
date`. That last line means the worker and the Seeder hash the text the same way; if they did not, the
two would loop forever.

### The embed worker as Azure Functions (A-21)

```
pwsh MeepleLedger.Functions/write-local-settings.ps1     # once, and again after a key changes
cd MeepleLedger.Functions; func start                    # needs Azure Functions Core Tools v4
curl -X POST http://localhost:7071/api/games/Wingspan/embed   # queue a re-embed for one Game
```

`MeepleLedger.Functions` is the A-19 worker re-homed on the **isolated worker** model (.NET 10, Functions
v4). The in-process model is retired. It has two functions:

| Function | Trigger | Bindings | What it does |
|---|---|---|---|
| `EmbedRequest` | Service Bus queue `embed-requests` | `ServiceBusMessageActions` | The Worker's message handler: embeds the Game and patches `/embedding` and `/embeddingTextHash` in Cosmos |
| `QueueEmbed` | HTTP `POST /api/games/{name}/embed` | Cosmos **input** (the Game by id), Service Bus **output** (the request) | 404 for an unknown Game; otherwise queues an embed request and answers 202 |

`QueueEmbed` has no SDK client code: the host reads the Game and sends the message. `EmbedRequest`
still uses the Cosmos SDK for the write, because the Cosmos output binding can only upsert a whole
document and the write must be a patch guarded by the ETag that was read.

- **Settlement moved to the host.** A function that returns normally has its message completed by the
  host (`autoCompleteMessages` in `host.json`). Bad payloads are still dead-lettered explicitly and
  failures still abandoned explicitly (with `lastError`, after a 5 second pause), so the three outcomes
  and the Worker's `dlq` output are the same as A-19.
- **The processor options moved to `host.json`:** `maxConcurrentCalls` 4, `maxAutoLockRenewalDuration`
  5 minutes, `functionTimeout` 5 minutes (the Consumption default, spelled out). Max delivery count and
  lock duration are on the queue, so they never changed.
- **Settings.** Bindings are run by the Functions host, which does not read user secrets, so
  `write-local-settings.ps1` copies them into `local.settings.json` (ignored by git, never published).
  Binding settings are flat names (`ServiceBusConnection`, `CosmosConnection`, `CosmosDatabase`,
  `CosmosContainer`); the code's own use `__` (`AzureOpenAI__Key`). A-22 turns each into an app setting.
- **`AzureWebJobsStorage` is required** even though nothing here uses storage. The script points it at
  A-20's storage account when that secret exists, otherwise at Azurite.
- **Don't run the Worker and `func start` together** unless you want competing consumers: both take
  from the same queue and each message goes to one of them. The Worker's `send`, `send-bad` and `dlq`
  commands are how to drive and check the function.

### The function apps in Azure (A-22)

```
pwsh infra/functions/provision-functions.ps1         # apps, identities, roles, app settings; metered
pwsh infra/functions/deploy-functions.ps1            # publish + zip deploy to meepleledger-func (Flex)
pwsh infra/functions/deploy-functions.ps1 -Staging   # deploy to meepleledger-func-win, slot staging
pwsh infra/functions/deploy-functions.ps1 -Swap      # swap staging <-> production, shows /api/about before and after
```

Two function apps run the same package:

| App | Plan | Role |
|---|---|---|
| `meepleledger-func` | Flex Consumption, Linux, .NET 10 isolated | The real one: `EmbedRequest` processes `embed-requests`, `QueueEmbed` answers on its real URL |
| `meepleledger-func-win` | Consumption, Windows, .NET 10 isolated, slot `staging` | The slot exercise only. `AzureWebJobs.EmbedRequest.Disabled` is `true`, so it never competes for messages |

No single cheap plan does everything: Flex runs .NET 10 on Linux but has **no deployment slots**;
Linux Consumption has slots but gets no new language versions (no .NET 10) and retires in September
2028. Hence the second, Windows, app for slots.

- **Service Bus through the identity.** Each app (and the staging slot, which has its *own* identity)
  has a system-assigned managed identity with roles on the `embed-requests` queue only: the Flex app
  gets *Azure Service Bus Data Receiver* (the trigger, and settling messages) and *Data Sender*
  (`QueueEmbed`'s output binding); the Windows app only *Data Sender*. No code changed: with an
  identity, `Connection = "ServiceBusConnection"` is a **prefix**, and the app setting is
  `ServiceBusConnection__fullyQualifiedNamespace = meepleledger-bus.servicebus.windows.net`. A plain
  `ServiceBusConnection` setting would win over it, so the script removes one if it finds it.
  Role assignments take minutes to propagate; a trigger tested straight away can fail and then fix itself.
- **App settings come from the user secrets**, the same names `local.settings.json` uses, and the
  script ends by checking every local setting has its Azure counterpart. A code deploy never carries
  them. Cosmos and Azure OpenAI still use keys (A-29 moves them to Key Vault).
- **Slot settings.** `SlotName` is a deployment slot setting, so it stays with its slot through a
  swap while the code moves. `GET /api/about` (anonymous) shows both: `-Swap` prints it before and
  after, and the builds trade places while the slot names stay put.
- **Each Consumption slot needs its own content share.** The code lives in the Azure Files share named
  by `WEBSITE_CONTENTSHARE`, and creating a slot with `--configuration-source` copies production's
  value. Both slots then run from one share: a deploy to staging shows up in production and a swap
  changes nothing. The script gives the slot its own share (Azure refuses to make that setting a slot
  setting; it is never swapped anyway).
- **Logs** go to Application Insights `meepleledger-func-insights` on workspace `meepleledger-logs`,
  both created in `rg-meepleledger` so deleting the group removes them. To trace one embed request
  from send to completion (Application Insights > Logs):

```kusto
union requests, traces
| where timestamp > ago(30m)
| where cloud_RoleName == "meepleledger-func"
| where operation_Name in ("QueueEmbed", "EmbedRequest")
| project timestamp, operation_Name, itemType, message, resultCode, operation_Id
| order by timestamp asc
```

### Event Grid: `PlayLogged` (A-20)

```
pwsh infra/eventgrid/provision-eventgrid.ps1 [-BrokenEndpoint https://<tunnel>/api/events]
dotnet run --project MeepleLedger -- --EventGrid:Enabled=true --RecommendationCache:Enabled=true
dotnet run --project MeepleLedger.Events                       # the two subscribers, until Enter
dotnet run --project MeepleLedger.Events -- webhook [status]   # fails every delivery (503), on port 5095
dotnet run --project MeepleLedger.Events -- dead-letters [purge]
```

Logging a Play publishes a `PlayLogged` event to the Event Grid custom topic `meepleledger-plays`.
This is the other half of the A-18 split: an embed request is *work I want done* and goes on a Service
Bus queue, where exactly one worker must handle it. `PlayLogged` is *something happened*: the app does
not know who listens, any number of subscribers can react, and nobody listening is fine.

The event is thin: which Play, which Game, the date, and whether you own the Game. Results, location
and duration stay in the store.

```json
{
  "id": "62df6263-...",
  "eventType": "MeepleLedger.PlayLogged.v1",
  "subject": "games/Wingspan/plays/578be47f-...",
  "dataVersion": "1",
  "data": { "playId": "578be47f-...", "game": "Wingspan", "playedOn": "2026-10-22", "owned": false }
}
```

- **The version is in the type.** A breaking change to the data becomes `MeepleLedger.PlayLogged.v2`,
  and since every subscription filters on the exact type, no subscriber gets data it cannot read.
- **The subject is a path**, most general part first, so a prefix filter can pick "every Play"
  (`games/`) or "Plays of one Game" (`games/Wingspan/`).
- **Fire and forget.** `PlayLoggedPublisher` listens to `PlayLog.Recorded`, so the Log a Play page
  knows nothing about events. The Play is saved before anything is sent; a failed publish is logged
  and the event is lost. The page never waits for or sees it. A test proves a Play is still logged
  when Event Grid can't be reached. Editing a Play does not publish (only `Record` raises the event).
- **Off by default** (`EventGrid:Enabled` is `false`), so the app still runs on nothing but the code
  (ADR-0001). When on, it needs `EventGrid:TopicEndpoint` and `EventGrid:TopicKey`, which the script
  saves into the shared user secrets, along with `EventStorage:ConnectionString`.

The script creates the topic, a storage account `meepleledgerevents` (two queues and a blob container
for dead letters), and these subscriptions:

| Subscription | Filter | Delivers to | What happens |
|---|---|---|---|
| `recommendation-cache` | Event type + subject begins with `games/` | Storage queue `play-logged-cache` | `MeepleLedger.Events` drops the cached Recommendations (A-16) that list the Game that was played |
| `not-owned-feed` | Event type + **advanced filter** `data.owned BoolEquals false` | Storage queue `play-logged-not-owned` | `MeepleLedger.Events` prints "You played X ... Wishlist it?" |
| `broken-webhook` (only with `-BrokenEndpoint`) | Event type | Webhook: `MeepleLedger.Events -- webhook` through a tunnel | Fails every delivery; 5 attempts, 30 minute TTL, then dead-lettered |

Every subscription dead-letters to the `eventgrid-deadletter` container.

- **Subject filter first, advanced filter where it earns it.** "Not owned" is a value inside the
  data, which a subject prefix can't express. Advanced filters are limited to 25 per subscription,
  and each operator only works on one value type: `BoolEquals` on a boolean, `NumberIn` on a number.
  A filter whose type doesn't match the data silently matches nothing.
- **The az CLI gets `BoolEquals false` wrong.** `--advanced-filter data.owned BoolEquals false` is
  stored as `true`: the CLI turns the string into a boolean, and any non-empty string is true. The
  subscription then quietly gets only Plays of Games you *do* own. The first live run hit exactly
  this. The script puts the value right with `update --set "filter.advancedFilters[0].value=false"`
  (`--set` parses JSON) and warns on read-back if it is ever not `false`. Always read a filter back.
- **Why queues and not webhooks for the working subscribers:** a webhook needs a public HTTPS URL that
  answers the validation handshake. A queue keeps the events until `MeepleLedger.Events` reads them,
  so the subscribers can run on a laptop and be stopped and started freely.
- **The handshake.** Event Grid sends a `SubscriptionValidation` event while a webhook subscription
  is being *created*, and only creates it if the answer echoes the validation code. A webhook that
  never answers just looks broken: no events, no error. So the webhook command and the tunnel must be
  running before the script runs with `-BrokenEndpoint`.
- **Order is not guaranteed, delivery is at least once.** Both handlers are safe to run twice and
  don't care which Play arrives first. A message the subscribers can't handle is retried 5 times, then
  moved to `<queue>-poison`.
- **The cache subscriber is a hook for now.** It drops every cached answer that lists the Game played
  (the per-Game tag sets from A-16). Today no Reason mentions Plays, so those answers would come back
  the same; it is where a Reason like "you last played it in May" would get its invalidation.

**Watching the retries.** Install a tunnel once (`winget install Microsoft.devtunnel`, then
`devtunnel user login`), then:

1. `dotnet run --project MeepleLedger.Events -- webhook` (listens on `http://localhost:5095/api/events`)
2. `devtunnel host -p 5095 --allow-anonymous` (prints an `https://...devtunnels.ms` URL)
3. `pwsh infra/eventgrid/provision-eventgrid.ps1 -BrokenEndpoint https://<tunnel>/api/events`; the
   webhook prints `handshake: answered validation code ...`
4. Log a Play in the app. The webhook prints one line per attempt with the seconds since the first.
5. `dotnet run --project MeepleLedger.Events -- dead-letters` prints each dead-lettered event's
   reason, attempt count, last outcome and times.

What the first live run (26 October, two Plays, endpoint answering 503) actually showed:

| | 7 Wonders Duel | Scythe |
|---|---|---|
| Published | 14:04:24 | 14:08:43 |
| Attempts | +0 s, +60 s, +660 s | +0 s, +60 s, +660 s |
| Dead-letter blob written | 15:20:26 (+76 min) | 15:24:44 (+76 min) |
| Reason | `TimeToLiveExceeded`, 3 attempts, last outcome `Busy (HTTP 503)` | the same |

- **The published schedule is a floor, not a timetable.** The docs list 10 s, 30 s, 1 min, 5 min,
  10 min, 30 min, 1 h...; what arrived was 1 min, then 10 min, then nothing for an hour. Event Grid
  backs off harder from an endpoint that keeps failing, and adds jitter.
- **The 30 minute TTL did not end anything at 30 minutes.** Event Grid only checks the time to live
  and the attempt count when a retry falls due. The next one came about an hour after the third
  attempt, so the events were dead-lettered at +76 minutes with only 3 of the 5 allowed attempts used.
  Plan for dead-lettering to take well over the TTL, and read the reason: `TimeToLiveExceeded` and
  `MaxDeliveryAttemptsExceeded` are different stories.

`webhook 400` shows the other path: Event Grid does not retry a 400, 401, 403, 404 or 413 from a
webhook, so the event is dead-lettered after the first attempt.

**Without a dead-letter destination, an event that runs out of attempts is dropped silently.** The
script warns if any subscription is missing one. The defaults are 30 attempts over up to 24 hours,
which is why the broken subscription sets its own. Delete it when you're done:
`az eventgrid event-subscription delete --name broken-webhook --source-resource-id <topic id>`.

**Cost:** Event Grid bills per million operations (the first 100,000 a month are free) and the
storage account per GB. Neither has an hourly charge. A-33 deletes `rg-meepleledger` with both in it.

## Running in containers (A-23)

```
docker compose up --build    # the app on http://localhost:8080, the worker, Postgres (pgvector) and Redis
docker compose down -v       # stop, and drop the Postgres volume
```

[MeepleLedger/Dockerfile](MeepleLedger/Dockerfile) and [MeepleLedger.Worker/Dockerfile](MeepleLedger.Worker/Dockerfile)
build from the repo root (`docker build -f MeepleLedger/Dockerfile .`), so the root
[.dockerignore](.dockerignore) applies to both.

- **Two stages.** The `sdk:10.0` image builds; the app runs on `aspnet:10.0`, the worker (a console
  app) on `runtime:10.0`. The SDK never reaches the final image.
- **The `.csproj` is copied and restored before the source**, so a source edit reuses the cached
  restore layer instead of downloading every package again.
- **`.dockerignore`** leaves out `bin/`, `obj/`, the root `raw/` and `data/` (patterns are relative
  to the repo root, so `data` does not touch `MeepleLedger/Data`, which the app needs), `.env`,
  `local.settings.json` and the projects neither image builds. Its patterns have no leading or
  trailing slash, because `az acr build` reads the file too and would otherwise ignore them (A-24).
- **Non-root.** Both run as the `app` user (uid 1654) the .NET images ship with, and the app listens
  on 8080, the images' default port. No HTTPS inside the container.
- **Nothing is baked in.** With no `.env` file the app runs on the in-memory Provider (ADR-0001) and
  the worker exits at once, saying which setting is missing. Settings go in a gitignored `.env` next
  to `compose.yaml`, colon written as `__` (`AzureOpenAI__Endpoint=...`). `SEARCH_INDEX_PROVIDER` and
  `RECOMMENDATION_CACHE_ENABLED` in `.env` switch the app onto the local Postgres or Redis.
- **The local Postgres gets the schema.** `infra/postgres/games-schema.sql` runs as an init script
  when the volume is first created, so `upload-postgres` can fill it if `Postgres:ConnectionString`
  points at `Host=localhost;Database=meepleledger;Username=meeple;Password=meeple-local`.
- **The worker stops on `docker stop`.** There is no keyboard in a container, so instead of waiting
  for Enter it waits for SIGTERM and lets messages in flight settle (compose allows 30 seconds).
  The worker also reads environment variables now, after the user secrets.

| Image | Size on disk | Compressed (what a registry stores) |
|---|---|---|
| `meepleledger:dev` | 405 MB | 114 MB |
| `meepleledger-worker:dev` | 344 MB | 95 MB |
| for comparison, `dotnet/sdk:10.0` alone | 1.31 GB | 359 MB |

**Blazor Server behind a load balancer needs sticky sessions** (or the Azure SignalR Service): each
user holds a SignalR circuit on one instance, and a request sent to another instance loses it. One
container locally doesn't care; A-26 (Container Apps) and A-28 (AKS) will. The same goes for the
Data Protection keys the app logs a warning about at startup: they live inside the container, so
each replica has its own and they are lost on every restart.

## Container Registry and ACR Tasks (A-24)

```
pwsh infra/acr/provision-acr.ps1 [-GitHubToken <token>]   # registry, purge task, commit-triggered build task
pwsh infra/acr/push-images.ps1                           # build here with Docker, push
pwsh infra/acr/build-in-acr.ps1                          # az acr build: built by ACR, no local Docker
pwsh infra/acr/build-in-acr.ps1 -Run                     # az acr run of infra/acr/acr-task.yaml
pwsh infra/acr/purge-tags.ps1 [-Delete]                  # the retention purge now (dry run by default)
```

The registry is `meepleledgeracr.azurecr.io`, Basic tier, in `rg-meepleledger` (A-33 deletes it with
the resource group). It holds two repositories, `meepleledger` and `meepleledger-worker`.

- **Every image has a version tag and `latest`.** Pushed from this machine or by `az acr build`, the
  version is the short commit hash (`meepleledger:cd8b339`); both scripts refuse to run with
  uncommitted changes, so the hash really is the code in the image. Built by a task, it is the run id
  (`meepleledger:ca1`), and `az acr task list-runs` shows the commit behind it. `latest` moves on every
  push. Deploy by version tag only: with `:latest` you can't tell what is running or roll back.
- **The admin account is off** and `provision-acr.ps1` switches it back off if it finds it on. Pushing
  uses `az acr login` (your Azure CLI identity); A-25, A-26 and A-28 pull with a managed identity that
  has `AcrPull`.
- **`az acr build` vs `az acr run`.** `build` is `docker build` in the cloud: one Dockerfile, one image.
  `run` runs a task file once, without saving a task: [acr-task.yaml](infra/acr/acr-task.yaml) builds
  both images side by side, then pushes them in one step after both have built. The `build-images` task
  runs the same file on every commit to `main` (pull requests don't trigger it). It reads the file from
  GitHub, so push before you expect it to change.
- **Both upload the repo as the build context**, filtered by `.dockerignore`. The az CLI reads that
  file with a literal matcher: `/raw/` or `bin/` match nothing, so with A-23's original file the upload
  was 206 MB, including `raw/`, `data/`, every `bin/` and `obj/`, and `.vs`. Without the slashes it is
  1.9 MB.
- **Retention:** the `purge-old-tags` task runs every Sunday at 04:00 UTC:
  `acr purge --ago 7d --keep 5 --untagged` on both repositories. It keeps the 5 newest version tags of
  each image, deletes older ones, and deletes untagged manifests (left behind when a tag moves, and
  still taking up storage). `latest` is always among the newest. Basic has no automatic retention
  policy (Premium has one, for untagged manifests), so the task is the cleanup.
- **Tiers:** all three have the same API and ACR Tasks. Standard adds storage (100 GB vs 10) and
  throughput; Premium adds geo-replication, private endpoints, content trust and repository-scoped
  tokens.

**Cost:** Basic is a fixed daily charge for as long as the registry exists, plus storage beyond 10 GB.
ACR Tasks bill per second of build time.

## App Service for Containers (A-25)

```
pwsh infra/appservice/provision-appservice.ps1 [-Tag <version>] [-Provider InMemory|Cosmos|Postgres|Redis]
pwsh infra/appservice/set-provider.ps1 Cosmos            # flip the Provider: one app setting, then a restart
az webapp log tail --name meepleledger-web --resource-group rg-meepleledger   # stream the container log
```

The web app runs at https://meepleledger-web.azurewebsites.net: Web App for Containers `meepleledger-web`
on the Linux B1 plan `meepleledger-plan`, in `rg-meepleledger` (A-33 deletes both with the resource group).

- **The image comes from ACR by identity.** The app has a system-assigned managed identity with `AcrPull`
  on `meepleledgeracr` only, and `acrUseManagedIdentityCreds` on, so the pull needs no password and the
  registry's admin account stays off. The CLI still warns "Retrieving credentials failed" when the image
  is set: it looks for the admin account, finds it off, and carries on. A new `AcrPull` assignment can
  take minutes to reach the registry; the pull is refused until then, so wait rather than debug.
- **Deployed by version tag.** By default the newest tag in the registry that isn't `latest`; `-Tag`
  picks another one, which is how you roll back.
- **Every setting is an app setting.** App Service hands them to the container as environment
  variables, so nested keys use `__`: `SearchIndex__Provider`, `Cosmos__Key`. A name with a colon is
  silently never read. The script copies the keys from the user secrets; `Cosmos:Database`, the
  Postgres pool settings and the cache TTL are not secrets and come from `appsettings.json` in the image.
- **`WEBSITES_PORT=8080`.** The .NET images listen on 8080 and App Service sends traffic to 80 unless told
  otherwise. (Today's platform also reads the image's `EXPOSE 8080`, but the setting doesn't rely on it.)
- **Always On, web sockets and ARR affinity are on.** Always On is off by default on low tiers, and then
  the first click after 20 idle minutes waits for a cold start. A Blazor Server circuit is a SignalR
  connection held in one instance's memory: web sockets keep it fast, and ARR affinity sends the browser
  back to the instance that holds it. Turning affinity off makes the app randomly reload once there is
  more than one instance.
- **The Provider switch is ADR-0001 in public.** The same image runs on the in-memory Provider with no
  settings at all, and on Cosmos after `set-provider.ps1 Cosmos`: no rebuild, no redeploy. The Tonight
  page shows the change: the "matches the words of your Ask" notice disappears. Postgres would also need
  a firewall rule for the app's outbound IP addresses (`az webapp show --query outboundIpAddresses`).
- **Container logs** go to the app's file system (`--docker-container-logging filesystem`), for
  `az webapp log tail`, or all at once with `az webapp log download`.

**Cost:** B1 is a fixed hourly charge for as long as the plan exists, whether the app is running or
stopped. Delete the plan to stop it.
