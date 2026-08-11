# Confirm BGG API access is a working token

Type: task
Status: open
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
