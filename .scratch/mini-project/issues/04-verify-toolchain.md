# Verify the toolchain end to end

Type: task
Status: in progress
Assignee: claude + Brett (wayfinder session, 2026-08-11)
Blocked by: —

## Question

Nothing to decide — this is manual work that must happen before sprint day, so that the
first hour of the sprint is not spent discovering the environment is broken.

Prove, on Brett's actual machine, that the whole loop works:

- `git init` the repo, add a .NET `.gitignore`, make a first commit. (No remote required
  yet; decide whether to push to GitHub.)
- Scaffold a Blazor Web App with the `InteractiveServer` render mode on .NET 10 and pin
  down the exact command and flags, so sprint-day Brett types one line and it works.
- Run it. Confirm it serves a page in the browser.
- Confirm hot reload works — this materially changes the feel of the sprint.
- Confirm debugging works: set a breakpoint in an event handler, click the button, and
  verify it is hit. Brett already debugs confidently; the point is proving the web
  workflow behaves like the console one they know.
- Note the .NET version, template name, and any warnings, since tutorials written before
  .NET 8 use different template names.

Scaffolding is ceremony, so Claude may drive it per the coaching contract — but Brett
should watch and run it themselves at least once, because "it works on my machine" needs to
mean *their* machine.

The answer records: the exact scaffold command, where the repo lives, and anything that
surprised us. Later tickets depend on those facts.
