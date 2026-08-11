# Decide the project and folder structure

Type: grilling
Status: open
Blocked by: 06 (closed)
Blocks: 08

## Question

Does MeepleLedger stay one web project with folders, or split into a domain class library
plus a web project — and either way, what are the folders called?

Graduated from the map's **Not yet specified**, which said to revisit this "once
[Design the storage seam](06-storage-seam.md) shows how many moving parts there actually
are." It now does, and they are countable:

- **~8 domain files** — `Game`, `OwnedGame`, `Condition`, `GameCatalog`, `GameCollection`,
  `Play`, `PlayerResult`, `PlayLog` (from
  [Model the domain](02-domain-model.md))
- **4 storage files** — `IGameCatalogSource`, `IMeepleStore`, `JsonGameCatalogSource`,
  `InMemoryMeepleStore`
- **1 seed data file**, plus whatever screens
  [Prototype the screens and the demo click path](07-prototype-screens.md) settles

That is comfortably enough to make flat-in-the-project-root wrong, and enough to make the
class-library question real rather than theoretical.

To settle:

- **One project or two?** A `MeepleLedger.Domain` class library is easier to reuse from an
  Azure Function or a chatbot in a later map, and it makes the dependency direction
  *structurally* enforced — the domain literally cannot reference Blazor. It costs a
  `.csproj`, a project reference, and some namespace churn on sprint day. One project costs
  nothing now and can be split later. **What is the actual cost of splitting later?**
- **Which folders, named what?** `Domain/` and `Storage/` were assumed throughout
  [Design the storage seam](06-storage-seam.md) — confirm or replace them. Does the seed
  JSON live in `wwwroot/`, in a `Data/` folder, or as an embedded resource? (`wwwroot` is
  publicly served, which may or may not matter.)
- **Namespaces.** Do they follow folders (`MeepleLedger.Domain`, `MeepleLedger.Storage`)?
  If so, `_Imports.razor` should carry the `@using` lines so components don't repeat them.
- **Does the structure show up in the presentation?** "Where does this class live and why"
  is a fair examiner question, and a tidy structure is cheap evidence of design intent.
- **Rubric check.** The guidelines grade fundamentals, not architecture — so this should be
  decided in minutes, not debated. Bias toward whatever costs the least sprint time while
  staying explicable.

Note the sprint-day sequencing constraint: this is one of the first things done in hour one,
because moving files after the fact costs more than placing them right.
