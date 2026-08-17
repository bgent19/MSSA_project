# 11 — The storage seam: two interfaces, in-memory implementations, DI

**What to build:** The app can hand its screens a fully-built catalog, collection and play log, from
one place, registered in the container — so that swapping in a real database later touches two files
instead of the whole app.

**Blocked by:** 06 — `Play`, `PlayerResult` and `PlayLog`; 09 — Emit the owned-collection seed;
10 — Emit the play history

**Status:** ready-for-brett

The whole seam, in full:

```csharp
public interface IGameCatalogSource  { GameCatalog Catalog { get; } }        // the world
public interface IMeepleStore        { GameCollection Collection { get; }    // mine
                                       PlayLog        PlayLog    { get; } }
```

Two interfaces, five members, **zero methods**. Both registered `AddSingleton`. Both sync.

This is the cleanest instance of interfaces, encapsulation and dependency injection in the app —
three named rubric items in one place.

- [ ] Both interfaces exist exactly as above — no methods, no `Save`
- [ ] A seeded catalog source reads the emitted catalog data
- [ ] An in-memory store builds the collection and play log **in its constructor**
- [ ] The store takes the catalog source as a **constructor dependency**
- [ ] Both are registered `AddSingleton` in `Program.cs`
- [ ] The app starts without throwing
- [ ] `dotnet build` is green; still zero package references
- [ ] Commit

## Watch out for

- **`AddSingleton`, not `AddScoped`. This is the trap.** In Blazor Server, `AddScoped` is scoped to
  the **circuit**, and a circuit dies on refresh. A scoped store means F5 empties the collection in
  front of the examiner — **silently, with no error**. Singleton is also genuinely *correct* while
  there is exactly one user: "the app's data" and "Brett's data" are the same object.
- **The store must take the catalog source as a constructor dependency**, because the seeded
  `OwnedGame`s and `Play`s must reference **the same `Game` instances** the catalog holds. Otherwise
  the catalog and the shelf disagree about what "Catan" is. This is ordinary constructor injection
  between two singletons — and another clean rubric beat.
- **Do not add a `Save` method.** It was deleted, not stubbed. With a singleton holding the
  aggregates in fields, `Collection.Add(x)` has *already* persisted. A no-op you must remember to
  call fails **invisibly today** and turns into silent data loss later.
- **Do not turn this into a repository.** The store's job is to hand over fully-built aggregates; it
  must never see an individual `OwnedGame` or `Play`. The tutorial shape
  (`IOwnedGameRepository.Add/Remove/GetAll`) would relocate the duplicate-title guard and the private
  `Dictionary` out of the domain and into infrastructure — moving the graded artifact out of the
  graded classes.
- **Components will call `Collection.Add(...)` and `PlayLog.Record(...)` directly.** That is
  intended. There is no save step, so there is none to forget.
- **If the app fails to start with a DI exception, it is a seed row violating an invariant** — not
  Blazor being broken. Fix the seeder's emit-time filter and re-emit; do not hand-patch.
- Two things to be able to say out loud: singletons are shared across circuits and so must be
  thread-safe in principle (near-zero risk for one person clicking), and this registration is what
  changes when authentication arrives.
