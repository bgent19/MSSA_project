# Design the storage seam

Type: grilling
Status: closed
Resolved: 2026-08-11
Assignee: claude + Brett (wayfinder session, 2026-08-11)
Blocked by: 02

## Question

What is the interface between the app and its stored data, such that swapping today's
in-memory or JSON storage for EF Core in map two touches as little code as possible?

This is the ticket that makes deferring the database *cheap*. Deferring is only free when
the seam is designed; otherwise map two is a rewrite. It is also, conveniently, the single
best rubric evidence available — interfaces, encapsulation, and dependency injection in
one place.

Blocked on [Model the domain: games, collections, and plays](02-domain-model.md), since a
repository interface is shaped by the aggregate it stores.

**Unblocked.** [Model the domain](02-domain-model.md) is closed and settled **three**
aggregates, which is the direct input here: `GameCatalog` (read-mostly, seeded),
`GameCollection` (mutable, `Dictionary`-backed), and `PlayLog` (append-mostly, owner-bound).
They have genuinely different access profiles, so "one interface or three" is a live
question rather than a formality. Note also that the aggregates already encapsulate their
own state behind read-only views — the seam should not hand out the mutable innards the
domain classes work to hide.

To settle:

- How many interfaces? One `ICollectionStore`, or a repository per entity? Fewer, deeper
  interfaces are usually better than many shallow ones — see `/codebase-design`.
- What methods, exactly? Method-by-method, because every one is a commitment. Resist
  designing for the database we don't have yet.
- Sync or async? EF Core is async-first, so a sync interface today means changing every
  signature later. Async over an in-memory list looks slightly silly but costs nothing —
  worth deciding deliberately rather than by accident.
- Does the interface return domain objects, or something else? Does it hand out mutable
  lists that callers can corrupt?
- What is today's implementation — in-memory only, or JSON persisted to disk so data
  survives a restart? Restart-survival matters for a demo; file I/O costs time and adds
  failure modes.
- How is it registered and injected? Scoped, singleton, or transient — and what does that
  actually mean in Blazor Server, where a circuit lives for a whole browser session? This
  is a real trap worth understanding rather than copying.
- What does the map-two swap to EF Core concretely touch? Name the files. If the answer is
  more than two or three, the seam is wrong.

## Resolution

**Two interfaces, five members between them, and not one method.** The whole seam:

```csharp
// Storage/IGameCatalogSource.cs
public interface IGameCatalogSource          // the world
{
    GameCatalog Catalog { get; }
}

// Storage/IMeepleStore.cs
public interface IMeepleStore                // mine
{
    GameCollection Collection { get; }
    PlayLog        PlayLog    { get; }
}
```

```csharp
// Program.cs
builder.Services.AddSingleton<IGameCatalogSource, JsonGameCatalogSource>();
builder.Services.AddSingleton<IMeepleStore, InMemoryMeepleStore>();
```

Components inject the interfaces and call domain methods directly — `Collection.Add(...)`,
`PlayLog.Record(...)`. There is no save step, so there is none to forget.

### The seven decisions, in the order they were taken

**1. Persistence port, not repository — the aggregates stay the owners.** The store's job is
to hand over a fully-built aggregate; it never sees an `OwnedGame` or a `Play`. The rejected
alternative was the tutorial shape (`IOwnedGameRepository.Add/Remove/GetAll`), which would
have relocated the duplicate-title guard and the private `Dictionary` — the model's best
encapsulation and data-structure evidence — out of the domain and into an infrastructure
class. Accepted cost, stated up front: this is *not* how EF Core wants to be used, so map two
may redraw the seam rather than slot into it.

**2. Split the catalog off from the mutable data.** `GameCatalog` differs from its siblings on
four axes at once — read-only vs mutable, seeded vs user-created, still-a-file-forever vs
replaced-by-EF, shared vs personal — and `SaveCatalog` never appeared in any draft. Two
interfaces that change for different reasons, rather than one that changes for both. The
payoff shows up immediately in decision 4.

**3. `AddSingleton` for both — the trap the ticket warned about.** `AddScoped` in Blazor Server
is **per circuit**, and a circuit dies on **refresh** (see
[Learn how a Blazor component works](01-learn-blazor-component.md)). A scoped store means F5
empties the collection in front of the examiner, silently and with no error. Singleton is
also *correct* today rather than merely convenient: with no auth there is exactly one user,
so "the app's data" and "Brett's data" are the same object. Two caveats to be able to say out
loud in the presentation: singletons are shared across circuits and so must be thread-safe in
principle (near-zero risk for one person clicking, not worth sprint time on locking); and this
is the registration that changes when auth arrives — likely as `Collection(string owner)`,
an interface change, not just a lifetime one.

