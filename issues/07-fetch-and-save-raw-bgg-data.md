# 07 — Fetch the game data from BGG and save every raw response

**What to build:** The seeder console app calls the BoardGameGeek XML API once, offline, and writes
every raw response to disk — so that the parsing work in the next ticket can be redone as many times
as it takes without spending another API call.

**Blocked by:** 01 — Download the BGG rank dump by hand

**Status:** ready-for-brett

This ticket does no parsing and emits no C#. It ends with a pile of raw XML on disk. That separation
is deliberate: a parsing bug discovered at game 147 must not cost ten more API calls and fifty
minutes.

The run, in order:

1. `/collection` for the owned shelf — **28 games**, verified. Write the `202` retry loop anyway; it
   is correct, and a cold queue may be slower than the ~2s observed.
2. Take the top-ranked ids from the CSV, filtering out `is_expansion = 1`.
3. Union the owned ids with the ranked ids and dedupe. With only 28 owned you need roughly **172**
   ranked ids to reach ~200 unique. Check the count *before* spending API calls.
4. `GET /thing?id=<20 ids>&stats=1` — **20 ids per call, 5 seconds between calls**, ~10 calls.

- [ ] The seeder reads `BGG_TOKEN` from the environment and sends it as a bearer token
- [ ] The `/collection` call handles a `202` by waiting and retrying
- [ ] Ranked ids are read from the CSV with expansions filtered out
- [ ] Ids are unioned and deduped, and the count is checked before fetching
- [ ] `/thing` is called in batches of 20 with `stats=1` and a 5-second gap between calls
- [ ] **Every raw response is saved to disk** before any parsing is attempted
- [ ] The raw dumps and the CSV are gitignored — `git status` proves it
- [ ] No token appears anywhere in a commit

## Watch out for

- **`HttpClient` will not let you set `Authorization` via `DefaultRequestHeaders.Add`** — it is a
  restricted header. Use:
  ```csharp
  http.DefaultRequestHeaders.Authorization =
      new AuthenticationHeaderValue("Bearer", token);   // using System.Net.Http.Headers;
  ```
- **A `401` probably is not a dead token.** `BGG_TOKEN` is a User-scope environment variable, and a
  terminal opened *before* it was set will not see it. **Open a fresh terminal first**, then suspect
  the token.
- **Throttling shows up as `500` or `503` — not `429`.** If you see one, you went too fast. Wait,
  then redo that batch.
- **Expect ~1.5 MB of XML across ~200 games** (a 20-id batch is ~298 KB), so buffer sensibly.
- **Re-run the data-quality check over the ranked ids.** All 28 owned games have complete data with
  zero missing values, but the ~172 ranked ids are a much wider and weirder sample. A `maxplayers`
  of `0` in there would make every play of that game throw the seat invariant.
- **The token is build-time only.** It never reaches the web app's configuration at all. If it leaks,
  revoke and regenerate at `https://boardgamegeek.com/applications`.
