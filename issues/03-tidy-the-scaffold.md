# 03 — Tidy the scaffold into Domain, Storage and Data

**What to build:** The already-scaffolded Blazor app is cleared of template clutter and organised
into the three folders the rest of the work assumes, so that every later ticket has an obvious place
to put its files. The app still builds green and still serves a page.

**Blocked by:** None — can start immediately.

**Status:** ready-for-brett

The app was already scaffolded with `dotnet new blazor -n MeepleLedger -int Server -au None`, which
is the exact confirmed line — `Program.cs` is ~27 lines with **zero package references**, and it
should stay that way. What is missing is structure.

This is the one ticket that is pure ceremony rather than graded code.

- [ ] `Domain/`, `Storage/` and `Data/` folders exist under the web project
- [ ] Namespaces follow the folders (`MeepleLedger.Domain`, `MeepleLedger.Storage`,
      `MeepleLedger.Data`) and **never mention the project name**
- [ ] `_Imports.razor` gains exactly two `@using` lines — Domain and Storage
- [ ] The default template pages (Counter, Weather) are deleted
- [ ] The startup project is pinned explicitly to the web app, not the seeder
- [ ] `dotnet build` is green with 0 warnings and still zero package references
- [ ] The app runs and serves a page in a browser

## Watch out for

- **`MeepleLedger.Data` is excluded from `_Imports.razor` on purpose.** It looks like an oversight
  and it is not. The confusing Razor error that a third `@using` would prevent can only fire when a
  component reaches for seed data directly — which is exactly the thing that should not compile.
- **The namespace choice is load-bearing, not cosmetic.** Because no namespace mentions the project,
  splitting `Domain/` into its own class library later is a pure file move: every `using` and every
  namespace declaration stays byte-identical. That is the entire reason a class library was declined
  now.
- **Visual Studio may launch the seeder instead of the web app.** Known, accepted risk from the
  structure decision. Set the startup project explicitly here so it never surprises you later.
- **Take five minutes to run it and look at it.** The next several tickets end with nothing on
  screen; this is deliberate, but it costs morale. This is the scheduled look-at-it moment.
