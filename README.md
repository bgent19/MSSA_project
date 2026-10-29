# MeepleLedger

A Blazor Server web app (.NET 10) for tracking a board game collection and the plays logged against
it, with a "what should we play tonight?" recommender. The game data comes from real
[BoardGameGeek](https://boardgamegeek.com) (BGG) data.

The repo holds two things: **the app**, which is what ships, and **study material** from an Azure
AI-200 study track, which is not deployed. This page is about the app; the study material is
described in [docs/study.md](docs/study.md).

## Use it

| Page | Route | What it does |
|---|---|---|
| Collection | `/` | Lists the games you own. Search by title or designer, filter by player count. Export the Collection and Play Log as one JSON file. |
| Add a Game | `/collection/add` | Pick a game from the catalog and add it with its condition and date acquired. A game you already own is refused. A game the catalog doesn't have can be found on BoardGameGeek ("Not in the list? Add from BoardGameGeek", also on Log a Play; needs a `Bgg:Token` setting). On the Cosmos store it is kept in the `games` container and embedded at once, so Tonight can recommend it; on the in-memory store it lasts until the app restarts. |
| Game detail | `/games/{name}` | One game's details, its plays, and your win rate for it. |
| Log a Play | `/log` | Record a session: pick any catalog game (owned or not), then the date, players, scores, winners, duration and location. |
| Edit a Play | `/plays/{id}/edit` | The same form, filled in with an existing play. |
| Play Log | `/plays` | Every play, newest first. Filter by game or to games you don't own. Edit or delete a play. |
| Statistics | `/stats` | Games owned and plays logged, most played games, and games played but not owned. |
| Tonight | `/tonight` | Type an Ask ("something co-operative with cards"), set players, minutes and owned-only, and get ranked Recommendations with the reason for each. Offline it matches words, not meaning, and says so. With a cloud Provider (Cosmos DB in production) it matches by meaning against each game's Blurb. |

## Run it offline

You need the .NET 10 SDK and nothing else: no Azure account, no secrets, no database.

```
dotnet run --project MeepleLedger
```

Then open the URL it prints. The app gets its games from the three files in `MeepleLedger/Data/`.
Those files are committed to the repo, so the app runs right after a clone.

**Offline, changes are not saved.** The in-memory store starts over from the seed data each time the
app launches, so anything you add, edit or delete is gone when it stops. Run it with
`--MeepleStore:Provider=Cosmos` (and the `Cosmos:*` user secrets) to keep the Collection and Play Log
in Cosmos DB, the way production does.

## Deploy it

Production is a small slice of this repo, held to about $5 a month
([ADR-0004](docs/adr/0004-production-is-a-small-slice-of-the-study-build.md)): the web app in Azure
Container Apps (scales to zero), Cosmos DB serverless for the Catalog, Collection and Play Log,
Azure OpenAI for Ask embeddings, Key Vault for the secrets, and Application Insights.

- **Provision** with the script in [infra/production/](infra/production/) (S-09, not written yet).
  Nothing else under `infra/` is used by production.
- **Deploy** with GitHub Actions (S-10, not written yet): every pull request is built and tested,
  and every merge to `main` builds the image, pushes it to GitHub Container Registry tagged with the
  commit hash, and updates the container app.

## What's in the repo

| Path | What it is |
|---|---|
| [MeepleLedger/](MeepleLedger/) | **The app.** `Domain/` has the classes, `Storage/` connects them to the app, `Data/` has the game data, `Components/` has the pages. |
| [MeepleLedger.Tests/](MeepleLedger.Tests/) | **The app.** xUnit tests for the domain rules. |
| [MeepleLedger.Seeder/](MeepleLedger.Seeder/) | **The app.** A console app that creates the files in `MeepleLedger/Data/` and loads Cosmos DB. It also has the study commands (measure, compare, Postgres, Redis). |
| [infra/production/](infra/production/) | **The app.** Provisions production. |
| [docs/adr/](docs/adr/) | The architecture decisions. |
| [MeepleLedger.Worker/](MeepleLedger.Worker/), [MeepleLedger.Functions/](MeepleLedger.Functions/), [MeepleLedger.Events/](MeepleLedger.Events/) | Study material: the embed worker on Service Bus, the same worker as Azure Functions, and the Event Grid subscribers. See [docs/study.md](docs/study.md). |
| [infra/](infra/) (everything else) | Study material: one folder of provisioning scripts per Azure service. See [docs/study.md](docs/study.md). |
| [presentation/](presentation/) | HTML slides for the project presentation. |
| `raw/`, `data/` | Working files for the seeder. Git ignores both, so they are empty after a fresh clone. |

`MSSA_project.slnx` ties the six projects together.

## How it's built

### Domain

The classes in [MeepleLedger/Domain](MeepleLedger/Domain) hold the rules. The pages call them
rather than enforcing anything themselves.

- `Game` — a catalog entry: name, designer, player count range, play time.
- `GameCatalog` — every game the app knows about (about 200), with search and player-count lookup.
  A Game added from BoardGameGeek joins it; Games are never removed.
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
interfaces, registered as singletons in `Program.cs`:

- `IGameCatalogSource` — `SeededCatalogSource` reads `CatalogSeed`; `CosmosGameCatalogSource` reads
  the Cosmos `games` container.
- `IMeepleStore` — `InMemoryMeepleStore` builds the collection and play log from `CollectionSeed`
  and `LogSeed`; `CosmosMeepleStore` keeps them in Cosmos DB. `MeepleStore:Provider` picks one.
- `IGameSearchIndex` — the Tonight Search Index. `SearchIndex:Provider` picks it: `InMemory` (the
  default), `Cosmos`, or the study Providers `Postgres` and `Redis`.

The store is created at startup, so bad data fails the launch instead of the first page load.
`Program.cs` reads as a list of named startup steps; each one is a small method further down the file.

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
container and user secrets from [Cosmos DB for NoSQL](docs/study.md#cosmos-db-for-nosql) in the study notes.

It upserts every game into the `games` container as one document: the same facts as
`CatalogSeed.cs`, plus the Blurb, its vector at `/embedding`, and `embeddingTextHash` (the hash of
the text the vector was made from, used by the [change feed](docs/study.md#watching-the-change-feed)). The
document id is the game's name.
Anything else in the container (like the hand-written test document) is deleted, so the container
always matches `data/vectors.json`. Running it again is safe. It prints the request charge (RU) of
the upload; the whole catalog is about 6,000 RU, a fraction of a cent on serverless.

## Study material

Everything from the Azure AI-200 track (the Postgres and Redis Providers, Service Bus, the worker and
function apps, Event Grid, containers on App Service, Container Apps and AKS, Key Vault, App
Configuration, OpenTelemetry and KQL) is described in [docs/study.md](docs/study.md). None of it is
deployed to production.
