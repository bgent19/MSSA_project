# Confirm BGG API access is a working token

Type: task
Status: closed
Resolved: 2026-08-11
Assignee: Brett (wayfinder session, 2026-08-11)
Blocked by: —
Blocks: 05

## Question

Nothing to decide — one fact to establish, and it gates the data-source decision.

[Survey board game data sources](03-survey-data-sources.md) found that the BGG XML API
returns `401` to every unauthenticated request as of 2025, and that getting access means a
registered application whose approval BGG says "may be a week or more." Brett has said he
already has API access. **"Access" has two very different meanings here**, and the sprint
plan differs sharply between them:

- **An approved application with a bearer token in hand** — the API is usable today, and
  the build-time seeding plan recommended by ticket 03 is on the table.
- **A BGG user account** (which is what most people mean by having access) — the approval
  queue is longer than the whole project, and static seed data becomes the only option.

Checklist, ~5 minutes:

1. Go to `https://boardgamegeek.com/applications`. Is there an application listed, and is
   it **approved**? Note whether it was registered commercial or non-commercial.
2. If yes, click **Tokens** and confirm a token exists (or create one).
3. Prove it end to end. With the token in `$T`:
   ```
   curl -s -o out.xml -w "%{http_code}\n" \
     -H "Authorization: Bearer $T" \
     "https://boardgamegeek.com/xmlapi2/thing?id=224517&stats=1"
   ```
   `200` with real XML means access is genuine. `401` means it isn't — check for a `www.`
   in the URL first, since BGG calls that out as a common cause.
4. While there, note Brett's **BGG username** and roughly how many games are in his
   collection — ticket 05 wants to know whether a real collection import is worth doing.

**Do not commit the token.** It goes in .NET user-secrets or an untracked local file. If it
ever leaks, revoke and regenerate at the applications page.

Resolve by recording: whether an approved application exists, whether a live call returned
200, where the token is stored, the BGG username, and the collection size.

## Answer

**Access is genuine.** The optimistic branch of this ticket is the true one — an approved
application with a working bearer token, not merely a BGG user account. The approval queue
BGG warns about ("may be a week or more") is not in our path.

| Fact | Value |
|---|---|
| Approved application exists | **yes** |
| Live `GET /xmlapi2/thing?id=224517&stats=1` with bearer token | **200** |
| BGG username | **TheGentleBean** |
| Collection size | **~40 games** |

### What this unblocks

The build-time seeding route recommended by
[Survey board game data sources](03-survey-data-sources.md) is **live, not hypothetical**.
Seeding can run offline against the real API, and the committed output ships with the app —
real data, Brett's own LINQ-to-XML parsing on the page, zero network dependency at demo time.

### The collection size is the interesting number

**~40 games is close to ideal for this sprint**, and it reshapes ticket 05's options rather
than just permitting one:

- It's a **real, lived-in collection** — a genuine shelf, not twenty titles typed to fill a
  demo. The audience-recognition problem ticket 05 raises largely solves itself.
- It's **one `/collection` call**, and small enough that the 202-retry queue on that endpoint
  is a single retry rather than a polling loop.
- It's **small enough to hand-curate after import** — 40 rows can be eyeballed, trimmed, and
  corrected by hand, including the expansion-mislabelling issue the survey flagged.
- It **weakens the case for the CSV dump of thousands**. That route existed to close the
  unlisted-game gap; a real 40-game shelf plus a decision on hand-entry may close it more
  cheaply. Ticket 05 still has to answer the gap, but the brute-force option now looks
  disproportionate.

### Outstanding action (small, but do it before the seeding run)

**The token is not yet stored anywhere.** It was proven live from the shell and nothing was
persisted. Before the seeding run it goes in .NET user-secrets (`dotnet user-secrets set`)
or an untracked local file — **never a commit**, and never in `appsettings.json`, which is
tracked. If it leaks, revoke and regenerate at `https://boardgamegeek.com/applications`.

Note that if seeding is genuinely build-time-only, the token never needs to reach the app's
configuration at all — it can live entirely in the one-off seeding script's environment.
Which of those two it is falls out of ticket 05.
