---
status: accepted
---

# The live Catalog grows at runtime; the compiled Catalog stays fixed

Seed-as-code compiles 200 Games into the app, which is why it runs after a clone, but it means a
Game outside those 200 can only arrive through fetch, emit, commit and deploy. That is fine for a
demo and wrong for an app the owner uses every week. With a durable Provider, the Catalog lives in
the Search Index's store and grows: the owner adds a Game by BoardGameGeek id or search, the app
fetches it from BGG, writes it, and embeds it inline. The compiled `CatalogSeed` stays as the fixed,
offline Catalog of the in-memory Provider (ADR-0001), and it is not regenerated from Games added at
runtime.

## Consequences

The Catalog source gains a write path, the BGG token becomes a production secret, and embedding
moves from the Seeder and the bus into the web app for this one case. A Game added at runtime is
not in the compiled Catalog, so the two Catalogs drift by design; nothing should try to keep them
in step.
