# Decide the project and folder structure

Type: grilling
Status: closed
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

## Resolution

**One project, three new folders, namespaces that follow them.** The whole structure:

```
MSSA_project.slnx            both projects listed
MeepleLedger/                the web app (already scaffolded and committed)
  Domain/                    ~8 files  -> namespace MeepleLedger.Domain
  Storage/                   4 files   -> namespace MeepleLedger.Storage
  Data/                      2 files   -> namespace MeepleLedger.Data  (generated)
  Components/                as scaffolded: Pages/, Layout/, _Imports.razor
MeepleLedger.Seeder/         console tool -> namespace MeepleLedger.Seeder
```

### One project, not two — because the split-later cost is near zero

A `MeepleLedger.Domain` class library buys one real thing: the dependency direction becomes
**structurally** enforced, since the domain cannot reference what it has no reference to. In
one project only discipline prevents a domain class from reaching into a component.

Declined anyway, because **folder-mapped namespaces make the later split a pure file move**.
`Game.cs` lives in `MeepleLedger/Domain/` with namespace `MeepleLedger.Domain`; splitting
later means creating a class library of that name, dragging 8 files in, and adding a project
reference — and **every `using` and every namespace declaration stays byte-identical**,
because no namespace ever mentioned the project. There is no churn to defer.

That makes the class library a cost paid in hour one for a mistake unlikely across 5 hours of
solo work on 8 files. The rubric grades classes and OOP design, not project topology, and a
`Domain/` folder shows design intent to an examiner just as well as a `.csproj` does.

**The namespace choice is therefore load-bearing, not cosmetic** — it is the entire reason
one project is safe. Flat namespaces would make this decision wrong retroactively.

### Folder names

`Domain/` and `Storage/` are **confirmed, not replaced** — they are the names every decision
in [Design the storage seam](06-storage-seam.md) was written in, and renaming now would make
several closed tickets read wrong for no gain.

`Data/` is kept **separate from `Storage/`** even though that is where the seed is consumed.
The line is *generated files you never open* vs *code you wrote*: an examiner clicking into
`Storage/` sees four hand-written files rather than four buried under a 200-line wall of game
titles. It also hands the presentation a free line — "that folder is generated, I don't touch
it" — which is exactly the framing [the demo narrative](10-demo-narrative.md) wants for its
API-as-decision beat.

### Two stale premises in this ticket's own body, corrected

- **The seed is not JSON.** [Choose the data source](05-choose-data-source.md) settled on a
  C# `static readonly List<Game>` precisely so it cannot fail at runtime. That kills all three
  options this ticket posed: `wwwroot/` (publicly served), a `Data/` JSON file, and embedded
  resources are all irrelevant to compiled source. The only live question was which folder the
  generated `.cs` sits in — `Data/`.
- **`JsonGameCatalogSource` does not exist.** The seam is in-memory seeded in the constructor,
  so that file is `SeededGameCatalogSource`. Folder count unchanged.

### The seeder is a second .csproj, and that is not a contradiction

"One project" means *the app* is not split into layers. The seeder is a build-time tool, not a
layer. It sits at **`MeepleLedger.Seeder/`, a sibling** — nesting it under `MeepleLedger/`
would make the web project's globbing try to compile it, which is a real build error, not a
style preference.

**It needs no project reference in either direction.** It parses XML and writes out C# *source
text*; it never constructs a `Game`. The types it names exist only once the emitted file
compiles inside the web project. The dependency between the two projects is a text file on
disk.

**Listed in the `.slnx`** (one line, new XML solution format) so it opens in Visual Studio
alongside the app — the point of ticket 05 committing it rather than throwing it away is that
the LINQ-to-XML parsing is graded code, and code an examiner cannot open is not evidence.

Accepted risk, named: with two projects in the solution, **Visual Studio can launch the wrong
startup project**, giving a console window hitting an API that is not there. Check the startup
dropdown once during setup.

### Namespaces

| Location | Namespace |
|---|---|
| `MeepleLedger/Domain/` | `MeepleLedger.Domain` |
| `MeepleLedger/Storage/` | `MeepleLedger.Storage` |
| `MeepleLedger/Data/` | `MeepleLedger.Data` |
| `MeepleLedger.Seeder/` | `MeepleLedger.Seeder` |

**File-scoped declarations** — `namespace MeepleLedger.Domain;`, no braces. Saves an
indentation level on every domain class, which matters when `GameCollection.Add` goes on a
slide and the invariant has to be readable from the back of the room. It is also Visual
Studio's default for new files in .NET 10, so it is the zero-effort path.

**Add exactly two `@using` lines** to `Components/_Imports.razor`, which already carries the
scaffold's own namespaces:

```razor
@using MeepleLedger.Domain
@using MeepleLedger.Storage
```

**`MeepleLedger.Data` is deliberately excluded.** The argument for including it was that a
missing `@using` surfaces as a Razor error reading nothing like a normal C# "type not found" —
bad to debug in hour two. But that error can only fire when a component reaches for seed data,
and a component reaching for seed data is precisely what should not compile. There the error
is the seam doing its job, at zero cost. Components read through `IMeepleStore`.

### The structure gets no presentation beat, but a rehearsed Q&A answer

[The demo narrative](10-demo-narrative.md) is a 7-minute core with its optional beats already
marked and the loops gap already costing a clause. A folder tour would be the weakest thing in
it — it shows organization, not skill, and the rubric grades neither. Forty seconds on
directory names dilutes a talk whose thesis is "the domain model is the app".

But "where does this class live and why" is a fair examiner question with a one-sentence
answer:

> *"Domain is my model, Storage is the seam behind it, Data is generated and I never open it —
> three folders, and the namespaces follow them so the domain can lift out into its own library
> without touching a single `using`."*

That is strong because it is the same content as the spine rather than a second topic — and it
lands the enforced-dependency-direction point we declined to pay for structurally, for zero
sprint minutes. It is Q&A prep, not a constraint on the sprint plan.

### Handed downstream

- [Run the seeding pipeline](11-run-seeding-pipeline.md) — the seeder project's exact location,
  name, solution membership, and the no-project-reference finding.
- [Write the hour-by-hour sprint plan](08-sprint-plan.md) — hour one creates three empty
  folders and edits one file (`_Imports.razor`); this is minutes, not a task worth budgeting.
  Also carries the startup-project check.
