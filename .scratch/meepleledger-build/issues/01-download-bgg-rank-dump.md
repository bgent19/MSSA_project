# 01 — Download the BGG rank dump by hand

**What to build:** The ranked-games CSV is on disk and out of the repo, so that the seeder has a
source of top-ranked game ids to fetch. This is an input to the build, not a deliverable.

**Blocked by:** None — can start immediately.

**Status:** ready-for-brett

Download it from a logged-in browser at `https://boardgamegeek.com/data_dumps/bg_ranks`.

**Do this at least two days before any coding session.** It is the single most fragile step in the
whole plan — not automatable, not quickly retryable, and it gates the catalog. Two days early makes
a second attempt free.

- [ ] The CSV is downloaded and saved either outside the repo or in a gitignored location
- [ ] `git status` shows the CSV is not tracked
- [ ] Rows with `is_expansion = 1` can be identified for filtering later
- [ ] The file is sorted or sortable by `rank`, so the top ids can be taken

## Watch out for

- **Do not try to fetch this in code.** Verified: `GET /data_dumps/bg_ranks` with a valid bearer
  token returns `200` with `Content-Type: text/html` — a generic Angular shell, with no `.csv`,
  `.zip` or `amazonaws` link anywhere in the body. The bearer token authorizes the **XML API**, not
  site pages, which want a logged-in browser session.
- **If the download fails or the login won't work, do not fight it.** The pre-sanctioned fallback is
  `/xmlapi2/hot?type=boardgame` — 50 items, verified working with the token — accepting a ~75-game
  catalog instead of ~200. This needs no decision on the day; nothing downstream depends on the
  figure 200, only on *catalog wider than the 28-game shelf*. The fallback is also faster, roughly
  4 API calls instead of 10.
