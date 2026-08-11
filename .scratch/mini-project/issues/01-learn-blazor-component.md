# Learn how a Blazor component works

Type: research
Status: closed
Assignee: claude (wayfinder session, 2026-08-10)
Blocked by: —

## Question

What does Brett need to understand about Blazor before the sprint starts, and can it be
written down as a primer short enough to actually read?

Brett has solid C# OOP but has never built a .NET web app. The sprint budget cannot absorb
learning the framework *and* building the app, so the learning happens here, before the
clock starts.

The primer should answer, in terms a console-app programmer already has words for:

- What a `.razor` component actually *is* — how markup and C# live in one file, and what
  the compiler turns it into.
- How `@onclick` reaches an ordinary C# method, and why the field it mutates is still there
  on the next click (the SignalR circuit, and what "InteractiveServer" means).
- Rendering: when does the UI update, why does it sometimes not, and what `StateHasChanged`
  is for.
- `[Parameter]` — passing data into a child component, the Blazor equivalent of a
  constructor argument.
- Dependency injection: `@inject` and `builder.Services.AddScoped<T>()`, since the storage
  seam will arrive this way.
- The handful of files a Blazor Web App template generates and which ones matter.
- The three or four mistakes beginners reliably make (async event handlers, mutating a list
  without re-render, `@bind` versus `@onchange`).

Deliberately excluded: forms validation, routing beyond `@page`, JS interop, render modes
other than `InteractiveServer`.

Deliverable: a markdown primer saved in the repo and linked from the answer. Prefer the
official Microsoft Learn Blazor docs for .NET 10 as the source; note where older tutorials
would mislead (the pre-.NET 8 "Blazor Server App" template naming).

## Resolution

**Yes — and it fits in one sitting.** The primer is written and lives at
[research/blazor-primer.md](../research/blazor-primer.md) (~2400 words of prose plus code;
this establishes `research/` as where this map keeps research assets). Every substantive
claim is cited to Microsoft Learn for .NET 10.

The load-bearing answer, in one line: **a `.razor` file compiles to an ordinary C# partial
class deriving from `ComponentBase`**, and with `InteractiveServer` that object lives in
server memory attached to a SignalR circuit — so a click is a socket message invoking a
method on a live object, and the field it mutates is still there next click. Brett's
console-app intuition holds; that was the bet behind choosing Blazor Server while charting,
and the docs confirm it.

Covered as scoped: what the compiler generates, `@onclick` and the circuit, the four
auto-render triggers and when `StateHasChanged` is needed, `[Parameter]` (plus
`EventCallback` for child-to-parent), DI, the template's files with the two key `Program.cs`
lines, and the four reliable beginner traps. Excluded as scoped: forms validation, routing
beyond `@page`, JS interop, other render modes.

### Facts later tickets depend on

- **`AddScoped<T>` in Blazor Server is scoped to the *circuit*, not to an HTTP request** —
  one instance per browser tab, surviving navigation, dying on refresh. This is the exact
  lifetime an in-memory store will have, so it is a direct input to
  [Design the storage seam](06-storage-seam.md). Singleton = shared across all tabs and
  must be thread-safe; transient is useless for a store, and disposable transients leak for
  the life of the circuit.
- **DI double-instance trap:** injecting a service from the top-level
  `Components/_Imports.razor` resolves **two** instances in page components, because
  `App.razor` always renders statically. Fix is a second `_Imports.razor` under
  `Components/Pages/`. Also relevant to the storage seam.
- **Prerendering is on by default** for interactive render modes, so `OnInitializedAsync`
  runs **twice**. Bites any store-backed page; worth remembering on sprint day.
- **.NET 9/10 also support constructor injection** in component code-behind, including
  primary constructors — not just `@inject` / `[Inject]`.
- **Template file list on .NET 10** adds `NotFound.razor` and `ReconnectModal.razor`
  versus .NET 8/9 — a small confirmation for
  [Verify the toolchain end to end](04-verify-toolchain.md) that the scaffold output will
  not match .NET 8 screenshots exactly.

### Caveat on one claim

The ticket asked to cover "`@bind` versus `@onchange`". The primer covers it, but flags
inline that **no doc page states using both on one element is an error** — it follows from
`@bind` expanding to `value` + `@onchange`, and the primer says so rather than asserting a
rule the docs don't. The documented escape hatches are `@bind:after`, `@bind:event`, and
`@bind:get`/`@bind:set`.

No fog graduated and no new tickets: this ticket sharpened *how* to build, not *what*.
