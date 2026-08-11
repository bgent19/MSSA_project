# Blazor Primer for a C# Console Programmer

**Target:** .NET 10 Blazor Web App, `InteractiveServer` render mode.
**Audience:** solid C# OOP, zero .NET web experience.
**Sources:** Microsoft Learn Blazor docs only, version selector at .NET 10 (`?view=aspnetcore-10.0`). A few pages carry the same content across .NET 8/9/10 (they're published with shared "monikers"); where that's true it's noted inline.

Excluded on purpose: forms validation, routing beyond `@page`, JS interop, and render modes other than InteractiveServer.

---

## 1. A `.razor` file is a class. Really.

You already know how to write this:

```csharp
public class Counter
{
    private int currentCount = 0;
    private void IncrementCount() => currentCount++;
}
```

A Razor component is that class, plus a method that describes what to draw. The docs are literal about it: components "are generated as C# partial classes" and derive from [`ComponentBase`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.componentbase), which implements the `IComponent` interface ([Razor components overview](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/?view=aspnetcore-10.0)).

`Counter.razor`:

```razor
@page "/counter"
@rendermode InteractiveServer

<h1>Counter</h1>
<p role="status">Current count: @currentCount</p>
<button class="btn btn-primary" @onclick="IncrementCount">Click me</button>

@code {
    private int currentCount = 0;

    private void IncrementCount() => currentCount++;
}
```

At build time the Razor compiler emits a C# class named `Counter` (namespace derived from the folder — a component in `Components/Pages` lands in `BlazorSample.Components.Pages`). Everything in `@code { }` becomes ordinary members of that class. The HTML becomes an override of `BuildRenderTree`, which appends elements, attributes, and text to a `RenderTreeBuilder`. Roughly:

```csharp
protected override void BuildRenderTree(RenderTreeBuilder builder)
{
    builder.OpenElement(0, "h1");
    builder.AddContent(1, "Counter");
    builder.CloseElement();
    // ...button, @onclick wired to this.IncrementCount...
}
```

Two consequences worth internalizing:

- `@currentCount` in markup is not string interpolation into a template. It's a field read inside a method of the same class. Private fields, private methods, `readonly`, `IDisposable` — all normal.
- The component's output goes into a **render tree**, an in-memory representation of the browser's DOM ([overview](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/?view=aspnetcore-10.0)). Blazor diffs the old tree against the new one and sends only the differences to the browser.

If you prefer, put the C# in a code-behind: `Counter.razor` for markup, `Counter.razor.cs` with `public partial class Counter { ... }`. Same class, two files.

---

## 2. How `@onclick` reaches your method, and why the field survives

In a console app, `IncrementCount` runs in your process and `currentCount` sits on the heap until you drop the reference. In InteractiveServer Blazor, that's *still what happens* — the process is just on the server.

The [hosting models doc](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0) states it plainly:

> With the Blazor Server hosting model, components are executed on the server from within an ASP.NET Core app. UI updates, event handling, and JavaScript calls are handled over a SignalR connection using the WebSockets protocol. **The state on the server associated with each connected client is called a *circuit*.**

The full click cycle:

1. Browser loads the page. The Blazor script opens a SignalR (WebSocket) connection to the server. That connection's server-side state is the **circuit**.
2. The server instantiates your `Counter` object *in server memory*, tied to that circuit.
3. You click. The browser sends "event on element #7" over the socket.
4. The server locates the `Counter` instance and invokes `IncrementCount()`. `currentCount` goes 0 → 1.
5. `ComponentBase` re-renders, diffs the render tree, and sends the DOM patch back over the socket. The browser applies it.

So the field is still there on the next click for the most boring possible reason: **it's the same object**. Nobody threw it away. There is no serialization round-trip, no hidden form field, no request/response reset. This is much closer to WinForms than to classic ASP.NET.

**The practical consequence — read this twice.** Circuit state is per-circuit and it dies. The docs enumerate exactly when a *new* circuit is created ([dependency injection, Service lifetime table](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection?view=aspnetcore-10.0)):

- the user closes the browser window and opens a new one;
- the user closes the tab and opens a new one;
- **the user presses reload/refresh.**

Also: "each browser screen requires a separate circuit and separate instances of server-managed component state," and closing a tab or navigating to an external URL is a *graceful* termination that releases the circuit and its resources immediately. Non-graceful drops (network blip) are held for a configurable retention period so the client can reconnect ([hosting models](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0)).

Translation for your app: anything held in a field or in a circuit-scoped service is **session state, not storage**. F5 wipes it. Two tabs are two independent worlds.

---

## 3. Rendering: when it happens by itself, and when it doesn't

`ComponentBase` contains logic that triggers a re-render at these times ([rendering](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/rendering?view=aspnetcore-10.0)):

- After applying an updated set of **parameters** from a parent component.
- After applying an updated **cascading parameter** value.
- After notification of an event and invoking one of its own **event handlers**.
- After a call to its own **`StateHasChanged`**.

Plus: a component *must* render when first added to the hierarchy.

So for the 90% case — button click mutates a field — you write nothing. The [event handling doc](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/event-handling?view=aspnetcore-10.0) says: "Delegate event handlers automatically trigger a UI render, so there's no need to manually call `StateHasChanged`."

**Where it breaks.** `ComponentBase` can only observe a `Task` at two moments: when it's first returned, and when it finally completes. It cannot see intermediate states. So in an async handler with multiple `await`s, you get an automatic render at the start and at the end — not in the middle:

```csharp
private async Task IncrementCount()
{
    currentCount++;              // renders here automatically

    await Task.Delay(1000);
    currentCount++;
    StateHasChanged();           // needed — framework can't know

    await Task.Delay(1000);
    currentCount++;
    // renders here automatically (task completes)
}
```

`StateHasChanged` **enqueues** a re-render "to occur when the app's main thread is free." It does not render synchronously. Calling it five times in a loop produces one render. And within a purely synchronous method there's "no opportunity for the renderer to render the component until after the event handler is finished" — so `StateHasChanged()` in the middle of sync code shows you nothing extra.

The other case that genuinely needs it: **anything outside Blazor's event pipeline.** A `System.Timers.Timer` callback, or a C# event raised by your own state-container service, is invisible to `ComponentBase`. Call `StateHasChanged` yourself. If the callback is off the renderer's synchronization context, wrap it — `StateHasChanged` "can only be called from the renderer's synchronization context and throws an exception otherwise":

```csharp
private void OnTimerCallback()
{
    _ = InvokeAsync(() => { currentCount++; StateHasChanged(); });
}
```

Don't spray `StateHasChanged` everywhere — the docs call that "a common mistake that imposes unnecessary rendering costs."

---

## 4. `[Parameter]` — the constructor argument you don't get to write

You cannot `new` a component. The framework does that. So how do you pass data in? A public auto-property marked `[Parameter]`.

`TodoItemView.razor`:

```razor
<li>@Text</li>

@code {
    [Parameter] public string Text { get; set; } = "";
    [Parameter] public bool Done { get; set; }
}
```

Used from a parent — the attributes are the arguments:

```razor
<TodoItemView Text="Buy milk" Done="false" />
```

Rules from the [components overview](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/?view=aspnetcore-10.0): parameters must be **auto-properties** with no custom `get`/`set` logic, because "component parameters are purely intended for use as a channel for a parent component to flow information to a child component." Put logic in `OnParametersSet` instead. Setting parameters re-renders the child — that's one of the four triggers above.

**Child → parent uses `EventCallback`.** Think of it as a `delegate` field the parent fills in:

```razor
@* Child.razor *@
<button @onclick="() => OnDeleted.InvokeAsync(Text)">Delete</button>

@code {
    [Parameter] public string Text { get; set; } = "";
    [Parameter] public EventCallback<string> OnDeleted { get; set; }
}
```

```razor
@* Parent *@
<Child Text="Buy milk" OnDeleted="RemoveItem" />
```

Per the [event handling doc](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/event-handling?view=aspnetcore-10.0): "To expose events across components, use an `EventCallback`. A parent component can assign a callback method to a child component's `EventCallback`." Use `EventCallback<T>` over a raw `Action`/`Func` — it dispatches to the *parent's* renderer, so the parent re-renders correctly.

Direction of data: parameters flow down, callbacks flow up. Never reach into a child and mutate it.

---

## 5. Dependency injection, and what "Scoped" actually means here

Same `IServiceCollection` you'd use in any .NET host. Registration in `Program.cs`:

```csharp
builder.Services.AddScoped<ITodoStore, InMemoryTodoStore>();
```

Consumption in a component — `@inject` is the normal way:

```razor
@inject ITodoStore Store

<p>@Store.Count items</p>
```

`@inject Type PropertyName` creates a property; "internally, the generated property uses the `[Inject]` attribute." You use `[Inject]` directly only in a code-behind or a custom base class, where there's no Razor directive to write:

```csharp
public class MyComponentBase : ComponentBase
{
    [Inject] protected ITodoStore Store { get; set; } = default!;
}
```

In .NET 9+ Blazor also supports **constructor injection** in code-behind partial classes, including primary constructors ([DI](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection?view=aspnetcore-10.0)). Services themselves always use constructor injection — `@inject`/`[Inject]` "isn't available for use in services."

### Scoped means *circuit*, not *request*

This is the single most important lifetime fact for a Blazor Server app, and the docs are explicit:

> In interactive server-side Blazor apps, **the DI scope lasts for the duration of the circuit** (the SignalR connection between the client and server), which can result in scoped and disposable transient services living much longer than the lifetime of a single component.

> Server-side development supports the `Scoped` lifetime across HTTP requests but **not across SignalR connection/circuit messages**... Scoped services aren't reconstructed when navigating among components on the client, where the communication to the server takes place over the SignalR connection of the user's circuit, not via HTTP requests.

So an `AddScoped<ITodoStore, InMemoryTodoStore>()` gives you: **one store instance per browser tab, alive from page load until refresh/close.** Navigating between pages does *not* reset it. Refreshing *does*.

| Lifetime | What you get | Practical implication for an in-memory store |
| --- | --- | --- |
| `Singleton` | One instance for the whole app process | **Shared by every user and every tab.** Fine for reference data; a bug for per-user data. Must be thread-safe. |
| `Scoped` | One instance per circuit (per browser tab) | **The usual choice.** Per-tab session state. Survives navigation, dies on refresh/close. Two tabs = two stores. |
| `Transient` | A new instance on every resolution | Useless for a store — each component gets its own empty one. Also: transients that implement `IDisposable` are held by the container **for the life of the circuit**, which the docs call out as a memory leak. Avoid disposable transients entirely. |

If you want a service scoped to a single *component's* lifetime rather than the circuit, inherit `OwningComponentBase` and resolve via `ScopedServices` — that creates a DI scope matching the component's lifetime.

---

## 6. The files the template generates, and which ones matter

`dotnet new blazor -int Server` produces roughly this ([project structure](https://learn.microsoft.com/en-us/aspnet/core/blazor/project-structure?view=aspnetcore-10.0)):

```
Program.cs
appsettings.json / appsettings.Development.json
Properties/launchSettings.json
wwwroot/                          static assets (css, images)
Components/
  App.razor                       root component: <html>, <head>, Routes, Blazor <script>
  Routes.razor                    the Router
  _Imports.razor                  shared @using / @inject for the folder tree
  Layout/
    MainLayout.razor (+ .css)
    NavMenu.razor (+ .css)
    ReconnectModal.razor          (.NET 10; shows connection state)
  Pages/
    Home.razor, Counter.razor, Weather.razor, Error.razor
  NotFound.razor                  (.NET 10)
```

**Matters a lot:**

- **`Program.cs`** — entry point, service registrations, request pipeline. The two lines that make interactivity exist:

  ```csharp
  var builder = WebApplication.CreateBuilder(args);

  builder.Services.AddRazorComponents()
      .AddInteractiveServerComponents();      // registers the Interactive Server services

  builder.Services.AddScoped<ITodoStore, InMemoryTodoStore>();   // your stuff

  var app = builder.Build();
  app.UseStaticFiles();
  app.UseAntiforgery();

  app.MapRazorComponents<App>()
      .AddInteractiveServerRenderMode();      // configures the interactive SSR endpoint

  app.Run();
  ```

  `MapRazorComponents<App>` "discovers available components and specifies the root component for the app (the first component loaded)."

- **`Components/Pages/*.razor`** — where you actually work. Routable via `@page "/foo"`.
- **`App.razor`** — "the root component of the app with HTML `<head>` markup, the `Routes` component, and the Blazor `<script>` tag." Edit it to set the app-wide render mode.
- **`_Imports.razor`** — shared `@using` directives so you don't repeat them. The template includes `@using static Microsoft.AspNetCore.Components.Web.RenderMode`, which is why you can write `@rendermode InteractiveServer` instead of `@rendermode RenderMode.InteractiveServer`.

**Matters less at first:** `MainLayout.razor` (the shell your pages render inside), `NavMenu.razor` (sidebar links), `wwwroot` (static files), `appsettings.json` (config), `launchSettings.json` (which profile `dotnet run` picks — the first one with `commandName: Project`).

⚠️ **One `_Imports.razor` trap:** injecting a service in the top-level `Components/_Imports.razor` "results in resolving *two instances* of the service in page components," because `App.razor` always renders statically. Put shared `@inject` in `Components/Pages/_Imports.razor` instead.

---

## 7. The four mistakes you will make

**a) `async void` event handlers.** From the [event handling doc](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/event-handling?view=aspnetcore-10.0), flagged Important:

> The Blazor framework doesn't track `void`-returning asynchronous methods (`async`). As a result, the entire process fails when an exception isn't caught if `void` is returned. **Always return a `Task`/`ValueTask` from asynchronous methods.**

```csharp
private async void Save() { ... }   // ❌ untracked; unhandled exception kills the circuit
private async Task Save() { ... }   // ✅
```

Synchronous `void` handlers are fine. It's specifically `async void`.

**b) Mutating a collection and seeing nothing change.** Adding to a `List<T>` inside a click handler works — the handler triggers a render. It fails when the mutation happens *outside* the event pipeline: a timer, a background task, a C# event from a shared state service, or a *different* component's action. Blazor "only knows about its own lifecycle methods and Blazor-triggered events." Two components sharing a scoped store don't re-render each other; the one that didn't handle the event needs `StateHasChanged()` (subscribe to an event on the store, call it from the handler, and unsubscribe in `Dispose`). Also beware: after the *last* `await` in an async handler your mutations are rendered, but mutations between `await`s are not.

**c) `@bind` vs `@onchange`.** `@bind="x"` is sugar. The docs show the equivalent hand-written form — it binds the property to *both* the element's `value` attribute and its `onchange` event:

