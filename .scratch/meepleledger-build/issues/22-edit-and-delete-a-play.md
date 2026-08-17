# 22 — Edit and delete a play

**What to build:** Fix a play you logged wrong, or remove one you logged by mistake — so that a typo
is not permanent.

**Blocked by:** 15 — The Play Log, with the per-row "not owned" badge

**Status:** if-ahead — **add-back item 5.** Not baseline sprint work.

Estimated ~30 minutes. **The least interesting item on the add-back list** — it is CRUD completeness
and mostly branching. It is last for that reason.

- [ ] A play in the log can be edited and the change persists
- [ ] A play can be deleted and disappears from the log
- [ ] Editing still enforces the seat invariant
- [ ] Editing still enforces that the play includes you
- [ ] Deleting a play does not touch the collection
- [ ] The page declares `@rendermode InteractiveServer`
- [ ] Commit

## Watch out for

- **Any new mutation belongs on `PlayLog`**, behind the same guards as `Record`. Do not let a
  component reach into the private `_plays` list — that would undo the encapsulation the whole
  project is built to demonstrate.
- **Deleting a play must not touch the collection**, and removing a game from the collection must not
  touch its plays. The shelf and the history stay independent.
- **There is no save step.** The store is a singleton holding the aggregates in fields, so mutating
  the log has already persisted.
- **Consider whether this is the best use of the time.** If you are far enough ahead to reach item 5,
  the game-detail screen (item 3) is worth more to the rubric than this is.
