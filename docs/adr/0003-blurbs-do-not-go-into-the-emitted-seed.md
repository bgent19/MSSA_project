---
status: accepted
---

# Blurbs are a data file, not emitted C#

Every other piece of seed data in this project is emitted as compiled C# into `MeepleLedger/Data/`,
and that convention has earned its place — it is why the app runs after a clone with no database.
Blurbs break it. Two hundred BoardGameGeek descriptions is on the order of 300 KB of prose, which
would take `CatalogSeed.cs` from 211 lines to something no one can open, diff or review, in service
of text that exists only to be embedded and then searched from a store. So the seeder writes Blurbs
to a gitignored working file under `data/`, and the Catalog seed keeps carrying structured facts
only.

## Consequences

A Game's Blurb is therefore optional in the domain and absent in the default offline Provider, which
is the honest shape: the in-memory Provider can rank by structured facts but cannot do semantic
retrieval, and the app must degrade gracefully rather than pretend. This is also the first real
pressure toward putting the Catalog in a data service, which is the lesson the exam is teaching.