```razor
<input @bind="InputValue" />

<!-- what that expands to, per the docs -->
<input value="@InputValue"
       @onchange="@((ChangeEventArgs e) => InputValue = e?.Value?.ToString())" />
```

Because `@bind` already emits `@onchange`, you cannot also write `@onchange` on the same element — you'd be specifying the same attribute twice, and the Razor compiler rejects it. *(This follows from the expansion shown in the docs; I did not find a page that states the compiler error in those words.)* The supported ways to run your own code alongside a bind:

- `@bind:after="Handler"` — runs a delegate after the value is assigned. Note: `EventCallback` "isn't supported" with `@bind:after`; pass a method returning `Action` or `Task`.
- `@bind:event="oninput"` — bind on every keystroke instead of on blur.
- `@bind:get` / `@bind:set` — full manual control. Required for real two-way binding: "Two-way data binding isn't possible to implement with an event handler."

Use plain `@onchange` when you only want to *react*; use `@bind` when you want the field and the element kept in sync.

**d) The render-mode gotcha — your page looks dead.** Buttons do nothing, no errors, no exceptions. Cause: **"The default render mode is Static."** From the [render modes doc](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0):

> Since no ancestor component specifies a render mode, the following component is *statically rendered* on the server. The button isn't interactive and doesn't call the `UpdateMessage` method when selected.

