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

**One interface, three members, registered as a singleton — and the aggregates keep doing
the work.**

```csharp
public interface ILedgerStore
{
    GameCollection Collection { get; }
    PlayLog PlayLog { get; }
    void Save();
}
```

That is the entire seam. The catalog sits *outside* it.

### The load-bearing decision: the store hands out aggregates, it is not the collection

Two shapes were on the table, and the choice between them was settled before anything else
because everything else follows from it.

- **Rejected — the store *is* the collection.** `IGameStore.Add(OwnedGame)`,
  `.Remove(name)`, `.GetAll()`; components inject the store and call it directly. This is
  the shape every Blazor tutorial uses, and it **hollows out the graded artifact**. With the
  store owning the rows, `GameCollection` has nothing left to own — the duplicate-title
  guard from [Model the domain](02-domain-model.md) either migrates into the store
  implementation (so it stops being a domain rule and a second implementation could simply
  forget it) or gets written twice. The aggregate withers into a wrapper.
- **Chosen — the store loads and saves whole aggregates.** Components get the *aggregate*
  from the store and call `Collection.Add(...)` on it. Every invariant, every LINQ filter,
  every guard clause stays on the classes Brett wrote. The store's only job is "get it into
  memory" and "make it durable."

The rule this encodes: **the seam persists the domain; it never replaces it.**

### Why the catalog gets no interface

Three aggregates, three access profiles: `GameCatalog` is seeded once and **never written**,
`GameCollection` is mutated, `PlayLog` is appended. Three interfaces of two methods each
would be three *shallow* interfaces — tripling the DI registrations, the injections in every
component, and the map-two surface — for a benefit that is entirely theoretical.

Putting the catalog behind the store would also state a falsehood: it would sit behind a
`Save()` it never calls. The split is already in the product's name — **the ledger is your
data (shelf + history); the catalog is the world, and the world is not yours to save.**

So `GameCatalog` is registered as a plain singleton built from seed data, with **no
interface at all**: exactly one implementation, no I/O, never changes. If map two wants it
from a database, that is one changed registration line.

Considered and rejected: three interfaces would put more interface code on the page, and the
rubric grades interfaces. One interface with a real second implementation arriving in map
two demonstrates the concept completely; three near-empty ones read as cargo-cult rather
than judgement.

### Today's implementation: pure in-memory, re-seeded at startup

Not JSON-on-disk. The deciding argument is **the demo**, not the effort.

[Prototype the screens](07-prototype-screens.md) settled a populated start (~18 games, ~40
plays) whose payoff is one live write filling a *deliberate gap*. In-memory means every run
produces byte-identical state: rehearse ten times, and the eleventh run on stage still has
the gap in it. **Restart is a reset button** — which on a graded stage is a feature.

JSON persistence would make rehearsal writes permanent — practice the demo once and the
deliberate gap is *filled*, requiring a hand-edited file before going on stage, while
nervous. It also buys real failure modes (write permissions, a half-written file, a working
directory that differs between `dotnet run` and Visual Studio) in exchange for preserving
exactly the rehearsal noise you don't want. It is rubric-neutral besides:
`File.WriteAllText` plus `JsonSerializer` is library calls, not Brett's logic.

**Accepted cost, knowingly:** anything typed during the demo is lost on process restart.
(Not on F5 — see the lifetime decision below.)

### `Save()` is an empty method, and stays

```csharp
public void Save() { /* in-memory: the aggregates are the storage */ }
```

The expensive part of a migration is never the implementation class — it is the **call
sites**. EF Core tracks entities and writes nothing until `SaveChanges()`. Omitting `Save()`
today means map two must hunt down every mutation site and add a line: precisely the
"deferring turned out to be a rewrite" outcome this ticket exists to prevent.

The price is bounded and known: **three mutation sites in the whole app** — add a game,
remove a game, record a play. Three extra lines. The comment is what keeps the empty body
legible as design rather than sloppiness.

### Sync, not async

`void Save()`, plain properties, no `Task` anywhere.

The ticket framed async as insurance against "changing every signature later" — the
three-mutation-site count defuses that. Migration means three `Store.Save()` →
`await Store.SaveAsync()` edits plus three handlers becoming `async Task`. An afternoon's
inconvenience, not a rewrite.

Against that, async costs something **on sprint day**. From
[research/blazor-primer.md](../research/blazor-primer.md):

```csharp
private async void Save() { ... }   // ❌ untracked; unhandled exception kills the circuit
private async Task Save() { ... }   // ✅
```

`async void` on an event handler kills the circuit silently — the page stops responding with
no error at all. Synchronous handlers **cannot hit that trap**. Handing a footgun to a
first-time .NET web developer on a five-hour clock, to insure against a three-line future
edit, is a bad trade.

`Collection` and `PlayLog` are **properties, not methods**, because the lookup is genuinely
instant — a property is honest here, and `Store.Collection.Search(term)` reads well. Noted
for map two: if EF loads per request, these become `GetCollectionAsync()`, since a property
that hits a database is a smell.

### Registration: Singleton — and the trap that makes it non-obvious

```csharp
builder.Services.AddSingleton<GameCatalog>(_ => new GameCatalog(SeedData.Games));
builder.Services.AddSingleton<ILedgerStore, InMemoryLedgerStore>();
```

