---
status: accepted
---

# In-memory stays the default Provider; cloud Providers are selected at deployment

MeepleLedger is gaining Cosmos DB, PostgreSQL and Redis Providers so that the AI-200 material can be
practised against a domain we already know. The obvious move would be to retire
`InMemoryMeepleStore` once a real store exists, but two things depend on the app running with no
external dependency at all: the README's promise that the app runs straight after a clone, and the
rehearsed offline demo tagged `demo-ready` by ticket C-17. So `InMemoryMeepleStore` and the
brute-force in-memory Search Index remain the **default** Provider, and every cloud Provider is
registered alongside them and chosen by configuration.

## Consequences

The switch is worth more than the compatibility it preserves. Choosing a Provider at deployment time
is exactly what Azure App Configuration exists for, so the project now has a genuine reason to use
it rather than a contrived one — and that is a skill the exam measures directly. The cost is that
every Provider must satisfy the same seam honestly: the moment a screen needs to know which
Provider is live, the seam has leaked and the default has stopped being a real fallback.