Fixes — pick one:

```razor
@* per component definition *@
@page "/counter"
@rendermode InteractiveServer
```

```razor
@* per instance, where it's used *@
<Dialog @rendermode="InteractiveServer" />
```

```razor
@* whole app, in Components/App.razor *@
<HeadOutlet @rendermode="InteractiveServer" />
<Routes @rendermode="InteractiveServer" />
```

The `Router` propagates its render mode to the pages it routes, so setting it on `Routes` makes everything interactive. Related trap: render mode **inherits** — a child inside a statically-rendered parent is static too, and you *can't* opt a child back into interactivity from a static parent's subtree by any means other than giving the child its own render mode. Also, you can't apply an interactive render mode to a layout inheriting `LayoutComponentBase` in a per-page/component setup.

**Why `@rendermode` exists at all:** the same component can run statically on the server, interactively on the server over SignalR (InteractiveServer), in the browser on WebAssembly, or Auto (server first, WebAssembly after the bundle downloads). One directive picks which. We only care about InteractiveServer here.

---

## 8. Where older tutorials will mislead you

Anything written before .NET 8 (Nov 2023) describes a different project shape. If you follow a 2021 tutorial, here's what won't match:

| 2021 tutorial says | .NET 10 reality |
| --- | --- |
| Pick **"Blazor Server App"** or **"Blazor WebAssembly App"** template | One unified **Blazor Web App** template. Hosting model is a *per-component* decision via `@rendermode`, not a project-level one. |
| `builder.Services.AddServerSideBlazor();` | `builder.Services.AddRazorComponents().AddInteractiveServerComponents();` |
| `app.MapBlazorHub();` + `app.MapFallbackToPage("/_Host");` | `app.MapRazorComponents<App>().AddInteractiveServerRenderMode();` |
| `Pages/_Host.cshtml` is the root page; a `_Layout.cshtml` sits behind it | **Gone.** `Components/App.razor` is the root component and contains the `<html>`/`<head>`/`<script>` markup directly. No Razor Pages host file. |
| `App.razor` contains `<Router>` and is the router | **`App.razor` is now the HTML root document.** The router moved to `Components/Routes.razor`. |
| Components live in `Pages/` and `Shared/` | `Components/Pages/` and `Components/Layout/`. (In a WebAssembly `.Client` project, routable components do still live in `Pages/`.) |
| Everything is interactive automatically | **Static SSR is the default.** Without `@rendermode`, nothing responds to clicks. This is the #1 source of "I copied the tutorial and my button doesn't work." |
| No mention of prerendering | Interactive render modes **prerender by default**, so `OnInitializedAsync` may run twice (once static, once interactive) and services must be resolvable in both passes. |

