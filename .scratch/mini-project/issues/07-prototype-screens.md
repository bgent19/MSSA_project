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

Link any prototype artifacts from the answer.
