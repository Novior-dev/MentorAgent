# Novior.MentorAgent.Blazor

> **New package IDs.** From 1.0.0 the packages, their namespaces and their assemblies are `Novior.MentorAgent.*`; up to 1.0.0-rc.15 they were `MentorAgent.*`. See *Migrating from the MentorAgent.\* packages* in the [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) README.

**Blazor WebAssembly** client for MentorAgent.
Install this in your Blazor WASM / Blazor Auto **client project**.

Connects to a `Novior.MentorAgent.Server` hub via SignalR and provides the same `<ChatWidget />` experience as Blazor Server — same features, same API, no code changes needed when switching between render modes.

All AI processing happens **server-side** — configured in your server project with `AddMentorAgent()`. The client sends messages, registers page context and UI actions, and receives streaming events.

---

## Table of Contents

- [What MentorAgent can do](#what-mentoragent-can-do)
- [Package Family](#package-family)
- [Getting started](#getting-started)
  - [Installation](#installation)
  - [Server project setup](#server-project-setup)
  - [Client project setup](#client-project-setup)
  - [Stop and confirmations — plain HTTP to the hub's server](#stop-and-confirmations--plain-http-to-the-hubs-server)
  - [Add the widget](#add-the-widget)
- [Widget customization](#widget-customization)
  - [Theme and appearance](#theme-and-appearance)
  - [Language (10 supported)](#language-10-supported)
  - [Voice](#voice)
  - [Onboarding tour](#onboarding-tour)
  - [Generative UI cards](#generative-ui-cards)
  - [Image input (multimodal)](#image-input-multimodal)
  - [RAG citations](#rag-citations)
  - [MCP and A2A status badges](#mcp-and-a2a-status-badges)
  - [Hosted tools badge](#hosted-tools-badge)
- [Page context and UI actions](#page-context-and-ui-actions)
- [Page navigation](#page-navigation)
- [UI Action overloads reference](#ui-action-overloads-reference)
- [`[MentorPage]` parameters (server project)](#mentorpage-parameters-server-project)
- [All `AddMentorAgentBlazor()` options](#all-addmentoragentblazor-options)
- [How it works](#how-it-works)
- [Events — IMentorStateService](#events--imentorstateservice)
  - [Complete event reference](#complete-event-reference)
  - [Sending state changes — the `Notify*` side](#sending-state-changes--the-notify-side)
  - [Custom HITL confirmation dialog](#custom-hitl-confirmation-dialog)
- [Blazor Auto tip](#blazor-auto-tip)
- [Requirements](#requirements)
- [Related Packages](#related-packages)
- [License](#license)

---

## What MentorAgent can do

All features below are available. Configure them server-side in `AddMentorAgent()`.

| Feature | Description |
|---|---|
| 🤖 **Multi-agent orchestration** | Coordinator + specialized agents via Handoff Workflow |
| 👥 **Group Chat teams** | Multiple agents collaborate before acting |
| 🛠️ **Tool discovery** | C# methods become AI tools via `[Description]` or `[MentorAction]` |
| 🎯 **UI Actions** | AI invokes page-level actions (highlight rows, open modals, pre-fill forms) as individually named tools with typed parameters and async support |
| 📖 **Agent Skills** | Domain knowledge loaded on demand (`load_skill`) — progressive disclosure |
| 🧠 **Contextual memory** | Remembers user preferences across sessions |
| 🗺️ **Page navigation** | AI navigates to pages decorated with `[MentorPage]` |
| 📚 **RAG** | Inject relevant documents into every AI response, read through an `IMentorRagSource` you implement on the server |
| 🔌 **MCP Client** | Consume external MCP servers as additional tools |
| 🖥️ **MCP Server** | Expose `[MentorAction]` methods to Claude Desktop, VS Code, Cursor |
| 🌐 **A2A Consumer** | Connect to remote A2A agents in the Handoff workflow |
| 📡 **A2A Server** | Expose as a federatable A2A agent |
| 🎨 **Customizable widget** | Themes, colors, position, avatar, bot name |
| 🌍 **Multi-language** | 10 languages for AI responses and widget UI |
| 🎤 **Voice input/output** | Browser Speech Recognition + Speech Synthesis — speaks while the answer streams, stops when the user takes the floor, optional hands-free loop |
| 🧭 **Onboarding tour** | First-run guide generated on the server from its own pages and tools |
| 🃏 **Generative UI cards** | A server-side tool returns a card and the widget renders it — fields, accent, buttons |
| 🖼️ **Multimodal image input** | Attach images to a message — upload, clipboard paste, drag & drop or URL |
| 🌐 **Hosted tools** | The model provider runs web search, a code-interpreter sandbox and file search — configured server-side, results arrive in the stream |
| 🔒 **Safety check** | AI-based prompt injection detection |
| ⏱️ **Rate limiting** | Per-user message limit |
| ✅ **Confirmation dialogs** | Destructive actions ask for approval (HITL) — including MCP tools, in either the MentorAgent or the Agent Framework native flow |
| 🔐 **Role-based actions** | Actions restricted by ASP.NET Core identity roles |
| 💸 **Token & cost optimization** | Slim cache-friendly prompt, semantic tool filtering, history compaction, RAG/memory gating |

---

## Package Family

| Package | Install when |
|---------|-------------|
| [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) | Blazor Server app |
| [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) | Server project (Web API / Blazor Auto server) |
| **Novior.MentorAgent.Blazor** ← you are here | Blazor WASM / Blazor Auto **client** project |
| [Novior.MentorAgent.Abstractions](https://www.nuget.org/packages/Novior.MentorAgent.Abstractions) | Never directly — it arrives with any of the above |
| [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) | **Optional** — Level-2 specialists in YAML, added on the server project |

---

## Getting started

### Installation

```bash
# Client project
dotnet add package Novior.MentorAgent.Blazor

# Server project — MentorAgent is included automatically as a transitive dependency
dotnet add package Novior.MentorAgent.Server
```

### Server project setup

> **All AI behaviour is configured on the server** (the transitive `Novior.MentorAgent` core): agents, RAG, memory, skills, MCP/A2A **and** the token/cost optimizations below. The client only renders the widget.

```csharp
// Server/Program.cs
builder.Services.AddMentorAgent(options =>
{
    options.AppName        = "My App";
    options.AppDescription = "An order management application";
    options.ChatClient     = chatClient;
    options.ScanAssemblies = [typeof(Program).Assembly];

    // All features configured here: agents, RAG, MCP, A2A, memory, skills...
    options.UseMemoryContext = true;
    options.UseRag           = true;
    options.McpServerEnabled = true;
    options.A2AServerEnabled = true;
    options.EnableSkills     = true;
    options.RateLimitPerUser = 20;
});
builder.Services.AddMentorAgentServer();

app.MapMentorAgentServer();   // /mentor-hub, /mentor/chat + /mentor/approve, /mentor/cancel, /mentor/session, /mentor/tour, /mentor/admin/metrics
app.MapMentorAgentMcp();      // optional
app.MapMentorAgentA2A();      // optional
```

`AddRazorComponents()` and `AddMentorAgentServer()` can come in either order: Blazor's circuits keep their user, and the hub, `/mentor/chat` and A2A get theirs from the request.

#### Token & cost optimization + reliable memory (server project)

These options cut the tokens sent per request and make memory reliable — all on the server. Full details: **[MentorAgent core README → Token & cost optimization](https://www.nuget.org/packages/Novior.MentorAgent)**.

```csharp
builder.Services.AddMentorAgent(options =>
{
    // ...ChatClient, ScanAssemblies as above...

    // Embedding model — powers semantic tool filtering AND semantic memory relevance.
    options.EmbeddingGenerator = new AzureOpenAIClient(endpoint, credential)
        .GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

    // Send only the tools semantically relevant to the message (requires EmbeddingGenerator).
    options.EnableToolFiltering = true;
    options.ToolFilterMinScore  = 0.35f;

    // Compact long conversation history before each call.
    options.EnableCompaction         = true;
    options.CompactionTokenThreshold = 4000;

    // Memory: reliable post-turn fact capture (default true) + inject only relevant memories.
    options.UseMemoryContext         = true;
    options.MemoryAutoCapture        = true;   // default — reliable writer on Path A
    options.MemoryRelevanceFiltering = true;   // requires EmbeddingGenerator
});
```

#### Robustness, observability & cost dashboard (server project)

Also configured on the server — see the [MentorAgent core README](https://www.nuget.org/packages/Novior.MentorAgent) for full details.

```csharp
builder.Services.AddMentorAgent(options =>
{
    // Middleware hooks
    options.OnException = ex => ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase)
        ? "The service is busy, please retry shortly." : null;
    options.ConfigureChatClientPipeline = b => b.UseLogging();

    // Observability (add an OpenTelemetry exporter to the app as usual)
    options.EnableObservability = true;

    // Dashboard cost pricing (supply your own; none built in)
    options.ModelPricing = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o"] = new ModelPrice(2.50m, 10.00m), ["gpt-4o-mini"] = new ModelPrice(0.15m, 0.60m),
    };
});
```

The **admin dashboard** ships as a Blazor component. Drop it on a protected page (you own the authorization). On Blazor Server / Auto it reads `IMentorMetrics` in-process; on standalone WASM, fetch `GET /mentor/admin/metrics` from the server and pass the snapshot:

```razor
@* Blazor Server / Auto — in-process (the component reads IMentorMetrics itself) *@
@attribute [Authorize(Roles = "Admin")]
@using Novior.MentorAgent.Abstractions.Components
<MentorDashboard Currency="$" />
```

```razor
@* Standalone WASM — fetch the snapshot from the server and pass it in *@
@attribute [Authorize(Roles = "Admin")]
@using System.Net.Http.Json
@using Novior.MentorAgent.Abstractions.Components
@using Novior.MentorAgent.Abstractions.Models
@inject HttpClient Http

<MentorDashboard Snapshot="_snapshot" OnRefresh="LoadAsync" Currency="$" />

@code {
    private MentorMetricsSnapshot? _snapshot;
    protected override Task OnInitializedAsync() => LoadAsync();

    // HttpClient must target the server; the endpoint is gated by options.DashboardRole.
    private async Task LoadAsync() =>
        _snapshot = await Http.GetFromJsonAsync<MentorMetricsSnapshot>("mentor/admin/metrics");
}
```

Also configured/available on the server (see the core README for full examples):
- **Rich responses** — with `options.EnableRichResponses` (server, default on) the assistant formats structured data as Markdown; **this widget renders the tables & lists automatically** — no client wiring, XSS-safe, tables scroll horizontally on narrow screens.
- **Model routing** (`options.StrongChatClient` + `options.RoutingStrategy`: `Semantic`/`Classifier`/`Cascade`/`Custom`) — cheap↔strong per turn.
- **Structured outputs** — inject `IMentorStructured` (`GenerateAsync<T>`) for typed results / auto-filled forms.
- **Evaluation** — inject `MentorEvaluator` (wraps the Agent Framework's native `agent.EvaluateAsync`) in tests to gate CI on token/quality regressions; plug `FoundryEvals`/MEAI evaluators for quality & safety.

### Client project setup

```csharp
// Client/Program.cs
using Novior.MentorAgent.Abstractions.Models;   // MentorLanguage, MentorTheme, ChatPosition
using Novior.MentorAgent.Blazor.Extensions;

// Stop and confirmations go through an HttpClient. The standalone WebAssembly template registers one;
// the client project of a Blazor Web App does not.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddMentorAgentBlazor(options =>
{
    options.HubUrl       = "/mentor-hub";    // URL of Novior.MentorAgent.Server hub
    options.BotName      = "My Assistant";
    options.Language     = MentorLanguage.English;
    options.Theme        = MentorTheme.Default;
    options.PrimaryColor = "#2563eb";
});
```

> ⚠️ **`HubUrl` — relative vs absolute.**
> - **Same origin** (Blazor Auto hosted, or WASM served by the same ASP.NET Core host): use a relative path — `options.HubUrl = "/mentor-hub"`. It is resolved against the page's base address, as the browser resolves it: `/mentor-hub` at the origin's root, `mentor-hub` under the app's base path.
> - **Different origin** (standalone WASM on `:5001` connecting to a server on `:5169`): use the server's **absolute URL** — `options.HubUrl = "http://localhost:5169/mentor-hub"` — and configure **CORS** on the server (see the [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) README → *CORS*). Without server-side CORS the SignalR handshake is silently blocked by the browser.

```csharp
// Standalone WASM example — different origin
builder.Services.AddMentorAgentBlazor(options =>
{
    options.HubUrl = "http://localhost:5169/mentor-hub";   // absolute — server on a different port
    options.BotName = "My Assistant";
});
```

### Authenticating the connection — `AccessTokenProvider`

**If your users sign in, set this.** The server resolves the caller from the SignalR
`HubCallerContext.User`, and a browser cannot send an `Authorization` header on a WebSocket or
Server-Sent Events connection — so the token travels the way SignalR expects, and
`AccessTokenProvider` is where you supply it. It is the
WASM equivalent of the JavaScript client's `accessTokenFactory`.

```csharp
var tokenStore = new TokenStore();                    // your own: holds the signed-in user's token
builder.Services.AddSingleton(tokenStore);           // same instance for your login page

builder.Services.AddMentorAgentBlazor(options =>
{
    options.HubUrl = "https://api.example.com/mentor-hub";
    options.AccessTokenProvider = () => Task.FromResult(tokenStore.Token);   // null while signed out
});
```

> ⚠️ **Without it every user is anonymous to the server**, and every `RequiredRoles` action is
> blocked. What else follows depends on the server's `AnonymousIdentity`. With the default
> `PerSession`, each hub connection is its own anonymous user: nothing is shared between people, but
> memory and the `RateLimitPerUser` allowance start over on every reconnect. With `Shared`, every
> user is the single key `"anonymous"`: one rate limit for everybody and **one memory bucket**. With
> `MemoryAutoCapture` on (the default), one person's name and preferences then reach another
> person's prompt.

> **The server must accept the token from the query string.** A browser cannot send an `Authorization`
> header on a WebSocket or Server-Sent Events connection, so SignalR sends the token as
> `?access_token=…`. With JWT bearer on the server, forward it in `OnMessageReceived`, or the hub
> still sees an anonymous caller:
>
> ```csharp
> // Server Program.cs
> builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
>     .AddJwtBearer(options => options.Events = new JwtBearerEvents
>     {
>         OnMessageReceived = ctx =>
>         {
>             var token = ctx.Request.Query["access_token"];
>             if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/mentor-hub"))
>                 ctx.Token = token;
>             return Task.CompletedTask;
>         }
>     });
> ```

> **For the hub, the provider is read on connect and on every reconnect, not per message.** (The
> [Stop and confirmation requests](#stop-and-confirmations--plain-http-to-the-hubs-server) read it
> again each time they are sent.) The widget opens the
> hub connection lazily, when the user sends the first message, and keeps it for the life of the page.
> A sign-in before that first message is picked up automatically. A user who signs in *after* it stays
> on the old, anonymous connection: `MentorHubClient` has no way to restart it, so reload the page
> after sign-in (`Navigation.NavigateTo(Navigation.Uri, forceLoad: true)`) and restore the persisted
> token during startup. A reconnect is a new connection on the server, so it also starts a new
> conversation.

For anything else the connection needs — a custom header, a transport restriction, cookies —
`ConfigureConnection` runs on the same `HttpConnectionOptions` immediately afterwards:

```csharp
options.ConfigureConnection = o => o.Headers["X-Tenant"] = tenantId;
```

### Stop and confirmations — plain HTTP to the hub's server

**■ Stop** (`POST /mentor/cancel`) and the answer to a confirmation (`POST /mentor/approve`) cannot
go through the hub (see [How it works](#how-it-works)), so the widget sends them as HTTP requests.
They follow the hub settings above:

- **Where they go.** When `HubUrl` is an absolute `http(s)` URL, both are posted to the **hub's
  origin** (`https://api.example.com/mentor/approve` for `HubUrl = "https://api.example.com/mentor-hub"`).
  With a relative `HubUrl` (same-origin hosting) they stay relative and resolve against the host's
  `HttpClient.BaseAddress`.
- **What they carry.** When `AccessTokenProvider` is set, each request sends
  `Authorization: Bearer <token>` with the token it returns — the same credential the hub uses. Both
  endpoints accept a Stop or an answer only from the user who opened the hub connection (`401` for an
  anonymous caller, `403` for another user), so this is what lets a signed-in user's Stop and
  confirmations through.

Two things are still yours:

- **Register an `HttpClient`.** The widget sends these requests through the `HttpClient` in your
  client's DI container, and `AddMentorAgentBlazor()` does not register one. The standalone
  WebAssembly template registers one. The client project of a Blazor Web App does not, and without it
  the WebAssembly runtime does not start (`CannotResolveService … HttpClient`): add the line of the
  setup snippet above to its `Program.cs`. With an absolute `HubUrl` the `BaseAddress` does not
  matter for these two calls.
- **CORS, cross-origin.** The server's CORS policy must allow the client's origin for these POSTs as
  well as for the hub, including the `Authorization` header when you send a token. On the server,
  `UseCors` comes before `UseAuthentication` and `UseAuthorization`, and those before `Map…`: with a
  protected hub, any other order blocks the client on another origin (see the
  [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) README → *CORS*).

A failure is only logged (the widget has already closed the prompt), so check the browser console if
Stop or a confirmation seems to be ignored.

### Add the widget

In `MainLayout.razor` or any page:

```razor
@using Novior.MentorAgent.Abstractions.Components

<ChatWidget />
```

**Important — a standalone Blazor WASM app requires manual CSS/JS links in `index.html`.**

Unlike Blazor Server (where the widget injects CSS automatically via `<HeadContent>`), a standalone Blazor WASM app uses a static `index.html` that is served before the .NET runtime starts. Add these two lines to your `wwwroot/index.html`:

```html
<head>
    ...
    <link href="_content/Novior.MentorAgent.Abstractions/css/MentorAgent.css?v=10" rel="stylesheet" />
</head>
<body>
    ...
    <script src="_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js?v=10"></script>
</body>
```

Without this, the widget will render unstyled until after WASM initializes (flash of unstyled content).

> ⚠️ **Keep the `?v=` and bump it on every upgrade.** On Blazor Server the widget writes these tags
> itself and versions them for you; here they are yours, nothing fingerprints them, and a returning
> visitor's browser will reuse the copy it already has. A stale `MentorAgent.js` fails silently and
> selectively — the widget still works, but the calls that did not exist in the older file are
> swallowed by their `try/catch`, so the [onboarding tour](#onboarding-tour) never appears and voice
> falls back to reading the whole answer at the end. If a feature seems missing after an upgrade,
> check the served file before anything else: it should contain `flagGet` and `beginSpeech`.

On Blazor Server, `<ChatWidget />` injects its own CSS and JS automatically — no changes to `_Host.cshtml` or `App.razor` needed. A Blazor Web App (Auto or WebAssembly) has no `index.html`: prerendered — the default — the widget writes the two tags itself, as on Blazor Server; with `prerender: false` add them to the `<head>` of `App.razor`.

---

## Widget customization

### Theme and appearance

```csharp
options.Theme        = MentorTheme.Minimal;   // Default | Dark | Minimal | Custom
options.PrimaryColor = "#7c3aed";             // any hex color
options.Position     = ChatPosition.BottomRight; // BottomRight | BottomLeft | TopRight | TopLeft | SideRight | SideLeft
options.AvatarUrl    = "/my-avatar.png";
options.BotName      = "ShopFlow Assistant";
```

### Welcome message and input

```csharp
options.WelcomeMessage    = "Hello! How can I help you today?";
options.InputPlaceholder  = "Ask anything...";
options.EnableSuggestions = true;   // show suggestion chips in the welcome panel
```

### Language (10 supported)

```csharp
options.Language = MentorLanguage.Italian;
// English | Italian | French | German | Spanish | Portuguese | Dutch | Polish | Japanese | Chinese
```

This sets the **widget's** UI strings only. The language the assistant *answers* in is the server's `options.Language` in `AddMentorAgent()`, and the prompt enforces it whatever language the user writes in. Set both:

```csharp
using Novior.MentorAgent.Abstractions.Models;   // MentorLanguage, in both Program.cs files

// Server Program.cs
builder.Services.AddMentorAgent(options => { /* ... */ options.Language = MentorLanguage.Italian; });

// Client Program.cs
builder.Services.AddMentorAgentBlazor(options => { options.Language = MentorLanguage.Italian; });
```

### Voice

```csharp
options.EnableVoiceInput  = true;   // microphone button (browser Speech Recognition)
options.EnableVoiceOutput = true;   // text-to-speech for AI responses

options.VoiceStreaming    = true;   // default — speak sentence by sentence while the answer streams
options.VoiceBargeIn      = true;   // default — taking the floor stops playback
options.VoiceHandsFree    = false;  // opt-in — keep the conversation going by voice alone
options.VoiceRate         = 1.0;    // 0.5–2.0
```

Unlike image input and the hosted-tools badge below, these are **not** a mirror of server settings. Voice is entirely browser behaviour: your server sees only the transcript, as an ordinary message, and these options are the real thing rather than a copy of something the server enforces. The audio is the browser's business: some browsers, Chrome among them, send it to their maker's speech service for recognition ([MDN, *Using the Web Speech API*](https://developer.mozilla.org/en-US/docs/Web/API/Web_Speech_API/Using_the_Web_Speech_API)).

**`VoiceStreaming`** is what makes voice output usable on anything longer than a sentence. Without it the assistant stays silent for the whole response and then recites it. Text is buffered to a sentence boundary and queued as its own utterance, so speech keeps pace with the stream — and the boundary detection knows that `8.459` is one number and that a fenced code block must be dropped whole rather than read out.

**`VoiceBargeIn`** stops playback when the user presses the microphone, sends a message, presses **■ Stop** or starts a new conversation. It is press-to-interrupt, not acoustic: a browser cannot listen through its own playback without echo cancellation.

**`VoiceHandsFree`** sends on silence and reopens the microphone after the spoken answer ends. It needs both voice options on — with nothing to listen to there is nothing to wait for, and the loop would transcribe the assistant. If you set only one, it is ignored.

### Onboarding tour

```csharp
options.EnableOnboardingTour = true;   // shown once, on this user's first open of the widget
options.TourUrl = "/mentor/tour";      // default — only change it if you remapped the server endpoints
```

The steps are **generated on the server** — only it knows the registered `[MentorPage]` pages and the assistant's tools — and fetched from `GET /mentor/tour`, which `MapMentorAgentServer()` maps whatever the server's own `EnableOnboardingTour` says. The flag on this client alone decides whether to show the tour; the server needs only the `[MentorPage]` pages, and with none it answers `[]`.

That split follows the same rule as the hosted-tools badge: the server is the single source of truth, and a client that reproduces server state locally eventually displays something the server no longer agrees with.

Each step can carry a ready-made question the user sends with one tap — which is the point, since the usual failure of an in-app assistant is not that people cannot find it but that they do not know what to ask. A **?** button in the header replays the tour later.

If the fetch fails the widget opens normally and logs a warning: a missing tour is a missing nicety, not a broken chat.

### Generative UI cards

Nothing to configure on the client. When a server-side tool returns a `MentorCard`, the server pushes it on the `Cards` hub event and the widget renders it under the reply — fields, accent colour and buttons.

Buttons work the same as in Blazor Server: `SendMessage` sends the text as a user turn, `Navigate` routes to the URL, `UIAction` runs an action the **current** page registered (resolved at click time, so a stale handler from a page you have navigated away from is skipped rather than invoked).

To render a card kind yourself, supply a template and fall back to the built-in renderer for the rest:

```razor
<ChatWidget>
    <CardTemplate Context="card">
        @if (card.Kind == "order") { <OrderCard Card="card" /> }
        else                       { <MentorCardView Card="card" /> }
    </CardTemplate>
</ChatWidget>
```

The fallback's buttons work without any wiring: `ChatWidget` cascades its own card-action dispatcher around the cards, and a `<MentorCardView>` inside the template that sets no `OnAction` uses it, with the same `SendMessage` / `Navigate` / `UIAction` behaviour as above. An explicit `OnAction` on the `MentorCardView` still wins. The dispatcher reaches only `MentorCardView`: buttons you draw yourself in a template (as `OrderCard` might) get nothing from it, so render a `MentorCardView` for any card whose buttons should behave like the built-in ones.

Cards are built by server-side application code, not by the model, which is why they can safely carry buttons: nothing said in the conversation can add one or change where it points.

### Image input (multimodal)

Lets the user attach images to a message. The widget accepts them **four ways** — 📎 file picker, `Ctrl+V` clipboard paste, drag & drop onto the composer, and 🔗 remote URL — and shows removable thumbnails before sending and inside the message bubble afterwards.

```csharp
// Client Program.cs — mirrors the server settings (the server is what actually enforces them)
builder.Services.AddMentorAgentBlazor(options =>
{
    options.EnableImageInput    = true;
    options.MaxImageBytes       = 4 * 1024 * 1024;                            // per image (default 4 MB)
    options.MaxImagesPerMessage = 4;                                          // per turn  (default 4)
    options.AllowedImageTypes   = ["image/png", "image/jpeg", "image/webp"];  // MIME allow-list
});
```

The **server** must also enable it, with a vision-capable model:

```csharp
// Server Program.cs
builder.Services.AddMentorAgent(options =>
{
    // ...AppName, ScanAssemblies as above...
    options.ChatClient       = azure.GetChatClient("gpt-4.1").AsIChatClient();   // vision-capable
    options.EnableImageInput = true;
});
```

The client values only drive the UI and give the user instant feedback; every attachment is **re-validated server-side** (allow-list, size, count) before it reaches the model. Images travel over the hub as the second argument of `SendMessage(text, attachments)`. The client always sends both arguments, with `null` for a text-only turn, because SignalR binds hub arguments by count. The images ride inside that one hub message as base64, so `AddMentorAgentServer()` raises `MentorHub`'s `MaximumReceiveMessageSize` from the **server's** `MaxImageBytes` × `MaxImagesPerMessage` (+512 KB). Keep the client values at or below the server's: a message larger than the hub limit makes SignalR abort the connection before the server can validate it, and the user sees their own bubble and no reply.

Sending images from your own code:

```csharp
@inject IMentorOrchestrator Orchestrator

await Orchestrator.SendMessageAsync("Cosa non va in questo screenshot?", [
    new MentorAttachment { MimeType = "image/png",  DataBase64 = base64, FileName = "error.png" },
    new MentorAttachment { MimeType = "image/jpeg", Url = "https://cdn.example.com/product.jpg" },
]);
```

> Image bytes are streamed to .NET through `IJSStreamReference`, never marshalled as one big interop payload — so this works unchanged on Blazor Server too, without touching `HubOptions.MaximumReceiveMessageSize`.

### RAG citations

```csharp
options.ShowRagSources = true;   // show citation chips below AI responses
```

### MCP and A2A status badges

When the server has MCP client servers or remote A2A agents configured, the widget can display live status badges in the header. Because the WASM client doesn't read the server configuration directly, you must mirror the relevant settings:

```csharp
// Client Program.cs
builder.Services.AddMentorAgentBlazor(options =>
{
    // MCP badge — mirror McpServers names from the server
    options.ShowMcpStatus  = true;
    options.HasMcpServers  = true;
    options.McpServerNames = ["time", "filesystem"];  // must match server McpServers[].Name

    // A2A badge — mirror RemoteAgents from the server
    options.ShowA2AStatus       = true;
    options.HasRemoteAgents     = true;
    options.RemoteAgentDisplays = [
        new AgentDisplayInfo { Name = "ShopFlow-B", AgentCardUrl = "http://localhost:5001" }
    ];
});
```

The MCP badge updates dynamically — when a server connects or disconnects the hub fires `McpServerStatusChanged` and the badge turns green/red. The A2A badge is static (shows configured agents, no live status).

> **Note**: `McpServerNames` must match the `Name` fields in `MentorMcpServer[]` configured on the server. A mismatch shows a stale "connecting" badge.

### Hosted tools badge

If the server enables [hosted tools](https://www.nuget.org/packages/Novior.MentorAgent.Server) (provider-side web search, code interpreter, file search, image generation, remote MCP), an amber pill lists them in the header. **Do not list them here** — turn the badge on and let the server say what it enabled:

```csharp
options.ShowHostedToolsStatus = true;   // that's all
```

The server sends its active set on the `HostedToolsDeclared` hub event the moment the client connects, and the widget prefers it over any local value. On WebAssembly the connection opens when the user sends the first message, so until then the badge shows `options.HostedTools` (nothing, by default). Setting `options.HostedTools` by hand still works as a pre-connection placeholder, but it is a copy that goes stale: change the server's configuration and the badge starts claiming tools that are not there — which is worse than no badge, because it is believed.

Unlike MCP the badge has no live status afterwards: hosted tools are configuration, not a connection.

**Live activity needs no configuration here.** When the server has `ShowHostedToolActivity` on, the widget already shows what the provider is doing, over the hub events it consumes anyway:

- *"Ricerca sul web… · .NET 10"* in the feedback line, via `ActionExecuting` / `ActionCompleted`
- pages cited by web search as citation chips, via `RagSourcesReady` — the same panel as RAG, so it also needs `ShowRagSources`
- generated images attached to the finished message, via the `GeneratedImages` event

The WASM client handles all three out of the box.

---

## Page context and UI actions

`IMentorPageContext` works identically to Blazor Server. Register context data and UI actions in any page — they are sent to the server as a snapshot before each AI message.

### Inject page context

```razor
@inject IMentorPageContext PageContext
@implements IDisposable

@code {
    protected override void OnInitialized()
    {
        PageContext
            .SetPageName("Orders")
            .Set("ActiveFilter", "Pending")
            .Set("VisibleRows", _orders.Count);
    }

    public void Dispose() => PageContext.Clear("Orders");   // this page's entries only, by its name
}
```

### Register UI actions (no parameter)

```csharp
PageContext.RegisterUIAction(
    "open_create_modal",
    "Opens the modal to create a new order",
    _ => OpenCreateModal());
```

### Register UI actions with typed parameter

```csharp
// The AI calls highlight_row(42) — parameter deserialized automatically
PageContext.RegisterUIAction<int>(
    "highlight_row",
    "Highlights the specified order row",
    id => HighlightRow(id),
    parameterHint: "integer: order ID");
```

### Async UI actions

```csharp
PageContext.RegisterUIActionAsync<OrderModel>(
    "prefill_form",
    "Pre-fills the edit form with order data",
    async model => {
        _formModel = model;
        await InvokeAsync(StateHasChanged);
    },
    parameterHint: "JSON: { orderId, amount, status }");
```

### Remove an action

```csharp
PageContext.UnregisterUIAction("highlight_row");
```

### Signal page ready (Blazor Server and MAUI Blazor Hybrid only)

**On WebAssembly `SignalReady()` is a no-op** and costs nothing, so you can keep the call in pages shared with a Blazor Server host:

```csharp
protected override async Task OnInitializedAsync()
{
    await LoadDataAsync();
    PageContext.SignalReady();   // Blazor Server, MAUI Blazor Hybrid: tells navigate_to the UI actions are ready. WASM: no-op
}
```

What matters on WebAssembly is *when the server learns about the page*. Its name, data and UI actions reach the server only with the page-context snapshot sent before the user's next message (`UpdatePageContext`). So when the assistant moves the user with `navigate_to`, the server does **not** wait for the new page — even on a `[MentorPage]` with `HasUIActions = true`, because on a hub connection nothing could ever signal it. The new page's UI actions become callable from the user's **next** message, not later in the same answer.

(On Blazor Server and in MAUI Blazor Hybrid, where `navigate_to` does wait on `HasUIActions` pages, call `SignalReady()` as the last line of `OnInitialized` / `OnInitializedAsync`, not from `OnAfterRenderAsync`, or the wait may already have timed out.)

---

## Page navigation

> ⚠️ `[MentorPage]` attributes are defined in the **server project** (scanned by `ScanAssemblies`), not in the client project.

```csharp
// Server project — scanned via options.ScanAssemblies
[MentorPage(Url = "/orders", Name = "Orders", Description = "Order management")]
public class OrdersPage { }

[MentorPage(Url = "/products", Name = "Products", HasUIActions = true, ReadyTimeout = 3000)]
public class ProductsPage { }
```

The AI calls `navigate_to("/orders")` — the WASM widget handles navigation automatically via Blazor's `NavigationManager`.

`HasUIActions` and `ReadyTimeout` only affect hosts where the page runs in the same process as the turn: Blazor Server, or a MAUI Blazor Hybrid app. For a turn that arrives over the hub, `navigate_to` returns without waiting for `SignalReady` (it logs this at Debug level), and the new page's UI actions reach the model with the next page-context snapshot, on the user's next message — see [Signal page ready](#signal-page-ready-blazor-server-and-maui-blazor-hybrid-only).

---

## UI Action overloads reference

Four overloads are available, from simple to fully typed and async:

| Overload | Parameter | Execution | Use when |
|---|---|---|---|
| `RegisterUIAction(name, desc, Action<object?>)` | Raw `object?` | Synchronous | Simple no-param or legacy code |
| `RegisterUIAction<TParam>(name, desc, Action<TParam>)` | Auto-deserialized from JSON | Synchronous | Typed param, sync handler |
| `RegisterUIActionAsync(name, desc, Func<object?, Task>)` | Raw `object?` | Async | No-param async actions (e.g. `async _ => { await LoadAsync(); }`) |
| `RegisterUIActionAsync<TParam>(name, desc, Func<TParam, Task>)` | Auto-deserialized from JSON | Async | Typed param, async handler *(recommended)* |
| `UnregisterUIAction(name)` | — | — | Remove a specific action dynamically |

### Automatic `parameterHint` generation

For typed overloads, `parameterHint` is auto-generated from the type when omitted:

| `TParam` | Auto-generated hint |
|---|---|
| `int`, `long` | `"integer"` |
| `float`, `double`, `decimal` | `"number"` |
| `bool` | `"boolean"` |
| `string` | `"string"` |
| `Guid` | `"string (GUID)"` |
| `DateTime` | `"string (ISO 8601 date)"` |
| `Status` *(enum)* | `"string (Active\|Inactive\|Pending)"` |
| `List<int>` | `"array<integer>"` |
| `OrderFormModel` *(class)* | `"{ customerId: integer, productName: string, ... }"` |

Override only when extra clarity is needed:
```csharp
.RegisterUIAction<int>(
    "highlight_row", "Highlights an order row",
    id => HighlightRow(id),
    parameterHint: "integer: order ID")  // ← manual override
```

> **On WebAssembly the AI does not wait for your handler.** The server forwards the call as `UIActionRequested` and reports the action complete as soon as it is sent. The browser then runs the handler and reports nothing back. As a result, an exception in the handler never reaches the model, and `OnUIActionCompleted` can fire before an async handler has finished. Make each action self-contained (load what it needs inside the handler) rather than relying on the model to chain actions that depend on the previous one having finished. (On Blazor Server, where the handler runs in-process, async handlers are awaited.)

---

## `[MentorPage]` parameters (server project)

| Parameter | Required | Description |
|---|---|---|
| `Url` | ✅ | Page URL (e.g. `"/orders"`) |
| `Name` | ✅ | Human-readable page name injected into the system prompt |
| `Description` | — | Optional feature description |
| `HasUIActions` | — | If `true`, `navigate_to` waits for `PageContext.SignalReady()` before UI actions — **only where the page runs in the turn's process: Blazor Server or MAUI Blazor Hybrid**. On a hub turn from this WASM client it does not wait: the page's actions arrive with the user's next message. Default: `false` |
| `ReadyTimeout` | — | Timeout in ms for `SignalReady()` (Blazor Server and MAUI Blazor Hybrid only). Default: `2000` |

---

## All `AddMentorAgentBlazor()` options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `HubUrl` | `string` | `"/mentor-hub"` | URL of the Novior.MentorAgent.Server SignalR hub. A relative value is resolved against the page's base address. When absolute (`http(s)`), its origin is also where Stop (`/mentor/cancel`) and confirmation answers (`/mentor/approve`) are posted |
| `AccessTokenProvider` | `Func<Task<string?>>?` | `null` | Bearer token for the hub, read on connect and on every reconnect, and sent as `Authorization: Bearer` on every Stop and confirmation request. **Without it every user is anonymous**: `RequiredRoles` blocked; memory and rate limit are per connection (server default `AnonymousIdentity = PerSession`) or one shared bucket (`Shared`) |
| `ConfigureConnection` | `Action<HttpConnectionOptions>?` | `null` | Escape hatch on the hub's connection options — headers, transports, cookies. Runs after `AccessTokenProvider` |
| `BotName` | `string` | `"Mentor AI"` | Bot name in the widget header |
| `WelcomeMessage` | `string?` | `null` | Welcome message (HTML supported) |
| `AvatarUrl` | `string?` | `null` | Custom avatar URL |
| `InputPlaceholder` | `string?` | `null` | Input box placeholder. `null` uses the localized text in `Language` ("Write a message..." in English) |
| `Theme` | `MentorTheme` | `Default` | Widget visual theme |
| `Position` | `ChatPosition` | `BottomRight` | Widget position on screen |
| `PrimaryColor` | `string?` | `null` | Custom hex accent color |
| `Language` | `MentorLanguage` | `English` | Language for widget UI strings (10 languages supported) |
| `EnableVoiceInput` | `bool` | `false` | Show microphone button (browser Speech Recognition) |
| `EnableVoiceOutput` | `bool` | `false` | Text-to-speech for AI responses (browser Speech Synthesis) |
| `VoiceStreaming` | `bool` | `true` | Speak each sentence as it streams instead of reading the finished answer back |
| `VoiceBargeIn` | `bool` | `true` | Stop playback when the user takes the floor |
| `VoiceHandsFree` | `bool` | `false` | Voice-only conversation loop. Ignored unless both voice options are on |
| `VoiceRate` | `double` | `1.0` | `SpeechSynthesisUtterance.rate` — useful range 0.5–2.0 |
| `EnableOnboardingTour` | `bool` | `false` | Show the [guided tour](#onboarding-tour) on first open. This flag alone decides: the server answers `GET /mentor/tour` whatever its own `EnableOnboardingTour` says |
| `TourUrl` | `string` | `"/mentor/tour"` | Where to fetch the tour steps from. A relative value is resolved against `HubUrl`'s origin when `HubUrl` is absolute (the tour is served by the same server as the hub), otherwise against the app base address. An absolute URL is used as-is |
| `EnableSuggestions` | `bool` | `false` | Suggestion chips in the welcome panel |
| `EnableImageInput` | `bool` | `false` | [Image attachments](#image-input-multimodal) — 📎 upload, paste, drag & drop and 🔗 URL. Must also be enabled server-side |
| `MaxImageBytes` | `int` | `4194304` | Client-side size cap per image (4 MB). Mirrors the server option |
| `MaxImagesPerMessage` | `int` | `4` | Client-side cap on images per message. Mirrors the server option |
| `AllowedImageTypes` | `List<string>` | `png, jpeg, gif, webp` | Client-side MIME allow-list. Mirrors the server option |
| `ShowRagSources` | `bool` | `false` | Citation chips below AI responses |
| `ShowHostedToolsStatus` | `bool` | `false` | Show the [hosted-tools badge](#hosted-tools-badge) (provider-side web search / code interpreter / file search / image generation / remote MCP) |
| `HostedTools` | `MentorHostedTools` | `None` | Pre-connection placeholder only. The server publishes its **actual** set on `HostedToolsDeclared` at connect and the widget prefers that — leave this unset rather than keeping a copy that goes stale |
| `ShowMcpStatus` | `bool` | `false` | Show MCP server connection status badge in the widget header |
| `HasMcpServers` | `bool` | `false` | Whether the server has MCP client servers configured (enables the MCP badge) |
| `McpServerNames` | `List<string>` | `[]` | Names of MCP servers configured on the server — pre-populates the badge in "connecting" state at startup |
| `ShowA2AStatus` | `bool` | `false` | Show A2A remote agent status badge in the widget header |
| `HasRemoteAgents` | `bool` | `false` | Whether the server has remote A2A agents configured (enables the A2A badge) |
| `RemoteAgentDisplays` | `List<AgentDisplayInfo>` | `[]` | Remote A2A agents to display in the A2A badge and detail bar |

---

## How it works

```
[Blazor WASM Browser]
  ChatWidget
    ↓ IMentorOrchestrator (WasmMentorOrchestrator)
    ↓ SignalR
[ASP.NET Core Server — Novior.MentorAgent.Server]
  MentorHub
    ↓ MentorOrchestrator (full AI pipeline)
    ↓ AI Provider (Azure OpenAI, OpenAI, Ollama...)
    ↑ Streaming events (chunks, confirmations, navigation, UI actions...)
    ↑ SignalR
  ChatWidget renders response
```

- **Page context** (page name, data, UI action descriptions) is sent to the server before every message via `UpdatePageContext`.
- **UI action invocations** arrive from the server as `UIActionRequested` and are executed locally in the browser by `WasmMentorStateService`.
- **Confirmation dialogs** (HITL) are shown inline by `ChatWidget`. The response is sent via `POST /mentor/approve?actionId=...&approved=true|false` (HTTP) — **not** via a hub method. SignalR processes hub messages sequentially per connection, so calling `RespondToApproval` via hub while `SendMessage` is awaiting would deadlock. `WasmMentorStateService` sends it to the hub's origin with the hub's bearer token — see [Stop and confirmations](#stop-and-confirmations--plain-http-to-the-hubs-server).
- **Stop** (cancelling an in-flight turn) is sent via `POST /mentor/cancel?connectionId=...` (HTTP) — **not** via the `CancelRequest` hub method, for the same sequential-dispatch reason: while `SendMessage` is streaming, SignalR cannot dispatch another hub invocation on that connection, so the hub call would only run *after* the turn it was meant to abort. `WasmMentorOrchestrator` sends it the same way as the approval: to the hub's origin, with the hub's bearer token.
- **UI actions after `navigate_to`**: the server does not wait for the new page (nothing on a hub connection can call `SignalReady`); the page's actions reach the model with the next `UpdatePageContext`, on the user's next message.
- **Navigation** triggered by the AI arrives as `NavigationRequested` and is handled by Blazor's `NavigationManager`.

### Conversation lifetime and session snapshots

The conversation lives on the server, in a DI scope that belongs to one SignalR connection. A reconnect (network drop, server restart, a laptop waking up) gets a new connection id and therefore a **new, empty conversation** on the server, even though the widget still shows the old messages.

`IMentorOrchestrator.SerializeSessionAsync` / `RestoreSessionAsync` **throw `NotSupportedException` on WebAssembly**, because the `AgentSession` is not in the browser. Save and restore it over HTTP instead, with the live connection id. That id is `null` until the first message opens the connection.

```csharp
@inject Novior.MentorAgent.Blazor.Hub.MentorHubClient Hub
// BaseAddress = the server, carrying the user's token
@inject HttpClient Http

// Save — 204 means there is no conversation yet: keep what you already have
var save = await Http.GetAsync($"mentor/session?connectionId={Uri.EscapeDataString(Hub.ConnectionId!)}");
if (save.StatusCode == HttpStatusCode.OK)
    _saved = await save.Content.ReadAsStringAsync();

// Restore on the new connection (after its first message; an earlier request can get 404)
await Http.PostAsync($"mentor/session?connectionId={Uri.EscapeDataString(Hub.ConnectionId!)}",
    new StringContent(_saved, Encoding.UTF8, "application/json"));
```

Both endpoints check that the caller is the user who opened the connection (`401`/`403` otherwise), before the body is read. The snapshot is opaque — protected with ASP.NET Core Data Protection and bound to that user (on a host without sign-in every visitor is the same anonymous user, so there it is bound to the application only) — so store it as the text `GET` returned. A body that is not a snapshot this server issued for this user, or was edited, or was saved before 1.0.0-rc.14, is refused with `400` and leaves the live conversation untouched. Restoring after a server restart needs the server's Data Protection key ring persisted (see the Novior.MentorAgent.Server README).

---

## Events — IMentorStateService

`ChatWidget` handles all events automatically. If you need to subscribe to events directly in your own components, inject `IMentorStateService`:

```csharp
@inject IMentorStateService State
@implements IDisposable

protected override void OnInitialized()
{
    State.OnStreamingChunk        += OnChunk;
    State.OnStreamingCompleted    += OnCompleted;
    State.OnBusyChanged           += OnBusy;
    State.OnError                 += OnError;
    State.OnActionExecuting       += OnActionExecuting;
    State.OnActionCompleted       += OnActionCompleted;
    State.OnActionFailed          += OnActionFailed;
    State.OnConfirmationRequired  += OnConfirmationRequired;
    State.OnNavigationRequested   += OnNavigation;
    State.OnRagSourcesReady       += OnRagSources;
    State.OnTeamMemberSpeaking    += OnTeamSpeaking;
    State.OnUIActionExecuting     += OnUIActionStart;
    State.OnUIActionCompleted     += OnUIActionEnd;
}

public void Dispose()
{
    State.OnStreamingChunk        -= OnChunk;
    // ... unsubscribe all
}
```

### Complete event reference

| Event | Signature | Fired when | Typical use |
|-------|-----------|-----------|-------------|
| `OnStreamingChunk` | `Action<string>` | Each streaming token | Append text to a custom chat bubble |
| `OnStreamingCompleted` | `Action` | Full response received | Finalize message, re-enable input |
| `OnBusyChanged` | `Action<bool>` | AI starts/stops processing | Show/hide spinner |
| `OnError` | `Action<string>` | Critical error (rate limit, safety block) | Show error banner |
| `OnActionExecuting` | `Action<string>` | Tool/agent is executing. Carries the text to show (the action's `Description`, or the routing, team or hosted-tool label), not the tool name | Show action feedback bar. To log which tool ran, use `OnActionCompleted`, which carries the tool name |
| `OnActionCompleted` | `Action<string>` | Tool execution succeeded | Hide feedback bar |
| `OnActionFailed` | `Action<string>` | Tool execution failed — `tool: message`; with the server's `OnException` set, never the exception's own text | Show error in feedback |
| `OnConfirmationRequired` | `Action<ConfirmationRequest>` | Destructive action needs user approval | Show custom confirmation dialog |
| `OnApprovalResponse` | `Action<ConfirmationRequest, bool>` | The answer reached the waiting turn: raised once the server accepted it, never after Stop or for a second answer | An audit trail of decisions |
| `OnNavigationRequested` | `Action<string>` | AI triggered navigation | Custom routing logic |
| `OnRagSourcesReady` | `Action<IReadOnlyList<MentorRagResult>>` | RAG documents the finished answer cited (plus hosted web/file-search citations); not raised when nothing was cited | Show custom citation UI |
| `OnTeamMemberSpeaking` | `Action<string, string>` | GroupChat member speaking | Show "Team · Role" feedback |
| `OnUIActionExecuting` | `Action<string>` | UI action started | Custom feedback |
| `OnUIActionCompleted` | `Action<string>` | UI action completed | Custom feedback |
| `OnMcpServerStatusChanged` | `Action<string, bool>` | MCP server connects (`true`) or disconnects (`false`) | Update the MCP status badge — forwarded over the hub as `McpServerStatusChanged` |
| `OnCardsReady` | `Action<IReadOnlyList<MentorCard>>` | A tool returned generative-UI cards | Render them yourself instead of using `CardTemplate` |
| `OnGeneratedImages` | `Action<IReadOnlyList<string>>` | The hosted image tool produced images | Show them in your own gallery. Each string is a `data:` URI or URL |
| `OnHostedToolsDeclared` | `Action<MentorHostedTools>` | Once per connection, right after connect | Drive your own capability badge from what the **server** actually enabled, rather than from a client-side guess |

```razor
@inject IMentorStateService State
@inject IMentorOrchestrator Orchestrator
@inject NavigationManager Nav
@implements IDisposable

@if (_capabilities.HasFlag(MentorHostedTools.WebSearch)) { <span class="badge">🌐 web</span> }

@foreach (var url in _images) { <img src="@url" alt="generata" /> }
@* Outside ChatWidget no dispatcher is cascaded: without OnAction the buttons do nothing *@
@foreach (var c in _cards)    { <MentorCardView Card="c" OnAction="OnCardAction" /> }

<span>MCP: @_mcp.Count(x => x.Value)/@_mcp.Count</span>

@code {
    private MentorHostedTools _capabilities;
    private IReadOnlyList<string> _images = [];
    private IReadOnlyList<MentorCard> _cards = [];
    private readonly Dictionary<string, bool> _mcp = new();

    protected override void OnInitialized()
    {
        State.OnHostedToolsDeclared    += Declared;
        State.OnGeneratedImages        += Images;
        State.OnCardsReady             += Cards;
        State.OnMcpServerStatusChanged += Mcp;
    }

    // Fires once per connection, before any message. Render your capability badge from THIS rather
    // than from a client-side copy of the server's configuration — the two drift, and a badge
    // claiming web search the model never got is worse than no badge.
    private void Declared(MentorHostedTools flags) { _capabilities = flags; InvokeAsync(StateHasChanged); }

    private void Images(IReadOnlyList<string> urls) { _images = urls; InvokeAsync(StateHasChanged); }

    // Only if you are NOT using ChatWidget's CardTemplate. Ignoring cards loses them outright: the
    // model is told they are already on screen, so it will not repeat their content as text.
    private void Cards(IReadOnlyList<MentorCard> cards) { _cards = cards; InvokeAsync(StateHasChanged); }

    private async Task OnCardAction(MentorCardAction action)
    {
        if (action.Kind == MentorCardActionKind.SendMessage) await Orchestrator.SendMessageAsync(action.Value);
        else if (action.Kind == MentorCardActionKind.Navigate) Nav.NavigateTo(action.Value);
        // UIAction: run the current page's registered action named action.Value, as ChatWidget does
    }

    private void Mcp(string name, bool connected) { _mcp[name] = connected; InvokeAsync(StateHasChanged); }

    public void Dispose()
    {
        State.OnHostedToolsDeclared    -= Declared;
        State.OnGeneratedImages        -= Images;
        State.OnCardsReady             -= Cards;
        State.OnMcpServerStatusChanged -= Mcp;
    }
}
```

All eighteen events are listed above — that is the whole of `IMentorStateService`'s read side.

> All events fire on a **background thread** from the SignalR connection. Always use `InvokeAsync(StateHasChanged)` when updating Blazor component state from these handlers.

### Sending state changes — the `Notify*` side

Each event has a matching `Notify*` method on the same interface (`NotifyStreamingChunk`,
`NotifyCardsReady`, …). Those are how the **orchestrator** raises the events; application code
subscribes and does not call them.

The two members you do call are the HITL replies:

| Method | Purpose |
|---|---|
| `ConfirmAsync(Guid actionId)` | Approve the pending action |
| `Cancel(Guid actionId)` | Reject it |

`actionId` is `ConfirmationRequest.ActionId` from `OnConfirmationRequired`. In WASM these travel to
the server over `POST /mentor/approve` (at the hub's origin, with the `AccessTokenProvider` token —
see [Stop and confirmations](#stop-and-confirmations--plain-http-to-the-hubs-server)), never as a hub method — SignalR dispatches one hub call at a
time per connection, so an approval sent through the hub would deadlock behind the turn that is
waiting for it.

### Custom HITL confirmation dialog

`ChatWidget` always shows its own inline confirmation prompt when `OnConfirmationRequired` fires, and no parameter turns it off. Subscribing therefore adds your dialog *next to* the widget's prompt instead of replacing it. Answering from your dialog also does not close the widget's prompt, which reacts only to its own buttons. To show only your dialog, hide the built-in prompt with CSS (`.bm-confirm { display: none !important; }`: the package stylesheet sets `display: flex` on the same selector), then subscribe to `OnConfirmationRequired` and call `ConfirmAsync` / `Cancel` yourself.

> This works identically whichever `MentorOptions.HitlMode` the server uses (`Blocking` or the Agent Framework's `Native` flow) — the WASM client sees the same `ConfirmationRequired` event and replies over the same `POST /mentor/approve` endpoint, so switching mode server-side needs no client change.

```razor
@inject IMentorStateService State
@implements IDisposable

@if (_pendingConfirmation is not null)
{
    <div class="my-confirm-dialog">
        <p>@_pendingConfirmation.Message</p>
        <button @onclick="Approve">Conferma</button>
        <button @onclick="Reject">Annulla</button>
    </div>
}

@code {
    private ConfirmationRequest? _pendingConfirmation;

    protected override void OnInitialized()
    {
        State.OnConfirmationRequired += OnConfirmationRequired;
        State.OnBusyChanged          += OnBusyChanged;
    }

    private void OnConfirmationRequired(ConfirmationRequest req)
        => InvokeAsync(() => { _pendingConfirmation = req; StateHasChanged(); });

    // The turn is over (answered, stopped or failed): nothing waits for this dialog any more.
    private void OnBusyChanged(bool busy)
    {
        if (!busy) InvokeAsync(() => { _pendingConfirmation = null; StateHasChanged(); });
    }

    private async Task Approve()
    {
        if (_pendingConfirmation is not { } req) return;
        _pendingConfirmation = null;
        await State.ConfirmAsync(req.ActionId);
    }

    private void Reject()
    {
        if (_pendingConfirmation is not { } req) return;
        _pendingConfirmation = null;
        State.Cancel(req.ActionId);
    }

    public void Dispose()
    {
        State.OnConfirmationRequired -= OnConfirmationRequired;
        State.OnBusyChanged          -= OnBusyChanged;
    }
}
```

Close the dialog on the click and when the turn ends, as here. `OnApprovalResponse` is raised only when
the answer reached a turn that was waiting for it, so it suits an audit trail of decisions, not closing
a dialog: a click after Stop raises nothing.

```csharp
// An audit trail: one entry per answer that a waiting turn received
State.OnApprovalResponse += (req, approved) =>
    logger.LogInformation("{Tool} {Decision} by the user", req.ToolName, approved ? "approved" : "rejected");
```

> `ConfirmationRequest` has three properties: `ActionId` (`Guid`), `ToolName` (`string`), and `Message` (`string`). Use `ToolName` to customize the dialog copy per action type if needed.

---

## Blazor Auto tip

In a Blazor Auto app, you can keep `<ChatWidget />` with `@rendermode="InteractiveServer"` — it stays server-side with no changes to your existing `Novior.MentorAgent` setup. Use `Novior.MentorAgent.Blazor` only when you need the widget to run fully in WebAssembly.

This needs the template's *Interactivity location: Per page/component*. With *Global* the layout runs under Auto and, once in WebAssembly, cannot host a server component (`NotSupportedException`): use `Novior.MentorAgent.Blazor` there. In the WebAssembly template, first add `.AddInteractiveServerComponents()` and `.AddInteractiveServerRenderMode()` to the server's `Program.cs`, or every page fails with HTTP 500.

```razor
@* Keep server-side in Blazor Auto — zero changes needed *@
<ChatWidget @rendermode="InteractiveServer" />
```

---

## Requirements

- .NET 10.0+
- **Server project** must have `Novior.MentorAgent` + `Novior.MentorAgent.Server` installed and configured
- Browser with WebAssembly support

---

## Related Packages

| Package | Purpose |
|---------|---------|
| [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) | Blazor Server app — AI orchestration engine |
| [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) | Any ASP.NET Core backend — SignalR hub + SSE + MCP + A2A |
| [Novior.MentorAgent.Abstractions](https://www.nuget.org/packages/Novior.MentorAgent.Abstractions) | Shared UI components (transitive dep — no need to install directly) |
| [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) | **Optional** — define Level-2 specialist agents in YAML instead of C# |

---

## License

Apache License 2.0 — the full text is in the `LICENSE` file inside this package, with the copyright notice in `NOTICE`. Versions up to and including 1.0.0-rc.14 were released under the MIT License, which still applies to them.