Also worth knowing: the [hosting models article](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0) itself now opens by saying it's "primarily focused on Blazor Server and Blazor WebAssembly apps in versions of .NET earlier than .NET 8," and that .NET 8+ apps "are better conceptualized by how Razor components are rendered." When you search, prefer the *render modes* article over the *hosting models* article. And always check the version selector in the top-left of a Learn page — it silently defaults to the newest, but Google will land you on `?view=aspnetcore-6.0` links.

---

## Sources

All Microsoft Learn, `?view=aspnetcore-10.0`:

- [ASP.NET Core Razor components overview](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/?view=aspnetcore-10.0) — component = partial class deriving from `ComponentBase`; `@code`; `[Parameter]` auto-property rule; naming.
- [ASP.NET Core Razor component rendering](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/rendering?view=aspnetcore-10.0) — the four re-render triggers; `StateHasChanged` semantics; `InvokeAsync`; `ShouldRender`.
- [ASP.NET Core Blazor render modes](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0) — mode table; `@rendermode` directive vs directive attribute; "The default render mode is Static"; inheritance rules; `Program.cs` API.
- [ASP.NET Core Blazor event handling](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/event-handling?view=aspnetcore-10.0) — `async Task` vs `async void`; handlers auto-render; `EventCallback`.
- [ASP.NET Core Blazor data binding](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/data-binding?view=aspnetcore-10.0) — `@bind` expansion to `value` + `onchange`; `@bind:event`, `@bind:after`, `@bind:get`/`@bind:set`.
- [ASP.NET Core Blazor dependency injection](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection?view=aspnetcore-10.0) — `@inject`/`[Inject]`/constructor injection; lifetime table; **"the DI scope lasts for the duration of the circuit"**; `_Imports.razor` double-resolution trap; `OwningComponentBase`; disposable-transient leak.
- [ASP.NET Core Blazor project structure](https://learn.microsoft.com/en-us/aspnet/core/blazor/project-structure?view=aspnetcore-10.0) — Blazor Web App file list; also documents the legacy Blazor Server template (`_Host.cshtml`, `AddServerSideBlazor`, `MapBlazorHub`) under its older version monikers.
- [ASP.NET Core Blazor hosting models](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0) — circuit definition; SignalR/WebSockets; per-tab circuits; graceful vs non-graceful disconnect.
- [ASP.NET Core Razor component lifecycle](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/lifecycle?view=aspnetcore-10.0) — `OnInitializedAsync` / `OnParametersSetAsync` / `OnAfterRenderAsync`.

**Version note:** the rendering, event handling, data binding, DI, hosting models, and lifecycle pages are published with a single moniker range covering .NET 3.1 through 11 — the .NET 10 view is the same prose as the .NET 8/9 view for everything cited here. The render modes and project structure pages start at .NET 8; .NET 10 adds only `NotFound.razor`, `ReconnectModal`, and fingerprinted script assets to what's described above.