**4. In-memory, seeded at startup — not JSON to disk.** Singleton already buys survival across
navigation, refresh, and a second tab; JSON's only added row is *app restart*, and the demo
runs in one sitting. JSON also costs more here than it looks: `System.Text.Json` can't touch
the private `_games` / `_plays` fields and can't rebuild through the guard clauses, so
persisting means a DTO layer plus two-way mapping — the encapsulation is precisely what makes
it expensive — along with new live-demo failure modes (locked file, malformed JSON, wrong
path). The one real objection to in-memory is answered by **seeding**: three or four owned
games and two or three plays in the constructor, so a mid-demo restart returns to a populated
app rather than a blank screen. Note the catalog is unaffected either way, since `Game` is a
plain data type that deserializes cleanly — decision 2 paying off. **Stretch goal:**
`JsonMeepleStore` is one new class and one changed line, and is the single most demonstrable
proof that the seam works — worth saying in the presentation whether or not it gets built.

**5. Sync, not async.** Both halves of the usual argument were rejected. "EF is async-first"
overstates it — EF fully supports `SaveChanges()`/`ToList()`, and async's thread-pool benefit
is invisible at one user. "Async costs nothing" understates it — every handler becomes
`async Task`, next door to the trap that an exception in an `async void` handler **kills the
circuit** untracked, and there is no I/O to await behind an in-memory field anyway. Cost
accepted knowingly: if map two goes async, four signatures and ~6 call sites change, with the
compiler pointing at each. The counter-case that was weighed and declined: paying for async
deliberately as CV/learning value.

**6. No `Save` methods — the interface is properties.** With a singleton holding the aggregates
in fields, `Collection` returns the same instance every call, so `collection.Add(x)` *has
already persisted*. `SaveCollection` would be an empty body, called after every mutation,
doing nothing — exactly the "designing for the database we don't have yet" the ticket warned
against. A no-op that must be called is worse than no method, because forgetting it is
**invisible today** and surfaces as silent data loss in map two. Reinforcing this: map two's
EF store can't be a singleton (`DbContext` isn't thread-safe), so lifetime, shape and method
set all move together anyway — pre-placing `Save` calls buys very little of that transition.
Also rejected: moving mutation onto the store (`Store.AddToCollection(...)`), the only option
where saving can't be forgotten, but it walks back decision 1 — the interface would grow with
the domain and components would stop calling `Add`/`Record` directly.

**7. Keep the interfaces; don't register the aggregates directly.** `AddSingleton<GameCollection>()`
with `@inject GameCollection` works today and was considered honestly. Rejected because
(a) `IMeepleStore` **is** the seam — remove it and deferring the database stops being cheap,
which is this ticket's entire reason to exist; (b) something must *construct* the aggregates —
`PlayLog` needs an `OwnerName`, both need seed rows — and doing that in DI turns `Program.cs`
into the seeding logic, where `InMemoryMeepleStore`'s constructor is its honest home;
(c) interfaces, encapsulation, and DI are named rubric items and this is their cleanest
instance in the app.

### One wrinkle to expect on sprint day

`InMemoryMeepleStore` seeds `OwnedGame`s and `Play`s that reference `Game` objects, and those
must be **the same instances** the catalog holds — otherwise the catalog and the shelf
disagree about what "Catan" is. So `InMemoryMeepleStore` takes `IGameCatalogSource` as a
constructor dependency and seeds from it. Ordinary constructor injection between two
singletons, and another clean rubric beat.

### What the map-two swap concretely touches

**Two files.** `EfMeepleStore.cs` (new) and one registration line in `Program.cs`. Zero
component changes, zero domain changes, and `IGameCatalogSource` is not opened at all. The
ticket's own test — "more than two or three and the seam is wrong" — passes.

Stated without varnish: map two must also change that registration to scoped, and a scoped
store dies on refresh, so map two will likely reintroduce `Save` and revisit this shape. **What
the seam actually protects is the domain model and the components** — which is the part worth
protecting. It is not a promise that map two is free.

### Handed downstream

- **The seed data's *content* is not decided here.** This ticket fixes the seam's shape and
  where seeding lives; what goes in the file belongs to
  [Choose the game data source and seeding strategy](05-choose-data-source.md).
- **Fog graduated.** The moving parts are now countable — ~8 domain files, 4 storage files, a
  seed file — which sharpens the map's repo-structure question into
  [Decide the project and folder structure](10-solution-structure.md).