**The trap:** in Blazor Server, `Scoped` does *not* mean per request. It means **per
circuit** — per browser tab — and the circuit **dies on F5**. Copying `AddScoped` from a
tutorial is therefore actively dangerous here.

| Lifetime | What you actually get |
| --- | --- |
| `Scoped` | One store **per browser tab**. Survives page navigation. **Refresh wipes it.** Two tabs are two separate worlds. |
| `Singleton` | One store for the whole process. Survives refresh. Shared by every tab. |
| `Transient` | A fresh empty store per injection — useless; disposable transients also leak for the life of the circuit. |

With `Scoped`, this happens on stage: log the play, see the row, hit F5 to show something —
**the play is gone** and the collection has silently re-seeded to its starting state.

`Singleton` is also *semantically* right, not merely convenient. The no-auth decision makes
the app single-user by definition: there is exactly one shelf and one play log, and they
belong to the process. A shared instance is the model, not a bug.

Two costs held knowingly:

- **Not thread-safe.** One person clicking through a demo will never trip it; two tabs
  mutating concurrently could. Worth naming out loud as a known limitation rather than
  pretending otherwise.
- **It becomes wrong in map two.** EF Core's `DbContext` must never be a singleton, so the
  registration flips to `AddScoped` when the database lands — one line, and correct at that
  point because auth will have made "per user" meaningful.

### What the map-two swap concretely touches

The test of the seam, and it passes:

**Changed — 1 file.**
- `Program.cs` — two registration lines swap (`AddSingleton<ILedgerStore,
  InMemoryLedgerStore>` → `AddScoped<ILedgerStore, EfLedgerStore>`, plus `AddDbContext`).

**Added — 2 files**, purely additive:
- `Storage/EfLedgerStore.cs`
- `Data/MeepleLedgerDbContext.cs` (plus generated migrations)

**Unchanged:** every `.razor` component, all seven domain classes, `ILedgerStore.cs` itself,
and the seed data. `InMemoryLedgerStore.cs` survives as a useful test double rather than
dead weight.

**One honest wrinkle — the swap is cheap, not free.** `GameCollection`'s private
`Dictionary<string, OwnedGame>` does not map to EF Core directly; EF maps collection
navigations, so a `Dictionary` backing field needs either a `List` backing with the
`Dictionary` rebuilt on load, or explicit field mapping. That cost lands on the *domain
class*, not the seam. It deliberately changes nothing today: the `Dictionary` is doing real
rubric work (O(1) duplicate rejection, per
[Model the domain](02-domain-model.md)) and is not being compromised for a database that
does not exist yet. Recorded here so map-two Brett inherits the fact rather than discovering
it.

### Project structure — settled, and the fog item with it

One project, folders not projects:

```
MeepleLedger/
  Domain/     Game, OwnedGame, Condition, GameCatalog, GameCollection, Play, PlayerResult, PlayLog
  Storage/    ILedgerStore, InMemoryLedgerStore
  Data/       SeedData
```

The map's "repo and solution structure for the long haul" fog item said to revisit once this
ticket showed how many moving parts there really are. The answer is **very few** — one
interface, one implementation, one registration — which weakens the case for a separate
class library rather than strengthening it. A domain library can be extracted later by moving
files; nothing here blocks it. That fog item is cleared for this map; map two may reopen it
when an Azure Function or chatbot actually needs to reference the domain.

Per the coaching contract, the folder scaffolding is ceremony Claude may create; every class
inside them is Brett's.

### Rubric coverage this ticket adds

**Interfaces** (`ILedgerStore`), **encapsulation** (the seam refuses to hand out the mutable
innards the aggregates hide), and **dependency injection** (constructor/`@inject` against an
abstraction, with a lifetime choice Brett can *explain*) — the three items landing in one
small, demonstrable place, exactly as the ticket predicted.

### Handed downstream

- To [Choose the game data source](05-choose-data-source.md): the store is constructed at
  **startup**, synchronously, from seed data available in-process. That **rules out a live
  API call at construction time** and reinforces the build-time-seeding incumbent from
  [Survey board game data sources](03-survey-data-sources.md). Seed must satisfy
  `new GameCatalog(...)` plus a pre-populated `GameCollection` and `PlayLog`. Whether it
  lives in a C# static class or an embedded JSON file remains that ticket's call — but JSON
  now costs a deserializer call on the startup path, where a failure takes the whole app
  down rather than one screen.
- To [Write the hour-by-hour sprint plan](08-sprint-plan.md): build order is **domain
  classes → `ILedgerStore` + `InMemoryLedgerStore` → the two `Program.cs` registration lines
  → first component**. The seam is roughly two small files and cannot be cut — it is where
  three rubric items live. Two pre-decided trap responses to sequence: `AddSingleton`, never
  `AddScoped`; and sync event handlers, never `async void`.
- To [Design the demo and presentation narrative](10-demo-narrative.md): "storage is
  in-memory behind an interface, deliberately, so the database swap is one new class and one
  changed line" is a *judgement* line rather than an omission — and the named limitations
  (not thread-safe, restart loses live writes) are stronger evidence of understanding than
  silence. `ILedgerStore` plus its registration is also a strong candidate for the
  code-on-screen moment, being three members long.

No fog graduated into new tickets; one fog item was cleared by being answered outright.
