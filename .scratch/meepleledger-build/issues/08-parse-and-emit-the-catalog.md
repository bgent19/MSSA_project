# 08 — Parse the XML and emit the catalog seed

**What to build:** The seeder turns the saved raw XML into committed C# source — a
`static readonly List<Game>` of ~75–200 games in the `Data/` folder — so that the app opens with a
catalog and never opens a socket.

**Blocked by:** 04 — `Game`, `OwnedGame` and `Condition`; 07 — Fetch the game data from BGG

**Status:** ready-for-brett

**Commit before you run the emit.** This commit is load-bearing, not hygiene: the pre-decided
response to a seed file that will not compile is *revert the generated file and re-emit*, and that
response only exists if the pre-emit state is committed. Never hand-patch 200 lines of generated
code under time pressure, and **do not substitute a stash** — a stash is precisely the thing you
lose track of later.

The parsing is ~20 lines of LINQ-to-XML with no NuGet package. That is deliberate: a client library
would take your code off the page, and the rubric marks your code.

- [ ] Parsing uses LINQ-to-XML with **zero package references**
- [ ] Exactly the five `Game` properties are extracted — everything else is discarded
- [ ] The emit-time filter **drops any game with `maxplayers < 1`**
- [ ] Output is `MeepleLedger/Data/`, namespace `MeepleLedger.Data`, a `static readonly List<Game>`
- [ ] The catalog is **wider than the 28-game shelf** — anywhere in ~75–200 is fine
- [ ] `dotnet build` is green after the emit
- [ ] Commit the generated file; `git status` confirms no token and no raw dump went with it

## Watch out for

- **The XML has three shape gotchas:** values live in `value=` attributes rather than element text,
  the name you want is the one with `type="primary"`, and there may be several names.
- **`Designer` is a `link`, not an element, and 20% of games have more than one.** That is not a
  corner case. **Take the first**, and guard it —
  `.FirstOrDefault()?.Attribute("value")?.Value ?? "Unknown"` — because a few games have no designer
  link at all and `.First()` throws on those. This is exactly the kind of thing that surfaces at game
  147 of 200, after the API calls are already spent.
- **`(Uncredited)` is a real BGG designer value, not a null.** It renders fine. Leave it.
- **Discard `BggId`, `Year`, `Description`, `ImageUrl` and `Categories`.** The domain model has five
  properties and adding a sixth later is a one-line change; an unused property on the graded class is
  dead weight a grader can see.
- **The emit-time filter is required by the shape of the storage seam, not by defensive habit.**
  Because the store is a singleton seeded in its constructor, a bad row does not produce a bad row —
  it throws *inside singleton construction*, so **the app fails to start**, wrapped in a DI
  exception. On stage that reads as "Blazor is broken", not as "row 147 is bad".
- **An internal validation-bypassing load path was considered and rejected.** An invariant you can
  bypass is not an invariant, and it would undercut the talk's entire spine. Filter in the seeder.
- **The generated file lands inside the web project**, so it is compiled with the app. This is why
  ticket 04 had to come first.
