# 17 — Rehearse the six-step click path and tag `demo-ready`

**What to build:** The demo runs clean, end to end, on the machine and display you will actually
present from — and the working state is tagged so it can be recovered instantly.

**Blocked by:** 02 — Make the `GameCollection.Add` slide; 16 — Styling, hard time-box

**Status:** ready-for-brett

**Do this adjacent to the presentation**, not days before. Rehearsal has to happen on the same
machine and the same display, immediately before presenting.

The six-step click path:

1. Open the app — populated Collection
2. Search
3. Filter by player count
4. Log a play of a game you **don't own**
5. It appears on top of the Play Log with a **"not owned" badge**
6. Statistics moves *(if Statistics was built — otherwise the path ends at step 5)*

- [ ] The six-step path runs clean, end to end, without a stumble
- [ ] It was run on the presentation machine and the presentation display
- [ ] The `GameCollection.Add` slide displays correctly on that display
- [ ] **The revert command has been typed once**, for real, during rehearsal
- [ ] The working state is committed and tagged `demo-ready`
- [ ] You can state the through-line — *the domain model is the app* — in one sentence

## Watch out for

- **Run the revert command once during rehearsal.** On stage it then becomes a command you have
  already typed, rather than a `git log` excavation under pressure. This is the whole reason the
  pre-emit commits exist.
- **A dead click gets a one-sentence diagnosis and no pause.** It is almost certainly a missing
  `@rendermode InteractiveServer`. Say what it is, move on, do not debug in front of the room.
- **A restart lands on a populated app**, because the store seeds in its constructor. If something
  goes badly wrong, restarting is a recovery, not a loss.
- **The spine's proof is seeded**, so if the live write in step 4 fails, the badged rows are already
  on screen from the seed data. Step 5 still proves the point.
- **The presentation slot length is still unknown.** The talk is a 7-minute core with two marked
  optional beats extending to ~12. Find out the real number if you can; the talk survives not
  knowing, because cuts are made by dropping marked sections rather than improvising.
- **Statistics may not exist.** It is an if-ahead item. If it wasn't built, the click path ends at
  step 5 — which is where the point is proven anyway.
