---
status: accepted
---

# Three vector stores, one Search Index seam

Cosmos DB for NoSQL, Azure Database for PostgreSQL with pgvector, and Azure Managed Redis all
implement the same `IGameSearchIndex`. Building three implementations of one interface is redundant
for an app that only ever needs one, and we are doing it anyway: the AI-200 exam does not only ask
whether you can run a vector query, it puts you in case studies that ask *which store suits this
workload*. That judgment comes from having felt an RU charge climb, tuned an HNSW build parameter
and watched a Redis key expire — not from a comparison table.

## Considered and rejected

Picking one store and reading about the others. Cheaper and faster, and it would leave the
comparative questions — the ones that carry the most marks in the data-services domain — resting on
recall instead of experience.

## Consequences

The seam has to be defined by what the domain asks of it (an Ask plus a Table, ranked Games out) and
not by any one store's query surface. If `IGameSearchIndex` starts carrying a parameter that only
Cosmos or only pgvector understands, the abstraction has failed and the comparison it exists to
support becomes meaningless.
