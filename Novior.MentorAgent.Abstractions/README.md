# Novior.MentorAgent.Abstractions

> **New package IDs.** From 1.0.0 the packages, their namespaces and their assemblies are `Novior.MentorAgent.*`; up to 1.0.0-rc.15 they were `MentorAgent.*`. See *Migrating from the MentorAgent.\* packages* in the [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) README.

> **You do not need to install this package directly.**
> It is included automatically as a transitive dependency of `Novior.MentorAgent` and `Novior.MentorAgent.Blazor`.

This package is the shared foundation of the MentorAgent family. It contains all the contracts, models, and UI components shared between the server-side and client-side packages — with no AI or ASP.NET Core server dependencies, making it fully compatible with Blazor WebAssembly.

---

## Table of Contents

- [Package Family](#package-family)
- [What's included](#whats-included)
  - [Interfaces](#interfaces)
  - [Models](#models)
  - [Enums](#enums)
  - [UI Components (Razor)](#ui-components-razor)
  - [Core](#core)
- [Component parameter reference](#component-parameter-reference)
- [Static assets — CSS and JS](#static-assets--css-and-js)
- [When you actually touch this package](#when-you-actually-touch-this-package)
- [Why a separate package?](#why-a-separate-package)
- [Requirements](#requirements)
- [Related Packages](#related-packages)
- [License](#license)

---

## Package Family

| Package | Install when |
|---------|-------------|
| [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) | Blazor Server app |
| [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) | Web API / headless backend, or Blazor Auto server-side project |
| [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) | Blazor WASM / Blazor Auto client project |
| **Novior.MentorAgent.Abstractions** ← you are here | Never directly — it arrives with any of the above |
| [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) | **Optional** — define Level-2 specialist agents in YAML instead of C# |

---

## What's included

### Interfaces

| Interface | Description |
|-----------|-------------|
| `IMentorOrchestrator` | AI orchestrator contract — implemented by `MentorOrchestrator` (server) and `WasmMentorOrchestrator` (WASM) |
| `IMentorStateService` | Thread-safe event bus between the AI layer and the UI layer |
| `IMentorPageContext` | Page context and UI actions registry |
| `IMentorAgent` | Marker interface for Level 2 specialized agents |
| `IMentorTeam` | Marker interface for Level 3 collaborative teams |
| `IMentorStructured` | Typed generation — `GenerateAsync<T>(input, instructions)` returns a deserialized `T`, with the JSON schema derived from your type |
| `IMentorMetrics` | Read-side of the token/cost counters — `GetSnapshot()` feeds `MentorDashboard` |
| `IMentorTour` | Supplies the onboarding tour steps. The default implementation generates them from the app's own pages and tools; register your own to script the tour by hand |
| `IMentorMetricsStore` | Optional persistence/aggregation for those metrics. Implement it to survive restarts or to read an external aggregate (Prometheus, Azure Monitor) instead of the in-RAM snapshot |

### Models

| Type | Description |
|------|-------------|
| `ConfirmationRequest` | HITL confirmation request shown in `ConfirmationBanner` |
| `MentorRagResult` | A single RAG search result with content, URL, title, and score, plus optional `Metadata` the model is shown with it |
| `MentorWidgetOptions` | UI-only options for `ChatWidget` (theme, position, language, etc.) |
| `UIActionRegistration` | Handler registration for a page-level UI action |
| `PageContextSnapshot` | Serializable page context sent from WASM client to server hub |
| `UIActionInfo` | UI action descriptor in a `PageContextSnapshot` |
| `AgentDisplayInfo` | Remote agent display info for the A2A detail badge |
| `MentorAttachment` | One image on a user turn — `DataBase64` (upload/paste) **or** `Url` (remote), mapped to the Agent Framework's `DataContent` / `UriContent` |
| `MentorUserMessage` | A user turn as text + attachments; an image-only turn (empty text) is valid |
| `MentorTourStep` | One step of the onboarding tour — title, body, the screen it describes and a ready-made question the user can send with one tap |
| `MentorCard` | A generative-UI card a tool returns instead of prose — kind, title, subtitle, fields, actions, image, accent |
| `MentorCardField` / `MentorCardAction` | One label/value row, and one button (`SendMessage` / `Navigate` / `UIAction`) |
| `MentorMetricsSnapshot` | Point-in-time totals, per-model breakdown, hourly series, top actions and cost — the payload of `GET /mentor/admin/metrics` |
| `MentorModelUsage` | One model's slice of that snapshot (tokens, calls, latency, cost) |
| `ModelPrice` | `record ModelPrice(decimal InputPer1M, decimal OutputPer1M)` — you supply prices; the library ships no price table, because they change and a stale one lies |

### Enums

| Enum | Values |
|------|--------|
| `MentorLanguage` | English, Italian, French, German, Spanish, Portuguese, Dutch, Polish, Japanese, Chinese |
| `MentorTheme` | Default, Dark, Minimal, Custom |
| `ChatPosition` | BottomRight, BottomLeft, TopRight, TopLeft, SideRight, SideLeft |
| `MentorshipLevel` | Minimal, Standard, Proactive |
| `MentorHostedTools` | `[Flags]` — None, WebSearch, CodeInterpreter, FileSearch, ImageGeneration, HostedMcp. The set the model provider may run on its own infrastructure; also what the server publishes on connect so a client badge never drifts from the server |
| `MentorModelKind` | Cheap, Strong, Embedding, Other — how a model's usage is attributed in the metrics snapshot. `Other` is any model id that is none of the three configured ones, e.g. a `ClassifierChatClient` on its own deployment |
| `MentorCardActionKind` | SendMessage, Navigate, UIAction — what a card button does |
| `MentorCardAccent` | Default, Success, Warning, Danger, Info — the card's colour accent |

### UI Components (Razor)

All MentorAgent UI components live here so they work identically in both Blazor Server and Blazor WASM:

| Component | Description |
|-----------|-------------|
| `ChatWidget` | Main floating chat widget — injects CSS/JS automatically |
| `MessageBubble` | A chat message: Markdown rendering, citation chips and any images the message carries. **XSS-safe by construction** — every piece of model text is HTML-encoded before a tag is emitted, so the model supplies data, never markup |
| `ChatInput` | Text input with voice recognition and the image composer (upload, paste, drag & drop, remote URL) |
| `MentorDashboard` | Admin token & cost dashboard — per-model breakdown, temporal SVG charts, cost table. Presentational: hand it a `MentorMetricsSnapshot`, from DI in Blazor Server or from `GET /mentor/admin/metrics` in WASM. **Never** rendered by `ChatWidget` — keep it off end-user pages |
| `ConfirmationBanner` | HITL approval dialog |
| `OnboardingTour` | Guided first-run tour. Presentational — it is handed the steps and raises callbacks, which is what lets the same component serve Blazor Server (steps from DI) and WASM (steps over HTTP) |
| `MentorCardView` | Built-in renderer for a `MentorCard`. **XSS-safe by construction** — every value goes through Razor interpolation and no `MarkupString` is used, because a card carries data and never markup |
| `WelcomePanel` | Empty-state welcome screen with suggestion chips |
| `TypingIndicator` | Animated typing dots while AI processes |
| `RagSourcePanel` | Citation chips below AI responses |
| `McpStatusBadge` / `McpDetailBar` | MCP server connection status |
| `A2AStatusBadge` / `A2ADetailBar` | A2A remote agent status |

### Core

| Type | Description |
|------|-------------|
| `MentorLocalizer` | In-memory i18n for 10 languages — no `.resx` files, no external dependencies. Registered by `AddMentorAgent()` / `AddMentorAgentBlazor()`; inject it as `MentorLocalizer` and call `L.Get("key")` if you build UI that must match the widget's language |
| `MentorLanguageExtensions` | `MentorLanguage.Italian.ToIsoCode()` → `"it"` (ISO 639-1; an unknown value gives `"en"`), and `ToFullName()` → `"Italian"`. `ToIsoCode()` is what the components hand the JS layer, which expands it to the BCP-47 tag the browser Speech APIs expect (`"it"` → `"it-IT"`). A custom voice control that talks to `SpeechRecognition` / `speechSynthesis` directly has to do that expansion itself to speak the same language as the widget |
| `TourProgress` | *(internal)* Where the onboarding tour resumes. Not API — documented only because its edge cases are user-visible |

---

## Component parameter reference

Every component is **presentational** in the sense that matters: it takes its data through
parameters and raises callbacks out, never calling the AI itself. That is what lets the identical
component serve Blazor Server (data from DI) and WebAssembly (data over HTTP), and it is what lets
you compose the parts on your own pages instead of taking the whole widget.

`ChatWidget` is the exception — it is the assembled widget and resolves `IMentorOrchestrator`,
`IMentorStateService` and navigation for itself.

> **They still need DI.** Every component except `MentorDashboard` and `MentorCardView` injects `IOptions<MentorWidgetOptions>`, `MentorLocalizer`
> or both, so `AddMentorAgent()` / `AddMentorAgentBlazor()` must have run in that host. Rendering
> `MessageBubble` on a page of an app that never registered MentorAgent throws at build-render time,
> not at first interaction.

> **Namespaces and injections.** The components live in `Novior.MentorAgent.Abstractions.Components`, the
> models and enums in `Novior.MentorAgent.Abstractions.Models`, the interfaces in
> `Novior.MentorAgent.Abstractions.Interfaces` and `MentorLocalizer` in `Novior.MentorAgent.Abstractions.Core`.
> `ChatMessage` / `ChatRole` come from `Microsoft.Extensions.AI`. The examples below assume these
> in your `_Imports.razor`, plus the services they call injected on the page:
>
> ```razor
> @using Novior.MentorAgent.Abstractions.Components
> @using Novior.MentorAgent.Abstractions.Models
> @using Novior.MentorAgent.Abstractions.Interfaces
> @using Novior.MentorAgent.Abstractions.Core
> @using Microsoft.Extensions.AI
>
> @inject IMentorOrchestrator Orchestrator
> @inject IMentorStateService State
> @inject NavigationManager   Nav
> @inject IMentorTour         Tour
> ```

### `ChatWidget`

| Parameter | Type | Description |
|---|---|---|
| `CardTemplate` | `RenderFragment<MentorCard>?` | Optional custom renderer for generative-UI cards. Not supplied → `MentorCardView` is used. Match on `card.Kind` and fall back to the built-in renderer for kinds you do not handle |

```razor
@* A MentorCardView inside the template needs no OnAction: ChatWidget cascades its own
   dispatcher around the cards, so these buttons send / navigate / run the UI action. *@
<ChatWidget>
    <CardTemplate Context="card">
        @if (card.Kind == "order") { <OrderCard Card="card" /> }
        else                       { <MentorCardView Card="card" /> }
    </CardTemplate>
</ChatWidget>
```

The template replaces the widget's card rendering for **every** card, but not its button handling:
a `<MentorCardView Card="card" />` rendered inside the template without `OnAction` uses the widget's
dispatcher, so its `SendMessage`, `Navigate` and `UIAction` buttons work as they do on an untemplated
card. An `OnAction` you set explicitly still wins. Buttons your template draws itself (inside
`OrderCard` above) do **not** get the dispatcher: handle them yourself, or render `MentorCardView` for
the cards whose buttons should just work.

Everything else about the widget comes from `MentorWidgetOptions` in DI, not from parameters — see
the option tables in the [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) or
[Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) README, depending on which
host you use.

### `MentorDashboard`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Snapshot` | `MentorMetricsSnapshot?` | `null` | An **override**, not the only source. Pass it in WASM/headless. In Blazor Server leave it `null` and the component resolves the data itself |
| `OnRefresh` | `EventCallback` | — | Fired *after* the Refresh button re-resolves. Handle it in WASM to re-fetch; in Blazor Server you can ignore it |
| `Currency` | `string` | `"$"` | Symbol prefixed to every cost figure. The library never converts — supply `ModelPricing` already in the currency you want shown |
| `Language` | `MentorLanguage?` | `null` | `null` → the `MentorLocalizer` from DI, which follows the `Language` option on both hosts (`AddMentorAgent()` and `AddMentorAgentBlazor()` both register it). Pass it only to override that language, or in an app that registered neither; there the dashboard falls back to English |

The Refresh button is part of the populated view; `OnRefresh` only decides whether anything of yours
runs when it is pressed. With no data at all the component renders its empty state rather than
throwing, and that state has **no** Refresh button. In WASM, a first fetch that failed cannot be
retried from the dashboard: retry it yourself (a button of your own, or a timer) and pass the new
`Snapshot`.

Its resolution order when `Snapshot` is `null` is: an external `IMentorMetricsStore.QueryAsync()`
first (so a Prometheus/Azure Monitor aggregate wins), then the live in-RAM `IMentorMetrics` snapshot.

```razor
@* Blazor Server — no parameters needed *@
<MentorDashboard />

@* WASM — you own the fetch *@
<MentorDashboard Snapshot="_snapshot"
                 OnRefresh="Reload"
                 Currency="€"
                 Language="MentorLanguage.Italian" />
```

### `MentorCardView`

| Parameter | Type | Description |
|---|---|---|
| `Card` | `MentorCard` | **Required.** The card to render |
| `OnAction` | `EventCallback<MentorCardAction>` | Raised when a button is pressed. Left unset inside `ChatWidget` (including in a `CardTemplate`), the widget's own dispatcher sends / navigates / runs the UI action; an explicit `OnAction` wins. Wire it yourself when you render cards outside the widget |

```razor
<MentorCardView Card="_card" OnAction="RunCardAction" />

@code {
    private readonly MentorCard _card = new("order")
    {
        Title    = "Ordine #1002",
        Subtitle = "Laura Bianchi",
        Accent   = MentorCardAccent.Info,
        Fields   = [new("Stato", "Shipped"), new("Totale", "1.249,98 €")],
        Actions  =
        [
            new("Apri",     MentorCardActionKind.Navigate,    "/orders/1002"),
            new("Dettagli", MentorCardActionKind.SendMessage, "Dammi i dettagli dell'ordine 1002"),
        ],
    };

    // @inject IMentorOrchestrator Orchestrator, NavigationManager Nav, IMentorPageContext PageContext
    private async Task RunCardAction(MentorCardAction action)
    {
        switch (action.Kind)
        {
            case MentorCardActionKind.SendMessage:
                await Orchestrator.SendMessageAsync(action.Value);
                break;

            case MentorCardActionKind.Navigate:
                Nav.NavigateTo(action.Value);
                break;

            // Resolved at click time, as ChatWidget does: the page that registered the action
            // may have changed since the card was rendered.
            case MentorCardActionKind.UIAction
                when PageContext.UIActions.TryGetValue(action.Value, out var reg):
                if (reg.HandlerAsync is not null) await reg.HandlerAsync(null);
                else reg.Handler(null);
                break;
        }
    }
}
```

> The buttons are written by the tool that produced the card, never by the model's prose — which is
> why a card can safely carry actions. Inside `ChatWidget` the buttons are handled for you, in a
> `CardTemplate` too as long as the card is rendered by `MentorCardView`; wire `OnAction` yourself only
> when you render cards outside the widget.

### `MessageBubble`

| Parameter | Type | Description |
|---|---|---|
| `Message` | `ChatMessage` | **Required.** The MEAI message. Role decides the side and styling |
| `IsStreaming` | `bool` | Renders the trailing caret while text is still arriving |
| `SentAt` | `DateTime?` | Timestamp under the bubble; omitted when `null` |
| `Sources` | `IReadOnlyList<MentorRagResult>?` | RAG citation chips below the message, rendered through `RagSourcePanel`, so only on an assistant message and only when `MentorWidgetOptions.ShowRagSources` is on |

```razor
@foreach (var m in _messages)
{
    <MessageBubble Message="m.Message" SentAt="m.At" Sources="m.Sources" />
}

@* the one still arriving — the caret is the only difference *@
@if (_streaming is not null)
{
    <MessageBubble Message="_streaming" IsStreaming="true" />
}
```

`Message` is MEAI's own `ChatMessage`, so the role decides the side and the styling. Model text is
HTML-encoded before any Markdown tag is emitted, so a reply containing `<script>` renders as text.

### `ChatInput`

| Parameter | Type | Description |
|---|---|---|
| `OnSend` | `EventCallback<MentorUserMessage>` | **Required.** Raised with text **and** attachments — an image-only turn (empty text) is valid |
| `Disabled` | `bool` | Blocks input while a turn is in flight |
| `Placeholder` | `string?` | Overrides the localised default. `MentorWidgetOptions.InputPlaceholder` is **not** read here: `ChatWidget` passes it in, so pass it yourself on your own surface |

The rest comes from `MentorWidgetOptions`. The microphone appears only with `EnableVoiceInput` (and a
browser that supports speech recognition). The image composer appears only with `EnableImageInput`,
limited by `MaxImageBytes`, `MaxImagesPerMessage` and `AllowedImageTypes`. `MaxMessageLength` sets
the textarea's `maxlength`.

```razor
<ChatInput OnSend="Send" Disabled="_busy" Placeholder="Chiedi qualcosa sugli ordini…" />

@code {
    private bool _busy;

    private async Task Send(MentorUserMessage message)
    {
        _busy = true;
        try
        {
            // Text AND attachments. An image-only turn — empty Text, one attachment — is valid.
            await Orchestrator.SendMessageAsync(message.Text, message.Attachments);
        }
        finally { _busy = false; }
    }
}
```

### `OnboardingTour`

| Parameter | Type | Description |
|---|---|---|
| `Steps` | `IReadOnlyList<MentorTourStep>` | **Required.** From `IMentorTour` in Blazor Server, or `GET /mentor/tour` in WASM |
| `StartIndex` | `int` | Where to open. Clamped into range, so a stale stored index cannot throw |
| `OnIndexChanged` | `EventCallback<int>` | Fired on every step change — this is what the host persists to resume later |
| `OnNavigate` | `EventCallback<string>` | The step's URL, when the user chooses to go there |
| `OnAsk` | `EventCallback<string>` | The step's ready-made question, when the user taps it |
| `OnClose` | `EventCallback` | Skip or finish |

`OnNavigate` and `OnAsk` are *engagement*, not dismissal: the host is expected to suspend the tour
and resume at the next step, not mark it seen. `ChatWidget` already does this.

```razor
@if (_tourOpen)
{
    <OnboardingTour Steps="_steps"
                    StartIndex="_resumeAt"
                    OnIndexChanged="Persist"
                    OnNavigate="GoThere"
                    OnAsk="AskThat"
                    OnClose="Finish" />
}

@code {
    private IReadOnlyList<MentorTourStep> _steps = [];
    private bool _tourOpen;
    private int  _resumeAt;

    protected override async Task OnInitializedAsync()
    {
        // IMentorTour from DI on both hosts: generated in-process on Blazor Server,
        // fetched from GET /mentor/tour by Novior.MentorAgent.Blazor on WebAssembly.
        _steps    = await Tour.GetStepsAsync();
        _resumeAt = await LoadSavedIndex();          // your storage; clamped for you
        // At or past the end: the user already acted on the last step, so there is nothing to resume.
        _tourOpen = _steps.Count > 0 && _resumeAt < _steps.Count;
    }

    // Track where the user IS, not where the tour opened: the component moves on its own and
    // OnIndexChanged is the only way you learn it.
    private Task Persist(int index) { _resumeAt = index; return SaveIndex(index); }

    // Engagement, not dismissal: suspend and resume at the NEXT step rather than marking it seen.
    private async Task GoThere(string url)  { _tourOpen = false; await SaveIndex(_resumeAt + 1); Nav.NavigateTo(url); }
    private async Task AskThat(string q)    { _tourOpen = false; await SaveIndex(_resumeAt + 1); await Orchestrator.SendMessageAsync(q); }

    private Task Finish() { _tourOpen = false; return MarkSeen(); }
}
```

### `ConfirmationBanner`

| Parameter | Type | Description |
|---|---|---|
| `Request` | `ConfirmationRequest` | **Required.** What is being approved |
| `OnConfirm` | `EventCallback` | **Required.** Approve |
| `OnCancel` | `EventCallback` | **Required.** Reject |

```razor
@if (_pending is not null)
{
    <ConfirmationBanner Request="_pending" OnConfirm="Approve" OnCancel="Reject" />
}

@code {
    private ConfirmationRequest? _pending;

    protected override void OnInitialized() =>
        State.OnConfirmationRequired += r => { _pending = r; InvokeAsync(StateHasChanged); };

    private async Task Approve() { var r = _pending!; _pending = null; await State.ConfirmAsync(r.ActionId); }
    private Task     Reject()    { var r = _pending!; _pending = null; State.Cancel(r.ActionId); return Task.CompletedTask; }
}
```

> On **WebAssembly** the snippet above already answers over HTTP: `State.ConfirmAsync` / `State.Cancel`
> post to `POST /mentor/approve?actionId=…&approved=true|false` rather than calling a hub method,
> because SignalR dispatches one invocation at a time per connection, so a hub call would queue behind
> the very turn it is meant to release. The request goes to the **hub's origin** when
> `MentorAgentBlazorOptions.HubUrl` is an absolute `http(s)` URL (relative to the host `HttpClient`'s
> `BaseAddress` when it is relative, i.e. same-origin hosting) and carries
> `Authorization: Bearer <token>` from `MentorAgentBlazorOptions.AccessTokenProvider` when one is set,
> the same credential the hub uses, so a signed-in user's answer is not refused with 401. Stop takes
> the same route (`POST /mentor/cancel`). The host must still register an `HttpClient`, as the WASM
> template does. Call the endpoints yourself only from a client that has no `IMentorStateService`
> (JavaScript, React), and send the same bearer token there.

### `WelcomePanel`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `BotName` | `string` | `"Mentor AI"` | Shown in the empty state heading and as the avatar's alt text |
| `WelcomeMessage` | `string?` | `null` | `null` or blank → the localised default. Rendered as **HTML**, not encoded: pass only text you wrote, never user or model input |
| `EnableSuggestions` | `bool` | `true` | Whether to render the three built-in, localised suggestion chips. They also need `OnChipClick`; with no handler they are not rendered |
| `OnChipClick` | `EventCallback<string>` | — | The chip's text, to be sent as a user turn |

```razor
@if (_messages.Count == 0)
{
    <WelcomePanel BotName="Assistente ShopFlow"
                  WelcomeMessage="Ciao! Posso aiutarti con ordini e prodotti."
                  EnableSuggestions="true"
                  OnChipClick="Send" />
}
```

The chip's text arrives as an ordinary user turn — there is nothing special about it, which is why
`OnChipClick` and the input's `OnSend` can share one handler.

### `RagSourcePanel`

| Parameter | Type | Description |
|---|---|---|
| `Sources` | `IReadOnlyList<MentorRagResult>?` | Citation chips: the first three, then a "+N more" chip that expands the rest. Renders nothing when `null`, empty, **or when `MentorWidgetOptions.ShowRagSources` is off**, which is the default: set `options.ShowRagSources = true` on either host |

```razor
<RagSourcePanel Sources="_sources" />

@code {
    // null or empty renders nothing at all, so there is no need to guard the element.
    private IReadOnlyList<MentorRagResult>? _sources;

    protected override void OnInitialized() =>
        State.OnRagSourcesReady += s => { _sources = s; InvokeAsync(StateHasChanged); };
}
```

### Status badges

| Component | Parameters |
|---|---|
| `McpStatusBadge` | `ServerStatus` (`IReadOnlyDictionary<string, bool?>?` — name → connected/failed/unknown), `OnToggle` |
| `McpDetailBar` | `Visible` (`bool`), `ServerStatus`, `OnClose` |
| `A2AStatusBadge` | `OnToggle` — the agent list comes from `MentorWidgetOptions.RemoteAgentDisplays` |
| `A2ADetailBar` | `Visible` (`bool`), `OnClose` |

`bool?` in `ServerStatus` is three-valued on purpose: `true` connected, `false` failed,
`null` not yet probed. Rendering `null` as "failed" would show a red badge during startup.

The badges are also gated by `MentorWidgetOptions`, so on a page of your own they render nothing until
the matching options are set. `McpStatusBadge` needs `ShowMcpStatus` **and** `HasMcpServers`, plus at
least one entry in `ServerStatus`. `McpDetailBar` needs `HasMcpServers` and a non-empty `ServerStatus`.
`A2AStatusBadge` needs `ShowA2AStatus` **and** `HasRemoteAgents`. `A2ADetailBar` needs a non-empty
`RemoteAgentDisplays`. On Blazor Server, `AddMentorAgent()` fills `HasMcpServers`, `HasRemoteAgents`,
`RemoteAgentDisplays` and `McpServerNames` from your MCP and A2A configuration. On WebAssembly you set
them on `MentorAgentBlazorOptions`, because the client cannot see the server's configuration.

```razor
<McpStatusBadge ServerStatus="_mcp" OnToggle="() => _mcpOpen = !_mcpOpen" />
<McpDetailBar   Visible="_mcpOpen" ServerStatus="_mcp" OnClose="() => _mcpOpen = false" />

<A2AStatusBadge OnToggle="() => _a2aOpen = !_a2aOpen" />
<A2ADetailBar   Visible="_a2aOpen" OnClose="() => _a2aOpen = false" />

@code {
    private bool _mcpOpen, _a2aOpen;

    // name → connected. Seed every configured server as null ("connecting") rather than false:
    // nothing has been probed yet. An EMPTY dictionary hides the badge until the first status
    // event, which with lazily-connected MCP servers is the first message.
    // (@inject IOptions<MentorWidgetOptions> Options)
    private readonly Dictionary<string, bool?> _mcp = new(StringComparer.OrdinalIgnoreCase);

    protected override void OnInitialized()
    {
        foreach (var name in Options.Value.McpServerNames) _mcp[name] = null;

        State.OnMcpServerStatusChanged += (name, ok) =>
        {
            _mcp[name] = ok;
            InvokeAsync(StateHasChanged);
        };
    }
}
```

The A2A badge takes no data parameter on purpose — its agent list comes from
`MentorWidgetOptions.RemoteAgentDisplays`, because a remote agent is configuration rather than
runtime state.

### `TypingIndicator`

No parameters — animated dots. Show it while a turn is in flight and nothing has streamed yet.

```razor
@if (_busy && _streaming is null)
{
    <TypingIndicator />
}
```

Show it only while a turn is in flight and *nothing* has streamed yet — once the first token lands,
`MessageBubble` with `IsStreaming="true"` carries the signal instead.

---

## Composing your own chat surface

The reason these components take parameters and raise callbacks instead of calling the AI is so you
can build a surface that is not the floating widget — an inline panel, a side rail, a page of your
own. Everything below is host-agnostic: in Blazor Server the state service comes from DI, on
WebAssembly the same interface is backed by the hub.

Without `ChatWidget` on the page nothing injects the stylesheet and the script, so add both tags
yourself: see [Static assets — CSS and JS](#static-assets--css-and-js).

```razor
@inject IMentorOrchestrator Orchestrator
@inject IMentorStateService State
@implements IDisposable

<div class="my-chat">
    @if (_messages.Count == 0)
    {
        <WelcomePanel BotName="Assistente" OnChipClick="Send" />
    }

    @foreach (var m in _messages)
    {
        <MessageBubble Message="m" />
    }
    @if (_streaming is not null) { <MessageBubble Message="_streaming" IsStreaming="true" /> }
    @if (_busy && _streaming is null) { <TypingIndicator /> }

    <RagSourcePanel Sources="_sources" />

    @if (_pending is not null)
    {
        <ConfirmationBanner Request="_pending" OnConfirm="Approve" OnCancel="Reject" />
    }

    <ChatInput OnSend="OnSend" Disabled="_busy" />
</div>

@code {
    private readonly List<ChatMessage> _messages = [];
    private ChatMessage? _streaming;
    private IReadOnlyList<MentorRagResult>? _sources;
    private ConfirmationRequest? _pending;
    private bool _busy;
    private readonly System.Text.StringBuilder _buffer = new();

    protected override void OnInitialized()
    {
        State.OnStreamingChunk     += Chunk;
        State.OnStreamingCompleted += Completed;
        State.OnBusyChanged        += Busy;
        State.OnRagSourcesReady    += Rag;
        State.OnConfirmationRequired += Confirm;
    }

    private void Chunk(string c)
    {
        _buffer.Append(c);
        _streaming = new ChatMessage(ChatRole.Assistant, _buffer.ToString());
        InvokeAsync(StateHasChanged);
    }

    private void Completed()
    {
        if (_buffer.Length > 0) _messages.Add(new ChatMessage(ChatRole.Assistant, _buffer.ToString()));
        _buffer.Clear();
        _streaming = null;
        InvokeAsync(StateHasChanged);
    }

    private void Busy(bool b)                          { _busy = b; InvokeAsync(StateHasChanged); }
    private void Rag(IReadOnlyList<MentorRagResult> s) { _sources = s; InvokeAsync(StateHasChanged); }
    private void Confirm(ConfirmationRequest r)        { _pending = r; InvokeAsync(StateHasChanged); }

    private async Task Send(string text) => await OnSend(new MentorUserMessage(text));

    private async Task OnSend(MentorUserMessage m)
    {
        _messages.Add(new ChatMessage(ChatRole.User, m.Text));
        _sources = null;
        await Orchestrator.SendMessageAsync(m.Text, m.Attachments);
    }

    private async Task Approve() { var r = _pending!; _pending = null; await State.ConfirmAsync(r.ActionId); }
    private Task     Reject()    { var r = _pending!; _pending = null; State.Cancel(r.ActionId); return Task.CompletedTask; }

    public void Dispose()
    {
        State.OnStreamingChunk       -= Chunk;
        State.OnStreamingCompleted   -= Completed;
        State.OnBusyChanged          -= Busy;
        State.OnRagSourcesReady      -= Rag;
        State.OnConfirmationRequired -= Confirm;
    }
}
```

> **Unsubscribe.** These are plain C# events on a scoped service that outlives your component, so a
> component that subscribes and never detaches is kept alive by the service and re-rendered after
> disposal. `ChatWidget` does this for you; a hand-built surface has to do it itself.

**Subscribe to `OnError` too.** A message the host refuses before the model runs produces no streamed
text and no `OnStreamingCompleted`: only `OnError` with the localised reason, then `OnBusyChanged(false)`.
That covers a message that is too long or rate-limited, one blocked by the safety check (or, with
`SafetyCheckFailure = MentorSafetyCheckFailure.Block`, one whose check could not run), and one refused
as out of scope under `RefuseOutOfScope`. Provider failures arrive the same way. Without a handler, the
surface above shows the user's bubble and then nothing:

```razor
@if (_error is not null) { <div class="my-chat__error">@_error</div> }

@code {
    private string? _error;

    // OnInitialized: State.OnError += Error;    Dispose: State.OnError -= Error;
    // OnSend:        _error = null;  before sending the next message
    private void Error(string message) { _error = message; InvokeAsync(StateHasChanged); }
}
```

---

## Static assets — CSS and JS

The stylesheet and the JavaScript layer ship in **this** package, not in `Novior.MentorAgent`:

```
_content/Novior.MentorAgent.Abstractions/css/MentorAgent.css
_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js
_content/Novior.MentorAgent.Abstractions/IconMA.png     ← default avatar
```

`ChatWidget` injects the first two itself through `<HeadContent>`, so a Blazor Server or Blazor Web
App host needs no markup. A **standalone WASM** host references them manually in `index.html`
instead — and neither file is fingerprinted, so the `?v=` query string is the cache buster and you
must bump it by hand after changing either. A stale `MentorAgent.js` fails silently and looks like
missing features rather than a caching problem.

That injection happens only where `ChatWidget` is rendered, and it relies on the `<HeadOutlet />`
that the Blazor Web App template already includes. A page that composes the parts **without**
`ChatWidget` (see [Composing your own chat surface](#composing-your-own-chat-surface)) gets neither
file: the components render unstyled, and `ChatInput`'s microphone and paste / drag & drop stay off
without an error. Sending a message still works: the call that resets the textarea after a send is
guarded, so a missing script neither throws nor ends a Blazor Server circuit. Reference both files yourself in that case, with the same paths and version
`ChatWidget` uses, in `App.razor` (Blazor Server / Web App) or `index.html`:

```html
<link href="_content/Novior.MentorAgent.Abstractions/css/MentorAgent.css?v=10" rel="stylesheet" />
<script src="_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js?v=10"></script>
```

The script can safely load a second time on a page where `ChatWidget` also renders: the later tag
wins and the voice state carries over.

The JS layer covers text-to-speech (sentence-queued, barge-in, hands-free), speech recognition, the
image composer (upload, paste, drag & drop, client-side downscaling) and `localStorage` helpers. It
is called through `IJSRuntime` by the components; there is no supported way to call it directly.

For the complete CSS variable reference — palette, widget size, fonts, the dashboard and chart
colours — see **[Widget customization](https://www.nuget.org/packages/Novior.MentorAgent) → CSS variables**
in the `Novior.MentorAgent` README. The variables are defined here but documented there, next to the
options that set them.

---

## When you actually touch this package

Most of the time you never reference these types by name — `Novior.MentorAgent` and `Novior.MentorAgent.Blazor`
wire them for you. Three cases where you do:

**1. Rendering the admin dashboard yourself.** `MentorDashboard` is presentational, so the same
component works in-process and over HTTP. In Blazor Server, inject the metrics; in WASM, fetch the
snapshot from the admin endpoint:

```razor
@* Blazor Server — in-process. It resolves the metrics itself; no parameters needed. *@
<MentorDashboard />

@* WASM — over HTTP. There is no IMentorMetrics in the browser, so pass the snapshot. Language is optional: it follows AddMentorAgentBlazor's Language unless you override it. *@
@inject HttpClient Http
<MentorDashboard Snapshot="_snapshot" Language="MentorLanguage.Italian" />
@code {
    MentorMetricsSnapshot? _snapshot;
    protected override async Task OnInitializedAsync() =>
        _snapshot = await Http.GetFromJsonAsync<MentorMetricsSnapshot>("/mentor/admin/metrics");
}
```

> Put it behind authorization. The snapshot is operator data — cost, volumes, top actions — and must
> never appear on an end-user page.

> The endpoint is gated on its own as well: `GET /mentor/admin/metrics` requires the role in
> `MentorOptions.DashboardRole` (`"Admin"` by default; `""` leaves it open, for development only). The
> `HttpClient` you fetch with must therefore send the signed-in admin's credentials (cookie or bearer
> token). Without them the request is refused (401/403, or a redirect to the login page under cookie
> authentication) and `GetFromJsonAsync` throws instead of the dashboard rendering its empty state. In a
> standalone WASM app, its `BaseAddress` must also be the server's origin, not the client's.

**2. Persisting or aggregating metrics.** Implement `IMentorMetricsStore` and register it *before*
`AddMentorAgent()`, in a headless host too, where `AddMentorAgentServer()` comes after it. Registered
any later, the store still answers `QueryAsync`, but `LoadForSeedAsync` and `SaveAsync` are never called,
because the persistence service is added only when the store is already there. Without one the snapshot
lives in RAM and resets on restart; with one you get durability, or you can serve an external aggregate
instead:

Every method has a default no-op implementation, because the two scenarios use **different halves** of
the contract — implement only the one you need:

```csharp
// (A) Local durability — restore totals at startup, persist periodically and on shutdown.
//     QueryAsync stays default (null) so the dashboard keeps serving the live in-RAM snapshot.
public sealed class FileMetricsStore : IMentorMetricsStore
{
    public Task<MentorMetricsSnapshot?> LoadForSeedAsync(CancellationToken ct = default) => /* read  */;
    public Task SaveAsync(MentorMetricsSnapshot s, CancellationToken ct = default)       => /* write */;
}

// (B) External source — serve the aggregate your OpenTelemetry export already pushed to
//     Prometheus / Azure Monitor (multi-instance, historical). Nothing to seed, nothing to save.
public sealed class PrometheusMetricsStore : IMentorMetricsStore
{
    public Task<MentorMetricsSnapshot?> QueryAsync(CancellationToken ct = default) => /* query */;
}

builder.Services.AddSingleton<IMentorMetricsStore, FileMetricsStore>();   // BEFORE AddMentorAgent()
```

**3. Building your own composer or transcript.** `MentorUserMessage` + `MentorAttachment` are the shape
of a user turn, and `IMentorOrchestrator.SendMessageAsync(text, attachments)` is what consumes it — so
a custom input control can replace `ChatInput` without touching the orchestration layer.

Working implementations of all three ship in the `MentorAgentSample` repository.

---

## Why a separate package?

`Novior.MentorAgent` depends on `Microsoft.Agents.AI`, `ModelContextProtocol` and other packages that are not compatible with Blazor WebAssembly. `Novior.MentorAgent.Blazor` (WASM) cannot reference `Novior.MentorAgent` directly.

`Novior.MentorAgent.Abstractions` contains only WebAssembly-safe dependencies:
- `Microsoft.AspNetCore.Components.Web`
- `Microsoft.AspNetCore.Components.Authorization`
- `Microsoft.Extensions.AI`

Both `Novior.MentorAgent` and `Novior.MentorAgent.Blazor` reference `Novior.MentorAgent.Abstractions` — sharing interfaces, models, and UI components without cross-contaminating dependencies.

**The practical rule:** anything that must run in the browser goes here. If you are adding a type
and it needs `Microsoft.Agents.AI`, an `HttpContext`, or the file system, it does not belong in this
package — the WASM build will not fail at compile time, it will fail at runtime in the browser.

---

## Requirements

- .NET 10.0 or later
- `Microsoft.AspNetCore.Components.Web` 10.0.3
- `Microsoft.AspNetCore.Components.Authorization` 10.0.3
- `Microsoft.Extensions.AI` 10.7.0

No AI provider, no server packages, no JavaScript dependencies beyond the browser's own Speech and
File APIs.

---

## Related Packages

| Package | Purpose |
|---------|---------|
| [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) | Blazor Server — full AI assistant |
| [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) | Any ASP.NET Core app — headless AI backend |
| [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) | Blazor WASM — SignalR client |
| [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) | Optional — Level-2 specialists defined in YAML |

---

## License

Apache License 2.0 — the full text is in the `LICENSE` file inside this package, with the copyright notice in `NOTICE`. Versions up to and including 1.0.0-rc.14 were released under the MIT License, which still applies to them.
