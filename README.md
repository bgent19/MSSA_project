# MeepleLedger

A Blazor Server web app for tracking a board game collection and the plays logged against it. The
game data comes from real [BoardGameGeek](https://boardgamegeek.com) (BGG) data.

The focus of the mini project is the `MeepleLedger.Seeder` project as well as the class design in
[MeepleLedger/Domain](MeepleLedger/Domain).

You need the .NET 10 SDK to build and run this.

## What's in the repo

| Path | What it is |
|---|---|
| [MeepleLedger/](MeepleLedger/) | The web app. `Domain/` has the classes. `Data/` has the game data. |
| [MeepleLedger.Seeder/](MeepleLedger.Seeder/) | A separate console app that creates the files in `MeepleLedger/Data/`. |
| `raw/`, `data/` | Working files. Git ignores both, so they are empty after a fresh clone. |

## Running the app

```
dotnet run --project MeepleLedger
```

NOTE: This app currently does nothing. This is the future work for this project.

The app gets its games from the three files in `MeepleLedger/Data/`. Those files are committed to
the repo, so the app runs right after a clone. You only need the seeder below if you want to
regenerate them.

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
