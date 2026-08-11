# Prototype the screens and the demo click path

Type: prototype
Status: open
Blocked by: 02

## Question

What screens exist, and what is the exact sequence of clicks that makes a convincing demo?

Blocked on [Model the domain: games, collections, and plays](02-domain-model.md) — screens
are views onto the model.

Use `/prototype` to make something cheap and concrete to react to, rather than arguing
about layouts in the abstract. Sketches or throwaway markup are fine; this is not sprint
code and none of it needs to survive.

To settle:

- The screen inventory. Likely candidates: collection list, game detail, log-a-play form,
  play history, some statistics view. Which of these are essential, and which are the
  stretch goals that get cut when hour four arrives?
- The demo click path, start to finish. Walk in, open the app, and what happens? A demo
  that shows an empty app being filled in live is a very different build from one that
  shows a populated collection being explored.
- Where does the *interesting* code surface visually? The rubric wants OOP and data
  structures; a statistics view driven by LINQ over play history makes invisible work
  visible. What else earns its place that way?
- How much styling is worth it? Brett knows HTML/CSS, so this is cheap for them — but it
  is not on the rubric, so it should be time-boxed deliberately.
- What is the minimum screen set that still tells a whole story? That set is the hour-one
  target; everything else is optional.

**Constraint from [Verify the toolchain end to end](04-verify-toolchain.md):** interactivity
is **per-page**. Every screen with a button, a form, or an `@onclick` needs
`@rendermode InteractiveServer` at the top of the `.razor` file. Omitting it renders a
page that looks perfectly correct but whose clicks silently do nothing — no error, no
exception, no breakpoint hit. When settling the screen inventory, mark each screen
interactive or static, so sprint-day Brett types the directive without having to think.

**Constraints from [Choose the game data source](05-choose-data-source.md):**

- **The app opens onto populated data, not an empty state.** ~200 catalog games, ~40 owned,
  ~60-80 seeded plays. The "empty app being filled in live" demo shape named above is
  therefore *off the table* — settle the click path for exploring a populated app, with one
  play logged live to show the write path.
- **A search box filtering the catalog is already a settled component**, and it appears in
  **two** flows — adding a game to the collection, and choosing the game when logging a play.
  Design it once. It is also a rubric-positive surface (LINQ filtering over a collection),
  so it belongs somewhere visible.
- **The seeded play history deliberately contains plays of unowned games.** A screen or view
  that makes that visible is where the domain model's central property — plays independent of
  ownership — becomes demonstrable. Worth a deliberate place in the click path.
- **No manual game entry anywhere.** There is no "add a game not in the list" affordance to
  design; the catalog is closed.
- Statistics driven by LINQ over ~60-80 plays now has real data behind it, which strengthens
  the case for the statistics view being essential rather than a stretch goal.

Link any prototype artifacts from the answer.
