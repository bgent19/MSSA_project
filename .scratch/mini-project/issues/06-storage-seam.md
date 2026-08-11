# Design the storage seam

Type: grilling
Status: open
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
