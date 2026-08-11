# Prototype the screens and the demo click path

Type: prototype
Status: closed
Assignee: claude + Brett (wayfinder session, 2026-08-11)
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

Link any prototype artifacts from the answer.

## Resolution

**Four screens, a populated start, and the collection as the landing page.**

Prototype artifact: [prototypes/screens-prototype.html](../prototypes/screens-prototype.html) —
three structurally different variants (A *The Shelf*, 5 screens collection-first; B *The Log*,
4 screens activity-first; C *The Console*, 1 screen no navigation), switchable via `?variant=`
and a floating bar, each with its own screen inventory, click path, hour-one target and cut
list. Throwaway; open it in a browser, no build.

**Chosen: A's structure, B's headline beat folded in, A's game-detail screen cut.**

### The screen inventory

| # | Screen | Render mode | What it carries |
|---|--------|-------------|-----------------|
| 1 | **Collection** *(landing)* | `@rendermode InteractiveServer` | `Dictionary`-backed store, duplicate-title guard, `OwnedGame` + `Condition`, search and player-count filter |
| 2 | **Log a play** | `@rendermode InteractiveServer` | the form; game picker reads the **catalog**, not the collection |
| 3 | **Play Log** | `@rendermode InteractiveServer` | `PlayLog.RecentFirst()`; the live write lands here |
| 4 | **Statistics** | *static* — no directive needed | LINQ showcase; includes the "played but not owned" row |

Every interactive screen is marked so sprint-day Brett types the directive without thinking —
the trap from [Verify the toolchain end to end](04-verify-toolchain.md), where a missing
directive renders a correct-looking page whose clicks silently do nothing.

**Hour-one target: screens 1 and 2.** That pair alone tells a whole story.
**Cut order:** Statistics → Play Log's filtering → Play Log entirely.

### The demo posture: populated, with one deliberate hole

The app **starts full** — roughly 18 games and ~40 plays spanning several months — and the
demo opens on a game played last weekend that isn't logged yet. Both halves in one:
the app reads as real from the first second, and there is still a live write that visibly
moves numbers on two other screens.

An empty start was rejected: it spends the first ninety seconds of a graded presentation
typing, and it cannot show LINQ doing anything interesting, because filtering four rows is
not filtering. Volume is what makes `Search`, `FilterByPlayerCount` and `MostPlayed` look
like work rather than decoration.

### The demo click path

1. Open on a populated collection — 18 games, legible without explanation.
2. Search a designer → the list narrows live.
3. Filter to a player count → narrows again. *(LINQ, on screen)*
4. Log a play: pick from the **catalog** a game that is **not owned** — the café/convention case.
5. Save → Play Log shows it on top.
6. Statistics → the counter moved, and the **Owned? no** row proves plays are independent of ownership.

Steps 4 and 6 are the pair stolen from variant B. They make the model's most consequential
decision — from [Model the domain](02-domain-model.md) — visible in about ten seconds, and
they cost almost nothing to build.

### Why the other variants lost

- **B (activity-first landing)** demotes the collection to a second tab. The collection holds
  the densest rubric evidence on the map — the `Dictionary`, the only enforced invariant a
  user can trip, the `Condition` enum. It should not be behind a click.
- **C (single screen, no routing)** is genuinely tempting on budget, and its cut list is the
  safest because cuts can't break navigation. Rejected because one large `.razor` file is a
  visibly weaker *component* story than four small ones, and an app with no routing risks
  reading to a grader as unfinished. Cutting A's game-detail screen recovers most of C's
  saving anyway.
- **A's game-detail screen** was cut as the fifth screen that mostly re-displayed data
  already seen. Accepted cost: **there is now no home for a per-game win rate.** If hour four
  arrives early, the cheapest place to recover it is an expandable collection row, not a new
  route.

### Styling

Time-boxed and deliberately last. Brett's HTML/CSS is strong, so this is cheap for them and
therefore tempting — but it scores zero on the rubric. It happens only after the cut list is
clear, never before.

### Handed downstream

- To [Choose the game data source](05-choose-data-source.md): the seed must include **plays,
  not just games** (~40 across several months), and the catalog must be broad enough that the
  log form can pick an *unowned* game — that is now a demo beat, not a nicety.
- To [Write the hour-by-hour sprint plan](08-sprint-plan.md): the hour-one target, the cut
  order, and the styling time-box above.
- Graduated from the fog: the demo and presentation narrative is now specifiable as
  [Design the demo and presentation narrative](10-demo-narrative.md).
