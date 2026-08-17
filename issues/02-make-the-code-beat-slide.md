# 02 — Make the `GameCollection.Add` slide

**What to build:** One presentation slide showing the `GameCollection.Add` method, ready to display
during the talk's code beat — so that the code moment is a slide rather than live Visual Studio.

**Blocked by:** None — can start immediately. The method's shape is already fixed by the spec, so
this does not wait on the code being written.

**Status:** ready-for-brett

The demo narrative fixed this as *the* code beat, and fixed that it is a slide. No IDE on screen.
The method is the best single piece of evidence in the app: it shows a guard clause (branching), a
`Dictionary` keyed by title (data structure choice with a reason), encapsulation of private state,
and a stated business rule, all in a few lines.

- [ ] One slide exists, showing the `GameCollection.Add` method
- [ ] The code is legible at presentation distance on the display that will actually be used
- [ ] No IDE chrome, no file tree, no line numbers that invite scrolling
- [ ] You can say what the method proves in one sentence without reading the slide

## Watch out for

- **Do not plan to show this live in Visual Studio.** Live editor state is a failure mode with no
  upside; a slide has neither.
- **No single file shows the relationships *between* classes**, so do not go looking for a second
  slide that does. That job is carried by a spoken structural sentence over the running app —
  *the catalog is the world, the collection is my shelf, the log is my history* — not by more code.
- If the domain model already exists by the time you make this, screenshot the real file rather than
  retyping it, so the slide cannot drift from the code.
