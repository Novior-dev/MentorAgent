# Novior.MentorAgent.Server

> **New package IDs.** From 1.0.0 the packages, their namespaces and their assemblies are `Novior.MentorAgent.*`; up to 1.0.0-rc.15 they were `MentorAgent.*`. See *Migrating from the MentorAgent.\* packages* in the [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) README.

Expose a fully-featured AI assistant backend from **any ASP.NET Core application** — Web API, Minimal API, Blazor Server. No Blazor required on the consumer side.

`Novior.MentorAgent.Server` adds a SignalR hub, an SSE streaming endpoint, the MCP server and the A2A agent — making your AI backend reachable from any ASP.NET Core application and any client: React, Vue, Angular, MAUI, mobile apps, or any HTTP/SignalR consumer. It is also the required server-side component when pairing with `Novior.MentorAgent.Blazor` for Blazor WebAssembly and Blazor Auto deployments.

---

## Table of Contents

- [Package Family](#package-family)
- [What Novior.MentorAgent.Server exposes](#what-noviormentoragentserver-exposes)
- [Getting started](#getting-started)
- [CORS — required for cross-origin clients](#cors--required-for-cross-origin-clients)
- [Connecting clients](#connecting-clients)
  - [Option A — SSE](#option-a--sse-simplest-no-library-required)
  - [Option B — SignalR](#option-b--signalr-full-feature-set)
  - [Complete SignalR Protocol Reference](#complete-signalr-protocol-reference)
  - [React component](#complete-chat-component--react)
  - [Angular component](#complete-chat-component--angular)
  - [Vue component](#complete-chat-component--vue)
  - [.NET MAUI / console](#net-maui--console)
- [Multimodal image input](#multimodal-image-input)
- [Hosted tools (web search, code interpreter, file search, images, remote MCP)](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp)
- [AI providers](#ai-providers)
- [The three-level agent model](#the-three-level-agent-model)
- [Built-in AI tools](#built-in-ai-tools)
- [Agent Skills](#agent-skills)
- [Page context and UI actions](#page-context-and-ui-actions)
- [HITL — Confirming actions](#hitl--confirming-actions)
- [Page navigation](#page-navigation)
- [Contextual memory](#contextual-memory)
- [RAG — Retrieval-Augmented Generation](#rag--retrieval-augmented-generation)
- [Streaming responses](#streaming-responses)
- [Token & cost optimization](#token--cost-optimization)
- [Middleware & extensibility](#middleware--extensibility)
- [Observability (OpenTelemetry)](#observability-opentelemetry)
- [Token & cost dashboard](#token--cost-dashboard)
- [Model routing](#model-routing)
- [Structured outputs](#structured-outputs)
- [Rich responses (tables & lists)](#rich-responses-tables--lists)
- [Generative UI — cards (Level 2)](#generative-ui--cards-level-2)
- [Declarative agents (YAML)](#declarative-agents-yaml)
- [Onboarding tour](#onboarding-tour)
- [Evaluation & regression testing](#evaluation--regression-testing)
- [MCP — Model Context Protocol](#mcp--model-context-protocol)
- [A2A — Agent-to-Agent](#a2a--agent-to-agent)
- [Security](#security)
- [Attribute reference](#attribute-reference)
- [Persistent conversation history](#persistent-conversation-history)
- [Session serialize and restore](#session-serialize-and-restore)
- [All configuration options](#all-configuration-options)
- [Requirements](#requirements)
- [Related Packages](#related-packages)
- [License](#license)

---

## Package Family

| Package | Install when |
|---------|-------------|
| [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) | Blazor Server app |
| **Novior.MentorAgent.Server** ← you are here | Web API / headless backend, or Blazor Auto server-side project — and any app, Blazor Server included, that publishes the MCP server or the A2A agent |
| [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) | Blazor WASM / Blazor Auto client project |

---

## What Novior.MentorAgent.Server exposes

| Endpoint | Protocol | Clients |
|----------|----------|---------|
| `/mentor-hub` | **SignalR** | Blazor WASM, MAUI, console .NET, any SignalR client |
| `/mentor/chat` | **SSE streaming** | React, Vue, Angular, fetch API, curl — no library needed. `GET` for text, `POST` for [text + images](#multimodal-image-input) |
| `/mentor/approve` | **HTTP POST** | All clients — HITL confirmation/rejection (see [HITL note](#hitl--confirming-actions)) |
| `/mentor/cancel` | **HTTP POST** | All SignalR clients — stops the in-flight turn (`?connectionId=...`). HTTP, not a hub method: SignalR cannot dispatch one while `SendMessage` is streaming. Returns **404** when that connection has no live scope — a normal race if Stop is pressed just as the turn ends, so do not treat it as an error |
| `/mentor/tour` | **HTTP GET** | All clients — [onboarding tour](#onboarding-tour) steps as JSON |
| `/mentor/session` | **HTTP GET / POST** | All SignalR clients: saves and restores the connection's conversation (`?connectionId=...`). `GET` returns the snapshot — opaque, protected with ASP.NET Core Data Protection and bound to the connection's user (`204` when there is nothing to save yet). `POST` with that snapshot as JSON body restores it (`400` if it is not one this server issued for this user, was edited, cannot be used by this host, or does not parse; `415` unless the request's content type is JSON). Owner-checked like approve/cancel, before the body is read. A `404` right after connecting means the connection's scope is not registered yet: restore after the first hub call, or retry once |
| `/mentor/admin/metrics` | **HTTP GET** | Operator dashboards — [token & cost snapshot](#token--cost-dashboard), role-gated. Never expose to end users |
| `/mcp` | **MCP server** | Claude Desktop, VS Code Copilot, Cursor, any MCP client |
| `/.well-known/agent-card.json` + `/a2a` | **A2A agent** | Other AI agents, orchestrators |

A reconnect is a new connection and therefore a new conversation. Save while the connection is alive and restore on the new one:

```javascript
// Owner-checked: send the credentials the hub connection uses. Cookies: credentials: 'include'.
// A bearer token: an Authorization header, see "Calling approve, cancel and session from an
// authenticated client".

// save (e.g. on every StreamingCompleted, or before navigating away)
const res = await fetch(`/mentor/session?connectionId=${connection.connectionId}`, { credentials: 'include' });
if (res.status === 200) localStorage.setItem('mentor-session', await res.text());   // 204 = nothing to save

// restore, after withAutomaticReconnect() or a page reload
async function restore() {
    const saved = localStorage.getItem('mentor-session');
    if (!saved) return;
    const post = () => fetch(`/mentor/session?connectionId=${connection.connectionId}`, {
        method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: saved,
    });
    let r = await post();
    if (r.status === 404) { await new Promise(ok => setTimeout(ok, 250)); r = await post(); }  // scope not registered yet: retry once
}
connection.onreconnected(restore);
```

---

## Getting started

### Installation

```bash
dotnet add package Novior.MentorAgent.Server
```

> `Novior.MentorAgent` is included automatically as a transitive dependency — you do not need to install it separately.
> The AI provider's SDK is not: for the Azure OpenAI example below add `Azure.AI.OpenAI` (`--prerelease`) and
> `Microsoft.Extensions.AI.OpenAI` (`AsIChatClient()`), and `Azure.Identity` for `DefaultAzureCredential`.
> Up to 1.0.0-rc.14 they came with `MentorAgent`.

### Minimal setup (Web API)

```csharp
// Program.cs — Web API or Minimal API
using Azure.AI.OpenAI;                   // AzureOpenAIClient
using Microsoft.Extensions.AI;           // AsIChatClient()
using Novior.MentorAgent.Abstractions.Models;   // MentorLanguage
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Server.Extensions;

builder.Services.AddMentorAgent(options =>
{
    options.AppName        = "My App";
    options.AppDescription = "An order management application";
    options.Language       = MentorLanguage.English;
    options.ChatClient     = new AzureOpenAIClient(endpoint, credential)
                                 .GetChatClient("gpt-4o").AsIChatClient();
    options.ScanAssemblies = [typeof(Program).Assembly];
});
builder.Services.AddMentorAgentServer();   // ← SignalR hub

var app = builder.Build();
app.MapMentorAgentServer();   // exposes /mentor-hub + /mentor/chat
app.Run();
```

### Blazor Auto — server project

```csharp
// Server project Program.cs
builder.Services.AddMentorAgent(options => { ... });
builder.Services.AddMentorAgentServer();

app.MapMentorAgentServer();
app.MapMentorAgentMcp();   // optional: requires options.McpServerEnabled = true, otherwise it throws at startup (AddMentorAgentServer() registers the server)
app.MapMentorAgentA2A();   // optional: requires options.A2AServerEnabled = true, otherwise it throws at startup (AddMentorAgentServer() registers the agent)
```

`AddRazorComponents()` and `AddMentorAgentServer()` can come in either order: Blazor's circuits keep their user, and the hub, `/mentor/chat` and A2A get theirs from the request. `AddMentorAgentServer()` goes after `AddMentorAgent()`, though: it registers the MCP server and the A2A agent from the options `AddMentorAgent()` read, and called before it with `McpServerEnabled` or `A2AServerEnabled` on, the host stops at start-up with the reason.

---

## CORS — required for cross-origin clients

> ⚠️ **This is the #1 cause of SignalR connection failures.** If your client runs on a **different origin** than the server — a standalone Blazor WASM app on `:5001`, a React dev server on `:5173`, an Angular app on `:4200`, etc. — you **must** configure CORS on the server. Without it, the browser silently blocks the SignalR handshake.

SignalR with browser clients requires credentials, and the CORS spec forbids `AllowAnyOrigin()` together with `AllowCredentials()`. You must list every client origin explicitly:

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("MentorAgentClients", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5001",   // Blazor WASM (HTTP)
                "https://localhost:7001",  // Blazor WASM (HTTPS)
                "http://localhost:5173",   // React (Vite)
                "http://localhost:4200")   // Angular
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();           // ← required for SignalR
    });
});

var app = builder.Build();

app.UseCors("MentorAgentClients");   // 1. CORS first
app.UseAuthentication();             // 2. then who is calling
app.UseAuthorization();              // 3. then what they may do
app.MapMentorAgentServer();          // 4. the endpoints last
```

**Keep this order: `UseCors` → `UseAuthentication` → `UseAuthorization` → `Map…`.** It matters as soon
as the hub requires a signed-in user (`configureTransports: e => e.RequireAuthorization()`, see
[Protecting the endpoints](#protecting-the-endpoints)). A `Program.cs` that does not call
`UseAuthentication()` and `UseAuthorization()` itself still gets them, because ASP.NET Core adds them
at the start of the pipeline, ahead of your `UseCors`. The browser's preflight for
`/mentor-hub/negotiate` carries no token, so authorization answers it `401` before CORS can add its
headers, and a client on another origin never connects, even with a valid token. With an explicit
`UseRouting()`, `UseCors` goes after it.

**You do NOT need CORS when:**
- The client is served from the **same origin** as the server (e.g. Blazor Auto hosted, or the WASM app served by the same ASP.NET Core host). In that case relative URLs like `HubUrl = "/mentor-hub"` work with no CORS at all.

**When CORS is required, the client must use the server's absolute URL:**

```csharp
// Client (separate origin) — full URL, not a relative path
options.HubUrl = "http://localhost:5169/mentor-hub";
```

---

## Connecting clients

### Option A — SSE (simplest, no library required)

No library required. Six frame types: `chunk` and `completed` carry the answer, `error` reports a
failure, and `cards`, `navigation` and `confirmationRequired` carry the outcomes that are not text.

Every frame beyond the first three is **additive** — a client that does not recognise a `type`
ignores it and still receives the whole answer — so the three-frame reader below remains correct
code. Read the extra frames when you want cards, routing, or approvals; skip them and nothing
breaks except those features.

> **Each `/mentor/chat` request is its own conversation.** The endpoint runs the turn in the request's own DI scope, so every `GET`/`POST` starts a fresh session. The model does not see the previous turns, a follow-up such as *"and the second one?"* has nothing to refer to, and per-session counters (`MaxHostedToolCallsPerSession`) reset on every call. SSE also has no way to send a `PageContextSnapshot`, so page context and client-side UI actions are not available. For a multi-turn chat, use [SignalR](#option-b--signalr-full-feature-set), whose scope and conversation span the connection.

| Frame | Payload | What it is for |
|---|---|---|
| `chunk` | `text` | One streamed fragment of the answer |
| `completed` | — | The **turn** is over. Not merely the current stream: a turn waiting on an approval has not completed |
| `error` | `message` | The turn failed. Followed by `completed` |
| `cards` | `cards[]` | [Generative cards](#generative-ui--cards-level-2) — camelCase, string enums |
| `navigation` | `url` | The assistant called `navigate_to`. Route however your framework routes |
| `confirmationRequired` | `actionId`, `tool`, `message` | A gated action is withheld. Answer with `POST /mentor/approve?actionId=…&approved=true|false` — the stream stays open and resumes on your verdict |

```javascript
const response = await fetch('/mentor/chat?message=' + encodeURIComponent(text));
const reader   = response.body.getReader();
const decoder  = new TextDecoder();
let buffer     = '';

while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });   // a read can end mid-frame or mid-character
    const lines = buffer.split('\n');
    buffer = lines.pop();                                  // keep the incomplete tail for the next read
    for (const line of lines) {
        if (!line.startsWith('data:')) continue;
        const event = JSON.parse(line.slice(5));
        if (event.type === 'chunk')     appendText(event.text);
        if (event.type === 'completed') finalize();
        if (event.type === 'error')     showError(event.message);
    }
}
```

**With images** — use `POST` with a JSON body (a base64 image doesn't fit in a query string). Everything else is identical:

```javascript
const response = await fetch('/mentor/chat', {
    method:  'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    body: JSON.stringify({
        message: text,
        attachments: [
            { mimeType: 'image/png', dataBase64: '<base64, no data: prefix>', fileName: 'screenshot.png' },
            { mimeType: 'image/jpeg', url: 'https://cdn.example.com/product.jpg' },
        ],
    }),
});
// …read the stream exactly as above
```

Requires `options.EnableImageInput = true` and a vision-capable model — see [Multimodal image input](#multimodal-image-input).

---

### Option B — SignalR (full feature set)

All 18 events. SSE covers the six that decide what the user sees; SignalR adds the rest — UI actions,
RAG citations, team collaboration, per-action feedback, MCP and hosted-tool status, busy state.
Choose it when your client drives the page, not only the conversation.

```bash
npm install @microsoft/signalr
```

```javascript
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
    .withUrl('/mentor-hub')
    .withAutomaticReconnect()
    .build();

// ── Subscribe to ALL events (see complete reference below) ───────────────────
connection.on('StreamingChunk',       chunk        => appendText(chunk));
connection.on('StreamingCompleted',   ()           => finalize());
connection.on('BusyChanged',          busy         => setSpinner(busy));
connection.on('Error',                msg          => showError(msg));
connection.on('ActionExecuting',      action       => showActionBar(action));
connection.on('ActionCompleted',      action       => hideActionBar(action));
connection.on('ActionFailed',         error        => showActionError(error));
connection.on('ConfirmationRequired', (id, tool, msg) => showConfirmDialog(id, tool, msg));
connection.on('NavigationRequested',  url          => router.push(url));
connection.on('RagSourcesReady',      sources      => showCitations(sources));
connection.on('TeamMemberSpeaking',   (team, role) => showTeamActivity(team, role));
connection.on('UIActionRequested',    (name, json) => executeUIAction(name, json));
connection.on('UIActionExecuting',    name         => onUIActionStart(name));
connection.on('UIActionCompleted',    name         => onUIActionEnd(name));
connection.on('McpServerStatusChanged', (name, ok) => setMcpBadge(name, ok));
connection.on('HostedToolsDeclared',  flags        => showHostedToolBadges(flags)); // once, on connect
connection.on('GeneratedImages',      images       => attachImages(images));        // data: URIs or absolute URLs
connection.on('Cards',                cards        => renderCards(cards));          // numeric enums over SignalR, see below

await connection.start();

// ── Send messages ─────────────────────────────────────────────────────────────
// Always two arguments — see the note under the protocol reference.
await connection.invoke('SendMessage', 'Mostrami gli ordini pending', null);

// ── Send a message with images (multimodal) ──────────────────────────────────
// The second argument carries the images. It is never optional on the wire: a text-only turn passes null (see the note under the protocol reference).
await connection.invoke('SendMessage', 'Cosa non va in questo screenshot?', [
    { mimeType: 'image/png', dataBase64: '<base64, no data: prefix>', fileName: 'error.png' },
    { mimeType: 'image/jpeg', url: 'https://cdn.example.com/product.jpg' },
]);

// ── Cancel current request — use HTTP, NOT a hub method ──────────────────────
// Same reason as HITL below: SignalR dispatches at most one hub invocation at a time per
// connection, so a CancelRequest hub call queues behind the still-running SendMessage and
// only fires after the turn it was meant to abort has already finished.
// Owner-checked: send the credentials the hub connection uses. Cookies: credentials: 'include'.
// A bearer token: an Authorization header, see "Calling approve, cancel and session from an
// authenticated client".
await fetch(`/mentor/cancel?connectionId=${connection.connectionId}`, { method: 'POST', credentials: 'include' });

// ── Reset conversation ────────────────────────────────────────────────────────
await connection.invoke('ResetSession');

// ── Confirm / reject a HITL dialog — use HTTP, NOT a hub method ──────────────
// SignalR processes hub messages sequentially per connection. While SendMessage
// is awaiting confirmation, the dispatcher cannot process any other hub message
// from the same connection — invoking RespondToApproval via hub would deadlock.
// Owner-checked like Stop: the same credentials.
await fetch(`/mentor/approve?actionId=${actionId}&approved=true`,  { method: 'POST', credentials: 'include' });  // approve
await fetch(`/mentor/approve?actionId=${actionId}&approved=false`, { method: 'POST', credentials: 'include' });  // reject
```

---

### Complete SignalR Protocol Reference

#### Server → Client events

| Event | Parameters | When fired | What to do |
|-------|-----------|------------|------------|
| `StreamingChunk` | `chunk: string` | Each streaming token from the AI | Append text to the chat bubble |
| `StreamingCompleted` | — | A streaming **segment** ended: the reply finished, or the turn was stopped or failed. It also fires mid-turn, right before `ConfirmationRequired`, so the explanation is committed before the dialog appears | Finalize the current bubble. Do **not** use it to re-enable input: a turn parked on a confirmation has not ended, and a turn refused up front (message too long, rate limit, safety or out-of-scope refusal) sends `Error` + `BusyChanged(false)` with no `StreamingCompleted`. Gate the composer on `BusyChanged` |
| `BusyChanged` | `busy: boolean` | AI starts/stops processing | Show/hide loading spinner |
| `Error` | `message: string` | Critical error (rate limit, safety block, a provider failure — including one the provider reports inside the stream, such as a 429 on the Responses API, since rc.14) | Show error message to user |
| `ActionExecuting` | `action: string` | A tool/agent is executing. `action` is the text to show — the action's description, or a routing, team or hosted-tool label — not the tool name, which `ActionCompleted` carries | Show action feedback bar (e.g. "Consulting specialist…") |
| `ActionCompleted` | `action: string` | Tool execution succeeded | Hide feedback bar |
| `ActionFailed` | `error: string` | Tool execution failed | Show error in feedback bar |
| `ConfirmationRequired` | `actionId: string`, `toolName: string`, `message: string` | Destructive action needs approval | Show confirmation dialog, then `POST /mentor/approve?actionId=...&approved=true\|false` (HTTP — **not** a hub method, see [HITL note](#hitl--confirming-actions)) |
| `NavigationRequested` | `url: string` | AI navigated after an action, or user asked to navigate | Call `router.push(url)` or equivalent |
| `RagSourcesReady` | `sources: RagSource[]` | The RAG documents the finished answer **cited** (sent once the answer is complete; no event when it cites none) — **and** the pages cited by the provider's hosted web search, in the same shape | Show citation chips below the AI message |
| `GeneratedImages` | `images: string[]` | The provider's hosted image-generation tool produced images | Attach to the message being committed; each entry is a `data:` URI or an absolute URL, ready for `<img src>`. Ignoring the event is safe |
| `Cards` | `cards: MentorCard[]` | A server-side tool returned generative-UI cards instead of prose | Render them under the reply — fields, accent and buttons. See [Generative UI — cards](#generative-ui--cards-level-2) for the shape and for what a button does. One difference on this transport: the hub uses SignalR's default JSON protocol, so enums arrive as **numbers**: `accent` 0-4 = default, success, warning, danger, info, and action `kind` 0-2 = sendMessage, navigate, uiAction. Only the SSE `cards` frame uses string enums. Ignoring the event loses the cards: the model is told they are already on screen, so it will not repeat their content in text |
| `TeamMemberSpeaking` | `teamName: string`, `memberRole: string` | A GroupChat team member is speaking | Show "Team analyzing · Data Analyst" in feedback bar |
| `UIActionRequested` | `actionName: string`, `parameterJson: string?` | AI invoked a client-side UI action | Execute the registered handler for `actionName` |
| `UIActionExecuting` | `actionName: string` | UI action started | Optional: show feedback |
| `UIActionCompleted` | `actionName: string` | UI action completed | Optional: hide feedback |
| `McpServerStatusChanged` | `name: string`, `connected: boolean` | An MCP client server connected (`true`) or failed/disconnected (`false`) | Update the MCP status badge (green/red) for that server |
| `HostedToolsDeclared` | `flags: number` | **Once on connect**, before any message | Render your capability badge from this rather than mirroring the server's configuration in the client — see [Hosted tools](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp). The value is the `MentorHostedTools` bit flags |
| *(client)* `onreconnecting` / `onclose` | `error?: Error` | The SignalR client's own callbacks, not hub events: the connection dropped or closed | Clear busy, the streaming text and an open confirmation. The turn in flight is lost with the connection, and the reconnected connection (a new scope) never sends `BusyChanged(false)` for it |
| *(client)* `onreconnected` | `connectionId?: string` | The SignalR client reconnected — on a **new** connection, with a new conversation | Restore the saved session with `POST /mentor/session` (see [What Novior.MentorAgent.Server exposes](#what-noviormentoragentserver-exposes)) |

#### Client → Server methods

| Method | Parameters | Description |
|--------|-----------|-------------|
| `UpdatePageContext` | `snapshot: PageContextSnapshot` | Send current page state before each message. Call before `SendMessage` |
| `SendMessage` | `text: string`, `attachments: MentorAttachment[] \| null` | Send a user message to the AI. `attachments` carries images for a multimodal turn. For a text-only turn pass `null` (or `[]`). The argument is **required on the wire**, see the note below. Each item is `{ mimeType, dataBase64? , url?, fileName? }` — exactly one of `dataBase64` / `url`. An image-only turn (empty `text`) is valid |
| ~~`CancelRequest`~~ | — | ⚠️ **Only works while idle.** SignalR sequential dispatch means it cannot run while `SendMessage` is streaming — exactly when Stop is needed. Use `POST /mentor/cancel?connectionId=...` instead |
| `ResetSession` | — | Start a new conversation (a fresh `AgentSession`) |
| ~~`RespondToApproval`~~ | ~~`requestId: string`, `approved: boolean`~~ | ⚠️ **Do not use for HITL.** SignalR sequential dispatch causes a deadlock while `SendMessage` is awaiting. Use `POST /mentor/approve` instead (see below) |

> ### ⚠️ `SendMessage` takes **two** arguments — always
>
> ```js
> connection.invoke('SendMessage', text, null);          // ✅ text-only
> connection.invoke('SendMessage', text, attachments);   // ✅ with images
> connection.invoke('SendMessage', text);                // ❌ rejected by the server
> ```
>
> The hub method's second parameter is declared with a C# default, but **a default value never
> reaches the wire**. SignalR binds invocation arguments positionally by *count*, inside the hub
> protocol's parser — before dispatch, before any hub filter, and before the method body — so a
> one-argument invocation fails with:
>
> ```
> InvalidDataException: Invocation provides 1 argument(s) but target expects 2.
> ```
>
> Worse, `EnableDetailedErrors` is **off by default**, so what your client actually sees is the
> opaque *"Failed to invoke 'SendMessage' due to an error on the server"* — which looks like a
> transport or authentication problem rather than an argument-count one. If you are staring at that
> message, this is almost certainly why.
>
> The same rule applies to every SignalR client language, not just JavaScript: hub methods cannot be
> overloaded and have no optional parameters. Pass `null` (or `[]` — they behave identically) for a
> text-only turn.

#### Rendering the `Cards` event

`Cards` is the one event whose payload you lose by ignoring it. The model is told the cards are
already on screen, so it deliberately does **not** repeat their content in prose — a client that
drops the frame shows the user a reply referring to something that was never rendered.

```typescript
conn.on('Cards', (cards: MentorCard[]) => setCards(cards));
```

```tsx
{cards.map((c, i) => (
    // over SignalR enums arrive as numbers: accent 0-4 = default, success, warning, danger, info
    <article key={i} className={`card card--${['default', 'success', 'warning', 'danger', 'info'][c.accent] ?? 'default'}`}>
        {c.imageUrl && <img src={c.imageUrl} alt="" />}
        <h4>{c.title}</h4>
        {c.subtitle && <p className="sub">{c.subtitle}</p>}

        {(c.fields ?? []).map((f, j) => (
            <div key={j} className="row"><span>{f.label}</span><b>{f.value}</b></div>
        ))}

        {(c.actions ?? []).map((a, j) => (
            <button key={j} onClick={() =>
                // kind 0 = SendMessage, 1 = Navigate, 2 = UIAction (numbers over SignalR)
                a.kind === 1 ? router.push(a.value)
              : a.kind === 2 ? runLocalAction(a.value)   // your own client-side UI action registry
              :                sendMessage(a.value)
            }>{a.label}</button>
        ))}
    </article>
))}
```

The buttons are written by the tool that produced the card, never by the model's prose — which is
what makes it safe for a card to carry actions at all.

#### `PageContextSnapshot` object

Sent before each message so the AI knows the current page state and available UI actions:

```typescript
interface PageContextSnapshot {
    pageName?: string;                // e.g. "Orders"
    contextData: Record<string, string | null>; // e.g. { activeFilter: "Pending", visibleRows: "15" }
    uiActions: UIActionInfo[];
}

interface UIActionInfo {
    name: string;           // snake_case, e.g. "highlight_row"
    description: string;    // shown to AI
    parameterHint?: string; // e.g. "integer: order ID" — kept by the hub and shown to the model as "(parameter: integer: order ID)"
}
```

The hint is the only thing that tells the model what shape the argument takes, so send one for every action that takes a parameter. The WebAssembly client (`Novior.MentorAgent.Blazor`) generates it from the handler's parameter type when you do not supply one, in the same form as Blazor Server (`integer`, `string (A|B)`, `array<string>`, `{ id: integer, ... }`).

A UI action requested through the hub is **fire-and-forget**: the server sends `UIActionRequested` and tells the model the action succeeded once the request is sent — the client's handler result is not waited for.

#### `RagSource` object

> JSON property names use **camelCase** (System.Text.Json default serialization from C# `MentorRagResult`).

```typescript
interface RagSource {
    content: string;    // document text injected into AI prompt
    sourceUrl?: string; // link for citation chip
    title?: string;     // display title for chip (falls back to sourceUrl)
    score: number;      // relevance score (higher = more relevant)
    metadata?: Record<string, string> | null; // the source's facts about the document, as the model saw them
}
```

---

### Complete chat component — React

Two files: the hook that manages the SignalR connection, and the component that renders the UI.

All three components (React, Angular, Vue) do the same four things beyond the events:

- **Same identity on every call.** Approve, cancel and session are HTTP calls, and they are
  owner-checked: they must carry the identity the hub connection carries, or they get `401` (see
  [Calling approve, cancel and session from an authenticated client](#calling-approve-cancel-and-session-from-an-authenticated-client)).
  Pass a `getToken` function when the hub requires a bearer token. It feeds the hub's
  `accessTokenFactory` and the `Authorization` header of every HTTP call. For cookie auth, omit it:
  the calls send `credentials: 'include'`.
- **HTTP answers are checked.** A confirmation or a Stop that the server refuses shows an error in
  the chat instead of failing silently. A `404` is not an error: nothing was waiting for it any more
  (the turn had ended, or the answer had already arrived).
- **A dropped connection frees the UI.** The reconnected connection is a new scope, and it never
  sends `BusyChanged(false)` for the turn that was running. `onreconnecting` and `onclose` clear
  busy, the streaming text and an open confirmation.
- **The conversation survives a reconnect.** The session is saved after every `StreamingCompleted`
  and restored in `onreconnected` (see [What Novior.MentorAgent.Server exposes](#what-noviormentoragentserver-exposes)).

```typescript
// useMentorHub.ts
import { useEffect, useRef, useState } from 'react';
import * as signalR from '@microsoft/signalr';

// hubUrl: relative when the server is this page's origin, absolute for a server on another
// origin (see CORS). getToken: only when the hub requires a bearer token; pass a stable function
// (module-level or useCallback), since the connection is built once per hubUrl.
export function useMentorHub(hubUrl: string, getToken?: () => string | Promise<string>) {
    const connRef      = useRef<signalR.HubConnection | null>(null);
    // Origin for the HTTP side-channel (approve/cancel/session): the page's own origin for a
    // relative hub URL, the server's origin for an absolute one (cross-origin setups, see CORS).
    const baseUrl      = new URL(hubUrl, window.location.href).origin;
    const streamingRef = useRef('');  // ref to avoid stale closure in StreamingCompleted
    const [messages, setMessages]     = useState<{ role: string; text: string }[]>([]);
    const [streaming, setStreaming]   = useState('');
    const [busy, setBusy]             = useState(false);
    const [action, setAction]         = useState('');
    const [sources, setSources]       = useState<any[]>([]);
    const [confirm, setConfirm]       = useState<{ id: string; msg: string } | null>(null);
    const [mcpStatus, setMcpStatus]   = useState<Record<string, boolean>>({}); // server name → connected

    const showError = (text: string) => setMessages(m => [...m, { role: 'error', text }]);

    // The HTTP side-channel, with the hub's identity: the bearer token, or the cookies.
    // Resolves to null when the request does not get through at all (network, CORS).
    const http = async (path: string, init: RequestInit = {}) => {
        const token = await getToken?.();
        const headers = new Headers(init.headers);
        if (token) headers.set('Authorization', `Bearer ${token}`);
        return fetch(`${baseUrl}${path}`, { ...init, headers, credentials: 'include' }).catch(() => null);
    };

    useEffect(() => {
        const conn = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl, getToken ? { accessTokenFactory: getToken } : {})
            .withAutomaticReconnect()
            .build();

        // A reconnect is a new connection, so a new scope: the turn that was running is lost and
        // nothing will send BusyChanged(false) for it. Free the UI ourselves.
        const clear = () => {
            setBusy(false); setAction(''); setConfirm(null);
            streamingRef.current = ''; setStreaming('');
        };
        conn.onreconnecting(clear);
        conn.onclose(clear);
        // Restore the conversation saved after the last StreamingCompleted (see below).
        conn.onreconnected(async () => {
            const saved = localStorage.getItem('mentor-session');
            if (!saved) return;
            const post = () => http(`/mentor/session?connectionId=${conn.connectionId}`, {
                method: 'POST', headers: { 'Content-Type': 'application/json' }, body: saved,
            });
            let r = await post();
            if (r?.status === 404) { await new Promise(ok => setTimeout(ok, 250)); r = await post(); }  // scope not registered yet
        });

        conn.on('StreamingChunk',       c => {
            streamingRef.current += c;
            setStreaming(p => p + c);
        });
        conn.on('StreamingCompleted',   async () => {
            // Read the text before clearing the ref: React runs the setMessages updater later,
            // when the ref would already be empty.
            const text = streamingRef.current;
            streamingRef.current = '';
            setStreaming('');
            if (text) setMessages(m => [...m, { role: 'assistant', text }]);   // no text: a segment closed before a confirmation
            const r = await http(`/mentor/session?connectionId=${conn.connectionId}`);   // save
            if (r?.status === 200) localStorage.setItem('mentor-session', await r.text());   // 204 = nothing yet
        });
        conn.on('BusyChanged',          b => setBusy(b));
        conn.on('Error',                e => setMessages(m => [...m, { role: 'error', text: e }]));
        conn.on('ActionExecuting',      (a: string) => setAction(a));
        conn.on('ActionCompleted',      (_: string) => setAction(''));
        conn.on('ActionFailed',         e => setAction(`Error: ${e}`));
        conn.on('ConfirmationRequired', (id, tool, msg) => setConfirm({ id, msg }));
        conn.on('NavigationRequested',  url => router.push(url)); // use your SPA router
        conn.on('RagSourcesReady',      s => setSources(s));
        conn.on('McpServerStatusChanged', (name, connected) =>   // drives the 🔌 MCP badge
            setMcpStatus(p => ({ ...p, [name]: connected })));
        conn.on('TeamMemberSpeaking',   (t, r) => setAction(`${t} · ${r}`));
        conn.on('UIActionExecuting',    name => setAction(`UI: ${name}`));
        conn.on('UIActionCompleted',    (_name: string) => setAction(''));
        conn.on('UIActionRequested',    (name, json) => {
            // dispatch to your own UI action handlers
            window.dispatchEvent(new CustomEvent('mentor-ui-action', { detail: { name, json } }));
        });

        conn.start();
        connRef.current = conn;
        return () => { conn.stop(); };
    }, [hubUrl]);

    const sendMessage = async (text: string, snapshot?: any) => {
        if (snapshot) await connRef.current?.invoke('UpdatePageContext', snapshot);
        setMessages(m => [...m, { role: 'user', text }]);
        // Two arguments always: SignalR matches hub methods by argument count.
        await connRef.current?.invoke('SendMessage', text, null);
    };

    const respond = async (id: string, approved: boolean) => {
        setConfirm(null);
        // HITL MUST use HTTP POST, not the hub: SignalR dispatches hub messages
        // sequentially per connection, so invoking a hub method while SendMessage
        // is still awaiting would deadlock. (See the protocol table above.)
        const r = await http(`/mentor/approve?actionId=${id}&approved=${approved}`, { method: 'POST' });
        if (!r?.ok && r?.status !== 404) showError(`Your answer did not reach the server (${r ? `HTTP ${r.status}` : 'no response'}).`);  // 404: nothing waits for it any more
    };

    // Cancel over HTTP, not the hub — a hub call cannot run while SendMessage is streaming.
    const cancel  = async () => {
        const r = await http(`/mentor/cancel?connectionId=${connRef.current?.connectionId}`, { method: 'POST' });
        if (!r?.ok && r?.status !== 404) showError(`Stop did not reach the server (${r ? `HTTP ${r.status}` : 'no response'}).`);  // 404: the turn had already ended
    };
    const reset   = () => { setMessages([]); connRef.current?.invoke('ResetSession'); };

    return { messages, streaming, busy, action, sources, confirm, mcpStatus, sendMessage, respond, cancel, reset };
}
```

```tsx
// MentorChat.tsx
import { useState } from 'react';
import { useMentorHub } from './useMentorHub';

export function MentorChat() {
    const [input, setInput] = useState('');
    // Same origin, open hub: useMentorHub('/mentor-hub').
    // Server on another origin, bearer token: useMentorHub('https://api.example.com/mentor-hub', () => auth.getToken()).
    const { messages, streaming, busy, action, sources, confirm,
            sendMessage, respond, cancel, reset } = useMentorHub('/mentor-hub');

    const handleSend = () => {
        if (!input.trim() || busy) return;
        // Pass a PageContextSnapshot as second argument if you have page state:
        // sendMessage(input, { pageName: 'Orders', contextData: {}, uiActions: [] });
        sendMessage(input);
        setInput('');
    };

    return (
        <div className="mentor-chat">

            {/* Messages */}
            <div className="messages">
                {messages.map((m, i) => (
                    <div key={i} className={`bubble bubble--${m.role}`}>
                        {m.text}
                    </div>
                ))}
                {streaming && (
                    <div className="bubble bubble--assistant">
                        {streaming}<span className="cursor">▋</span>
                    </div>
                )}
                {busy && !streaming && <div className="typing">···</div>}
            </div>

            {/* Action feedback bar */}
            {action && (
                <div className="action-bar">
                    <span className="pulse" /> {action}
                </div>
            )}

            {/* RAG citations */}
            {sources.length > 0 && (
                <div className="citations">
                    {sources.map((s, i) => (
                        <a key={i} href={s.sourceUrl} target="_blank" className="citation-chip">
                            📄 {s.title ?? s.sourceUrl}
                        </a>
                    ))}
                </div>
            )}

            {/* Confirmation dialog */}
            {confirm && (
                <div className="confirm-dialog">
                    <p>{confirm.msg}</p>
                    <button onClick={() => respond(confirm.id, true)}>Confirm</button>
                    <button onClick={() => respond(confirm.id, false)}>Cancel</button>
                </div>
            )}

            {/* Input */}
            <div className="input-bar">
                <input
                    value={input}
                    onChange={e => setInput(e.target.value)}
                    onKeyDown={e => e.key === 'Enter' && handleSend()}
                    placeholder="Ask anything..."
                    disabled={busy}
                />
                {busy
                    ? <button onClick={cancel}>■ Stop</button>
                    : <button onClick={handleSend} disabled={!input.trim()}>Send</button>
                }
                <button onClick={reset} title="New conversation">↺</button>
            </div>
        </div>
    );
}
```

---

### Complete chat component — Angular

Two files: the injectable service and the component. Same identity, checks and reconnect handling
as the React component (see the list above it).

```bash
npm install @microsoft/signalr
```

```typescript
// mentor-hub.service.ts
import { Injectable, InjectionToken, OnDestroy, inject, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Router } from '@angular/router';

// hubUrl: relative when the server is this page's origin, absolute for a server on another origin
// (see CORS). getToken: only when the hub requires a bearer token. Without a provider the service
// uses '/mentor-hub' and no token. In app.config.ts, for example:
//   { provide: MENTOR_HUB_CONFIG, useFactory: () => {
//       const auth = inject(AuthService);   // your own token source
//       return { hubUrl: 'https://api.example.com/mentor-hub', getToken: () => auth.getToken() };
//   } }
export interface MentorHubConfig { hubUrl: string; getToken?: () => string | Promise<string>; }
export const MENTOR_HUB_CONFIG = new InjectionToken<MentorHubConfig>('MENTOR_HUB_CONFIG',
    { providedIn: 'root', factory: () => ({ hubUrl: '/mentor-hub' }) });

@Injectable({ providedIn: 'root' })
export class MentorHubService implements OnDestroy {

    // Reactive state — use in templates with {{ messages() }}
    messages     = signal<{ role: string; text: string }[]>([]);
    streaming    = signal('');
    busy         = signal(false);
    currentAction = signal('');
    ragSources   = signal<any[]>([]);
    confirmation = signal<{ id: string; tool: string; message: string } | null>(null);
    mcpStatus    = signal<Record<string, boolean>>({});   // server name → connected

    private config = inject(MENTOR_HUB_CONFIG);
    // Origin for the HTTP side-channel (approve/cancel/session): the page's own origin for a
    // relative hub URL, the server's origin for an absolute one.
    private baseUrl = new URL(this.config.hubUrl, window.location.href).origin;
    private connection: signalR.HubConnection;

    constructor(private router: Router) {
        const getToken = this.config.getToken;
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(this.config.hubUrl, getToken ? { accessTokenFactory: getToken } : {})
            .withAutomaticReconnect()
            .build();

        this.registerHandlers();
        this.connection.start();
    }

    // The HTTP side-channel, with the hub's identity: the bearer token, or the cookies.
    // Resolves to null when the request does not get through at all (network, CORS).
    private async http(path: string, init: RequestInit = {}): Promise<Response | null> {
        const token = await this.config.getToken?.();
        const headers = new Headers(init.headers);
        if (token) headers.set('Authorization', `Bearer ${token}`);
        return fetch(`${this.baseUrl}${path}`, { ...init, headers, credentials: 'include' }).catch(() => null);
    }

    private showError(text: string): void {
        this.messages.update(m => [...m, { role: 'error', text }]);
    }

    private registerHandlers(): void {
        // A reconnect is a new connection, so a new scope: the turn that was running is lost and
        // nothing will send BusyChanged(false) for it. Free the UI ourselves.
        const clear = () => {
            this.busy.set(false); this.currentAction.set(''); this.confirmation.set(null); this.streaming.set('');
        };
        this.connection.onreconnecting(clear);
        this.connection.onclose(clear);
        // Restore the conversation saved after the last StreamingCompleted (see below).
        this.connection.onreconnected(async () => {
            const saved = localStorage.getItem('mentor-session');
            if (!saved) return;
            const post = () => this.http(`/mentor/session?connectionId=${this.connection.connectionId}`, {
                method: 'POST', headers: { 'Content-Type': 'application/json' }, body: saved,
            });
            let r = await post();
            if (r?.status === 404) { await new Promise(ok => setTimeout(ok, 250)); r = await post(); }  // scope not registered yet
        });

        this.connection.on('StreamingChunk',       (c: string)  => this.streaming.update(p => p + c));
        this.connection.on('StreamingCompleted',   async ()     => {
            const text = this.streaming();
            this.streaming.set('');
            if (text) this.messages.update(m => [...m, { role: 'assistant', text }]);   // no text: a segment closed before a confirmation
            const r = await this.http(`/mentor/session?connectionId=${this.connection.connectionId}`);   // save
            if (r?.status === 200) localStorage.setItem('mentor-session', await r.text());   // 204 = nothing yet
        });
        this.connection.on('BusyChanged',          (b: boolean) => this.busy.set(b));
        this.connection.on('Error',                (msg: string)=> this.messages.update(m => [...m, { role: 'error', text: msg }]));
        this.connection.on('ActionExecuting',      (a: string)  => this.currentAction.set(a));
        this.connection.on('ActionCompleted',      (_: string)  => this.currentAction.set(''));
        this.connection.on('ActionFailed',         (e: string)  => this.currentAction.set(`Error: ${e}`));
        this.connection.on('ConfirmationRequired', (id: string, tool: string, msg: string) =>
            this.confirmation.set({ id, tool, message: msg }));
        this.connection.on('NavigationRequested',  (url: string)=> this.router.navigateByUrl(url));
        this.connection.on('RagSourcesReady',      (s: any[])   => this.ragSources.set(s));
        this.connection.on('McpServerStatusChanged', (name: string, connected: boolean) =>  // 🔌 MCP badge
            this.mcpStatus.update(m => ({ ...m, [name]: connected })));
        this.connection.on('TeamMemberSpeaking',   (t: string, r: string) => this.currentAction.set(`${t} · ${r}`));
        this.connection.on('UIActionExecuting',    (name: string) => this.currentAction.set(`UI: ${name}`));
        this.connection.on('UIActionCompleted',    (_: string)  => this.currentAction.set(''));
        this.connection.on('UIActionRequested',    (name: string, json: string | null) => {
            // Dispatch to your own UI action handlers
            document.dispatchEvent(new CustomEvent('mentor-ui-action', { detail: { name, json } }));
        });
    }

    async sendMessage(text: string, snapshot?: any): Promise<void> {
        if (snapshot) await this.connection.invoke('UpdatePageContext', snapshot);
        this.messages.update(m => [...m, { role: 'user', text }]);
        // Two arguments always: SignalR matches hub methods by argument count.
        await this.connection.invoke('SendMessage', text, null);
    }

    async respond(id: string, approved: boolean): Promise<void> {
        this.confirmation.set(null);
        // HITL MUST use HTTP POST, not the hub — SignalR dispatches hub messages
        // sequentially per connection, so a hub call while SendMessage awaits deadlocks.
        const r = await this.http(`/mentor/approve?actionId=${id}&approved=${approved}`, { method: 'POST' });
        if (!r?.ok && r?.status !== 404) this.showError(`Your answer did not reach the server (${r ? `HTTP ${r.status}` : 'no response'}).`);  // 404: nothing waits for it any more
    }

    // Cancel over HTTP, not the hub — a hub call cannot run while SendMessage is streaming.
    cancel  = async () => {
        const r = await this.http(`/mentor/cancel?connectionId=${this.connection.connectionId}`, { method: 'POST' });
        if (!r?.ok && r?.status !== 404) this.showError(`Stop did not reach the server (${r ? `HTTP ${r.status}` : 'no response'}).`);  // 404: the turn had already ended
    };
    reset   = () => { this.messages.set([]); this.connection.invoke('ResetSession'); };

    ngOnDestroy(): void { this.connection.stop(); }
}
```

```typescript
// mentor-chat.component.ts
import { Component, signal } from '@angular/core';
import { MentorHubService } from './mentor-hub.service';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

@Component({
    selector: 'app-mentor-chat',
    standalone: true,
    imports: [CommonModule, FormsModule],
    template: `
        <div class="mentor-chat">

            <!-- Messages -->
            <div class="messages">
                @for (m of hub.messages(); track $index) {
                    <div class="bubble" [class]="'bubble--' + m.role">{{ m.text }}</div>
                }
                @if (hub.streaming()) {
                    <div class="bubble bubble--assistant">
                        {{ hub.streaming() }}<span class="cursor">▋</span>
                    </div>
                }
                @if (hub.busy() && !hub.streaming()) {
                    <div class="typing">···</div>
                }
            </div>

            <!-- Action feedback bar -->
            @if (hub.currentAction()) {
                <div class="action-bar">
                    <span class="pulse"></span> {{ hub.currentAction() }}
                </div>
            }

            <!-- RAG citations -->
            @if (hub.ragSources().length > 0) {
                <div class="citations">
                    @for (s of hub.ragSources(); track $index) {
                        <a [href]="s.sourceUrl" target="_blank" class="citation-chip">
                            📄 {{ s.title ?? s.sourceUrl }}
                        </a>
                    }
                </div>
            }

            <!-- Confirmation dialog -->
            @if (hub.confirmation(); as c) {
                <div class="confirm-dialog">
                    <p>{{ c.message }}</p>
                    <button (click)="hub.respond(c.id, true)">Confirm</button>
                    <button (click)="hub.respond(c.id, false)">Cancel</button>
                </div>
            }

            <!-- Input -->
            <div class="input-bar">
                <input [(ngModel)]="input" (keydown.enter)="send()"
                       placeholder="Ask anything..." [disabled]="hub.busy()" />
                @if (hub.busy()) {
                    <button (click)="hub.cancel()">■ Stop</button>
                } @else {
                    <button (click)="send()" [disabled]="!input.trim()">Send</button>
                }
                <button (click)="hub.reset()" title="New conversation">↺</button>
            </div>
        </div>
    `
})
export class MentorChatComponent {
    input = '';
    constructor(public hub: MentorHubService) {}
    send() {
        if (!this.input.trim() || this.hub.busy()) return;
        this.hub.sendMessage(this.input);
        this.input = '';
    }
}
```

---

### Complete chat component — Vue

Same identity, checks and reconnect handling as the React component (see the list above it).

```bash
npm install @microsoft/signalr
```

```typescript
// useMentorHub.ts
import { ref, onUnmounted } from 'vue';
import * as signalR from '@microsoft/signalr';
import { useRouter } from 'vue-router';

// hubUrl: relative when the server is this page's origin, absolute for a server on another
// origin (see CORS). getToken: only when the hub requires a bearer token.
export function useMentorHub(hubUrl: string, getToken?: () => string | Promise<string>) {
    const router = useRouter();
    // Origin for the HTTP side-channel (approve/cancel/session): the page's own origin for a
    // relative hub URL, the server's origin for an absolute one.
    const baseUrl = new URL(hubUrl, window.location.href).origin;

    const messages      = ref<{ role: string; text: string }[]>([]);
    const streaming     = ref('');
    const busy          = ref(false);
    const currentAction = ref('');
    const ragSources    = ref<any[]>([]);
    const confirmation  = ref<{ id: string; tool: string; message: string } | null>(null);
    const mcpStatus     = ref<Record<string, boolean>>({});   // server name → connected

    const showError = (text: string) => messages.value.push({ role: 'error', text });

    // The HTTP side-channel, with the hub's identity: the bearer token, or the cookies.
    // Resolves to null when the request does not get through at all (network, CORS).
    const http = async (path: string, init: RequestInit = {}) => {
        const token = await getToken?.();
        const headers = new Headers(init.headers);
        if (token) headers.set('Authorization', `Bearer ${token}`);
        return fetch(`${baseUrl}${path}`, { ...init, headers, credentials: 'include' }).catch(() => null);
    };

    const connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl, getToken ? { accessTokenFactory: getToken } : {})
        .withAutomaticReconnect()
        .build();

    // A reconnect is a new connection, so a new scope: the turn that was running is lost and
    // nothing will send BusyChanged(false) for it. Free the UI ourselves.
    const clear = () => {
        busy.value = false; currentAction.value = ''; confirmation.value = null; streaming.value = '';
    };
    connection.onreconnecting(clear);
    connection.onclose(clear);
    // Restore the conversation saved after the last StreamingCompleted (see below).
    connection.onreconnected(async () => {
        const saved = localStorage.getItem('mentor-session');
        if (!saved) return;
        const post = () => http(`/mentor/session?connectionId=${connection.connectionId}`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: saved,
        });
        let r = await post();
        if (r?.status === 404) { await new Promise(ok => setTimeout(ok, 250)); r = await post(); }  // scope not registered yet
    });

    connection.on('StreamingChunk',       (c: string)  => streaming.value += c);
    connection.on('StreamingCompleted',   async ()     => {
        if (streaming.value) messages.value.push({ role: 'assistant', text: streaming.value });   // no text: a segment closed before a confirmation
        streaming.value = '';
        const r = await http(`/mentor/session?connectionId=${connection.connectionId}`);   // save
        if (r?.status === 200) localStorage.setItem('mentor-session', await r.text());   // 204 = nothing yet
    });
    connection.on('BusyChanged',          (b: boolean) => busy.value = b);
    connection.on('Error',                (msg: string)=> messages.value.push({ role: 'error', text: msg }));
    connection.on('ActionExecuting',      (a: string)  => currentAction.value = a);
    connection.on('ActionCompleted',      (_: string)  => currentAction.value = '');
    connection.on('ActionFailed',         (e: string)  => currentAction.value = `Error: ${e}`);
    connection.on('ConfirmationRequired', (id: string, tool: string, msg: string) =>
        confirmation.value = { id, tool, message: msg });
    connection.on('NavigationRequested',  (url: string)=> router.push(url));
    connection.on('RagSourcesReady',      (s: any[])   => ragSources.value = s);
    connection.on('McpServerStatusChanged', (name: string, connected: boolean) =>  // 🔌 MCP badge
        mcpStatus.value = { ...mcpStatus.value, [name]: connected });
    connection.on('TeamMemberSpeaking',   (t: string, r: string) => currentAction.value = `${t} · ${r}`);
    connection.on('UIActionExecuting',    (name: string)=> currentAction.value = `UI: ${name}`);
    connection.on('UIActionCompleted',    (_: string)  => currentAction.value = '');
    connection.on('UIActionRequested',    (name: string, json: string | null) => {
        // Dispatch to your own UI action handlers
        document.dispatchEvent(new CustomEvent('mentor-ui-action', { detail: { name, json } }));
    });

    connection.start();

    onUnmounted(() => connection.stop());

    const sendMessage = async (text: string, snapshot?: any) => {
        if (snapshot) await connection.invoke('UpdatePageContext', snapshot);
        messages.value.push({ role: 'user', text });
        // Two arguments always: SignalR matches hub methods by argument count.
        await connection.invoke('SendMessage', text, null);
    };

    const respond = async (id: string, approved: boolean) => {
        confirmation.value = null;
        // HITL MUST use HTTP POST, not the hub (sequential hub dispatch would deadlock).
        const r = await http(`/mentor/approve?actionId=${id}&approved=${approved}`, { method: 'POST' });
        if (!r?.ok && r?.status !== 404) showError(`Your answer did not reach the server (${r ? `HTTP ${r.status}` : 'no response'}).`);  // 404: nothing waits for it any more
    };

    // Cancel over HTTP, not the hub — a hub call cannot run while SendMessage is streaming.
    const cancel = async () => {
        const r = await http(`/mentor/cancel?connectionId=${connection.connectionId}`, { method: 'POST' });
        if (!r?.ok && r?.status !== 404) showError(`Stop did not reach the server (${r ? `HTTP ${r.status}` : 'no response'}).`);  // 404: the turn had already ended
    };
    const reset  = () => { messages.value = []; connection.invoke('ResetSession'); };

    return { messages, streaming, busy, currentAction, ragSources, confirmation, mcpStatus,
             sendMessage, respond, cancel, reset };
}
```

```vue
<!-- MentorChat.vue -->
<template>
    <div class="mentor-chat">

        <!-- Messages -->
        <div class="messages">
            <div v-for="(m, i) in messages" :key="i" :class="`bubble bubble--${m.role}`">
                {{ m.text }}
            </div>
            <div v-if="streaming" class="bubble bubble--assistant">
                {{ streaming }}<span class="cursor">▋</span>
            </div>
            <div v-if="busy && !streaming" class="typing">···</div>
        </div>

        <!-- Action feedback bar -->
        <div v-if="currentAction" class="action-bar">
            <span class="pulse" /> {{ currentAction }}
        </div>

        <!-- RAG citations -->
        <div v-if="ragSources.length" class="citations">
            <a v-for="(s, i) in ragSources" :key="i"
               :href="s.sourceUrl" target="_blank" class="citation-chip">
                📄 {{ s.title ?? s.sourceUrl }}
            </a>
        </div>

        <!-- Confirmation dialog -->
        <div v-if="confirmation" class="confirm-dialog">
            <p>{{ confirmation.message }}</p>
            <button @click="respond(confirmation.id, true)">Confirm</button>
            <button @click="respond(confirmation.id, false)">Cancel</button>
        </div>

        <!-- Input -->
        <div class="input-bar">
            <input v-model="input" @keydown.enter="send"
                   placeholder="Ask anything..." :disabled="busy" />
            <button v-if="busy" @click="cancel">■ Stop</button>
            <button v-else @click="send" :disabled="!input.trim()">Send</button>
            <button @click="reset" title="New conversation">↺</button>
        </div>
    </div>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { useMentorHub } from './useMentorHub';

const input = ref('');
// Same origin, open hub: useMentorHub('/mentor-hub').
// Server on another origin, bearer token: useMentorHub('https://api.example.com/mentor-hub', () => auth.getToken()).
const { messages, streaming, busy, currentAction, ragSources, confirmation,
        sendMessage, respond, cancel, reset } = useMentorHub('/mentor-hub');

function send() {
    if (!input.value.trim() || busy.value) return;
    sendMessage(input.value);
    input.value = '';
}
</script>
```

---

### .NET MAUI / console

```csharp
// Install: Microsoft.AspNetCore.SignalR.Client
var connection = new HubConnectionBuilder()
    .WithUrl("http://your-api/mentor-hub")
    .WithAutomaticReconnect()
    .Build();

connection.On<string>("StreamingChunk",      chunk => Console.Write(chunk));
connection.On(        "StreamingCompleted",  ()    => Console.WriteLine());
connection.On<bool>(  "BusyChanged",         busy  => { /* show spinner */ });
connection.On<string>("Error",               msg   => Console.WriteLine($"Error: {msg}"));
connection.On<string>("ActionExecuting",     act   => Console.WriteLine($"[{act}]"));
connection.On<string>("ActionCompleted",     _     => { });
connection.On<string>("ActionFailed",        err   => Console.WriteLine($"Failed: {err}"));
connection.On<string, string, string>("ConfirmationRequired", async (id, tool, msg) => {
    Console.WriteLine($"Confirm: {msg} [y/n]");
    var approved = Console.ReadLine() == "y";
    // HITL MUST use HTTP POST, not the hub — a hub call while SendMessage awaits would deadlock.
    // Owner-checked: if the hub connection is authenticated, send the same bearer token here
    // (http.DefaultRequestHeaders.Authorization) or the answer is refused with 401.
    using var http = new HttpClient();
    await http.PostAsync($"http://your-api/mentor/approve?actionId={id}&approved={approved.ToString().ToLower()}", null);
});
connection.On<string>("NavigationRequested", url => Console.WriteLine($"Navigate: {url}"));
connection.On<JsonElement[]>("RagSourcesReady", s => Console.WriteLine($"{s.Length} sources"));
connection.On<string, string>("TeamMemberSpeaking", (t, r) => Console.WriteLine($"[{t}] {r}"));
connection.On<string, string?>("UIActionRequested", (name, json) => Console.WriteLine($"UI: {name}({json})"));
connection.On<string, bool>("McpServerStatusChanged", (name, ok) => Console.WriteLine($"MCP {name}: {(ok ? "online" : "offline")}"));

await connection.StartAsync();
// Two arguments always — a C# default does not travel over the wire.
await connection.InvokeAsync("SendMessage", "Ciao!", null);
Console.ReadLine();
await connection.StopAsync();
```

---

## Protecting the endpoints

`MapMentorAgentServer()` maps everything **unauthenticated by default**, because the package cannot
know how your application authenticates. That default is fine for a single-user demo and wrong for
anything else, so here is what each endpoint exposes and what to do about it.

| Endpoint | Open by default | What an anonymous caller can do | Protect it with |
|---|---|---|---|
| `/mentor-hub` | ✅ | Run turns — **your model spend** | `configureTransports` |
| `GET`/`POST /mentor/chat` | ✅ | Run turns — **your model spend** | `configureTransports` |
| `POST /mentor/approve` | — | For a confirmation raised over the **hub**: nothing, because only the principal it was raised for may answer it. A confirmation raised over **SSE** (or in-process) has no recorded owner, so anyone holding its `actionId` can answer it. Keep `/mentor/chat` authenticated and treat the `actionId` as a secret. When the hub connection closes, its turn ends with it, and a late answer gets `404` | *(enforced for hub connections)* |
| `POST /mentor/cancel` | — | Nothing: owner-only, same rule | *(enforced)* |
| `GET`/`POST /mentor/session` | — | Nothing: owner-only, same rule | *(enforced)* |
| `GET /mentor/admin/metrics` | — | Nothing, when `DashboardRole` is set | `DashboardRole` |
| `GET /mentor/tour` | ✅ | Read the tour — public screen names, identical for every user, no counts and no per-user data. Deliberate | — |
| `/mcp` *(if mapped)* | ✅ | Call every **ungated** `[MentorAction]` as an MCP tool (gated actions are withheld there) | `MapMentorAgentMcp(configure: ...)` |
| `/a2a` + `/.well-known/agent-card.json` *(if mapped)* | ✅ | Run A2A tasks against your assistant (**your model spend**) and read the agent card | `MapMentorAgentA2A(configure: ...)` |

MCP and A2A are mapped by their own calls, so `configureTransports` does not reach them. Both take the same kind of convention callback:

```csharp
app.MapMentorAgentMcp(configure: e => e.RequireAuthorization());
app.MapMentorAgentA2A(configure: e => e.RequireAuthorization());   // applied to /a2a and the agent card
```

### Requiring authentication on the transports

```csharp
app.MapMentorAgentServer(configureTransports: e => e.RequireAuthorization());
```

`configureTransports` is applied to the hub and both `/mentor/chat` endpoints — the three that run a
turn. It takes the ordinary `IEndpointConventionBuilder`, so a named policy works too:

```csharp
app.MapMentorAgentServer(configureTransports: e => e.RequireAuthorization("MentorUsers"));
```

> ### ⚠️ Unauthenticated callers, memory and rate limiting
>
> A caller with no principal is keyed by `AnonymousIdentity`. The default, `PerSession`, gives each
> **hub connection** its own key, and each `GET`/`POST /mentor/chat` request its own too, because every
> SSE request is its own session. Two anonymous callers therefore never read each other's memories,
> and an anonymous SSE caller has no memory across requests.
>
> `AnonymousIdentity = MentorAnonymousIdentity.Shared` puts every anonymous caller on the single key
> `"anonymous"`. One user's remembered facts are then injected into another user's turns, and
> `MemoryAutoCapture` (on by default) keeps writing into that shared bucket. Measured before
> `PerSession` became the default: one anonymous `POST /mentor/chat` stored a private code, and a
> second, entirely separate anonymous request read it back. Use `Shared` only for a single-user host.
>
> MentorAgent logs a warning at startup when `/mentor/chat` is left without authorization and either
> `UseMemoryContext` or `MemoryAutoCapture` is `true`. That includes the **defaults**
> (`MemoryAutoCapture = true`, `UseMemoryContext = false`), even though nothing is captured until
> `UseMemoryContext` is on. Require authentication if callers must keep their memory across
> connections, or turn memory off:
>
> ```csharp
> options.UseMemoryContext  = false;
> options.MemoryAutoCapture = false;
> ```
>
> The same key drives `RateLimitPerUser`. Under `PerSession`, an anonymous caller gets a **fresh
> allowance with every new hub connection, every SSE request and every A2A task**, so the limit does not
> bound an anonymous client that reconnects, calls `/mentor/chat` repeatedly or sends tasks to `/a2a`.
> Under `Shared`, every anonymous caller spends one allowance. To bound anonymous spend, require
> authentication on the transports (`configureTransports`) and on A2A
> (`MapMentorAgentA2A(configure: …)`).

### Why approve, cancel and session need no flag

Those three take an `actionId` or a `connectionId` and act on somebody's live conversation. They are
**owner-checked** by the package: when the connection was opened by an authenticated principal, only
that principal may use them — an anonymous caller gets `401`, a *different* signed-in user gets
`403`. Being signed in is not the same as being the owner, which is why a plain
`RequireAuthorization()` on them would not have been enough.

The principal is recognised by its `NameIdentifier` claim, else `sub`, else `oid`, else its name, so
Windows authentication and JwtBearer with `MapInboundClaims = false` pass the check too. A name never
matches another user's `NameIdentifier`.

When the connection carries no principal — an unauthenticated host, or an SSE client whose
confirmation never passed through a hub connection — there is nothing to compare and behaviour is
unchanged.

### Calling approve, cancel and session from an authenticated client

The owner check compares the principal on the **HTTP request** with the one that opened the hub connection. These calls must therefore carry the same identity as the hub. A bare `fetch` does not. With bearer tokens it sends no `Authorization` header, and cross-origin it sends no cookies. Either way the request is anonymous and gets `401`, and the confirmation, the Stop or the restore silently does nothing.

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${apiBase}/mentor-hub`, { accessTokenFactory: () => getToken() })
    .build();

// Bearer tokens: send the same token on the HTTP side
await fetch(`${apiBase}/mentor/approve?actionId=${id}&approved=true`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${await getToken()}` },
});

// Cookie auth, cross-origin: include the cookies (the CORS policy already has AllowCredentials)
await fetch(`${apiBase}/mentor/cancel?connectionId=${connection.connectionId}`, {
    method: 'POST',
    credentials: 'include',
});
```

---

## Multimodal image input

Users can send **images** along with their message — a screenshot of an error, a photo of a receipt, a product picture — and the model reasons about them. Built on the Agent Framework's native multimodal API: the user turn becomes a `ChatMessage` with a `TextContent` plus one `DataContent` (inline) or `UriContent` (remote) per image.

**Server setup** (off by default):

```csharp
builder.Services.AddMentorAgent(options =>
{
    options.ChatClient = azure.GetChatClient("gpt-4.1").AsIChatClient();   // must be vision-capable

    options.EnableImageInput    = true;
    options.MaxImageBytes       = 4 * 1024 * 1024;                          // per image (default 4 MB)
    options.MaxImagesPerMessage = 4;                                        // per turn  (default 4)
    options.AllowedImageTypes   = ["image/png", "image/jpeg", "image/webp"];// MIME allow-list
});
```

Every attachment is **re-validated server-side** against the allow-list, the size cap and the count cap before it reaches the model — client checks are only for fast feedback. A disallowed type or an oversized image is dropped with a warning log; images beyond `MaxImagesPerMessage` are dropped silently (the first N are kept), and with `EnableImageInput` off every attachment is ignored. The turn still runs with whatever passed.

> **SignalR message size — handled for you.** Attachments travel inside the `SendMessage` hub invocation as base64, and SignalR's default `MaximumReceiveMessageSize` is only **32 KB** — smaller than any real photo. Worse, exceeding it makes the server *abort the connection*, so the client sees no reply, no error and no busy indicator at all. When `EnableImageInput` is on, `AddMentorAgentServer()` therefore raises `HubOptions<MentorHub>.MaximumReceiveMessageSize` to `MaxImageBytes × MaxImagesPerMessage × 4/3 + 512 KB`. It is scoped to the MentorAgent hub, so your own hubs keep their limits, and it only ever raises a value you set yourself. Keep `MaxImageBytes`/`MaxImagesPerMessage` tight — they are what sizes this buffer.

### Wire format

```jsonc
{
  "mimeType":   "image/png",       // must be in AllowedImageTypes
  "dataBase64": "iVBORw0KGgo…",    // inline bytes, NO "data:" prefix   ─┐ exactly
  "url":        null,              // …or a public https URL            ─┘ one of the two
  "fileName":   "screenshot.png"   // optional, display only
}
```

| Transport | How to send |
|---|---|
| **SignalR** | `connection.invoke('SendMessage', text, attachments)` — **always send both arguments**; pass `null` when there are no images |
| **SSE** | `POST /mentor/chat` with `{ "message": …, "attachments": [ … ] }` (`GET` stays text-only) |

### React

```tsx
const fileToAttachment = (file: File) => new Promise<Attachment>((resolve) => {
  const reader = new FileReader();
  reader.onload = () => {
    const result = String(reader.result);
    resolve({ mimeType: file.type, dataBase64: result.slice(result.indexOf(',') + 1), fileName: file.name });
  };
  reader.readAsDataURL(file);
});

// upload · paste · drag & drop · URL — all produce the same Attachment[]
<textarea
  onPaste={async e => {
    const files = [...e.clipboardData.items]
      .filter(i => i.kind === 'file')
      .map(i => i.getAsFile())
      .filter((f): f is File => !!f && f.type.startsWith('image/'));
    if (files.length) setAttachments(await Promise.all(files.map(fileToAttachment)));
  }}
/>

await connection.invoke('SendMessage', text, attachments);
```

### Angular

```typescript
async onFiles(files: FileList) {
  this.attachments = await Promise.all([...files].map(f => this.toAttachment(f)));
}

private toAttachment(file: File): Promise<Attachment> {
  return new Promise(resolve => {
    const reader = new FileReader();
    reader.onload = () => {
      const r = String(reader.result);
      resolve({ mimeType: file.type, dataBase64: r.slice(r.indexOf(',') + 1), fileName: file.name });
    };
    reader.readAsDataURL(file);
  });
}

async send() {
  await this.connection.invoke('SendMessage', this.text, this.attachments.length ? this.attachments : null);
  this.attachments = [];
}
```

### Vue

```vue
<input type="file" accept="image/*" multiple @change="onFiles" />
<div @dragover.prevent @drop.prevent="onDrop">…</div>

<script setup>
const attachments = ref([]);

const toAttachment = file => new Promise(resolve => {
  const reader = new FileReader();
  reader.onload = () => {
    const r = String(reader.result);
    resolve({ mimeType: file.type, dataBase64: r.slice(r.indexOf(',') + 1), fileName: file.name });
  };
  reader.readAsDataURL(file);
});

const onFiles = async e => { attachments.value = await Promise.all([...e.target.files].map(toAttachment)); };
const onDrop  = async e => { attachments.value = await Promise.all([...e.dataTransfer.files].map(toAttachment)); };

const send = async () => {
  await connection.invoke('SendMessage', text.value, attachments.value.length ? attachments.value : null);
  attachments.value = [];
};
</script>
```

### .NET MAUI / console

```csharp
// MAUI: pick a photo from the gallery (or MediaPicker.CapturePhotoAsync() for the camera)
var photo = await MediaPicker.Default.PickPhotoAsync();
await using var stream = await photo!.OpenReadAsync();
using var ms = new MemoryStream();
await stream.CopyToAsync(ms);

var attachments = new[]
{
    new { mimeType = photo.ContentType, dataBase64 = Convert.ToBase64String(ms.ToArray()), fileName = photo.FileName },
};

await connection.InvokeAsync("SendMessage", "Cosa vedi in questa foto?", attachments);
```

### Rendering the thumbnail

```javascript
const src = a.url ?? `data:${a.mimeType};base64,${a.dataBase64}`;
```

> The bundled Blazor widget (`Novior.MentorAgent` / `Novior.MentorAgent.Blazor`) already implements upload, paste, drag & drop and URL out of the box — just set `EnableImageInput = true`.

---

## Hosted tools (web search, code interpreter, file search, images, remote MCP)

The Agent Framework's **provider-hosted tools** give the model capabilities that run on the *provider's* infrastructure during inference — no code on your server, and **nothing to implement in the client**: they are configured here and their results simply appear in the streamed answer.

```csharp
builder.Services.AddMentorAgent(options =>
{
    options.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter;

    // File search needs at least one vector store — without ids the tool is skipped (fail-closed)
    // options.HostedTools |= MentorHostedTools.FileSearch;
    // options.FileSearchVectorStoreIds = ["vs_abc123"];
    // options.FileSearchMaxResults     = 5;

    // Image generation. ⚠️ On Azure this is NOT enough on its own: Azure resolves the image
    // deployment from the x-ms-oai-image-generation-deployment HEADER, not from the tool payload.
    // Add it as a pipeline policy where you build the AzureOpenAIClient — see the samples'
    // Infrastructure/ImageDeploymentHeaderPolicy.cs — or every image turn fails with
    // "imagegen deployment must be provided through header".
    // options.HostedTools |= MentorHostedTools.ImageGeneration;
    // options.HostedImageModel = "gpt-image-1-mini";
    // options.HostedImageSize  = "1024x1024";   // the cost knob

    // Hosted MCP: the PROVIDER dials the server, so it must be reachable from the provider's
    // network (no localhost), and approval is enforced by the provider.
    // Not to be confused with options.McpServers, where this process is the MCP client.
    // options.HostedTools |= MentorHostedTools.HostedMcp;
    // options.HostedMcpServers = [
    //     new MentorHostedMcpServer
    //     {
    //         Name = "microsoft_learn", Url = "https://learn.microsoft.com/api/mcp",
    //         AllowedTools = ["microsoft_docs_search"], RequireApproval = true,
    //     }
    // ];
});
```

**Provider support is not universal:**

| Client | Function tools | Web search | Code interpreter | File search | Image gen | Hosted MCP |
|---|:--:|:--:|:--:|:--:|:--:|:--:|
| Azure OpenAI / OpenAI — **Responses** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Azure OpenAI / OpenAI — **Chat Completions** | ✅ | ✅¹ | ❌ | ❌ | ❌ | ❌ |
| Foundry (`AIProjectClient`) | ✅ | ✅² | ✅² | ✅² | ✅² | ✅² |

¹ Depends on the deployment; an unsupported one answers `400 unknown_parameter: web_search_options`. Availability is per **deployment** too, not only per client type.

² Only through an `IChatClient` set on `options.ChatClient`. `HostedTools` is applied on Path A only: with a pre-built `options.Agent` (how `AIProjectClient` is wired under *AI providers*) it is ignored with a startup warning — declare hosted tools on that agent yourself.

To get the full set, switch to the Responses client:

```csharp
var azure = new AzureOpenAIClient(endpoint, credential);
options.ChatClient = azure.GetResponsesClient().AsIChatClient("gpt-4.1");

// ⚠️ Required with Responses: the service owns the conversation and returns a conversation id, and
// AF refuses to combine that with a local ChatHistoryProvider — every turn would fail with
// "Only ConversationId or ChatHistoryProvider may be used, but not both".
options.UseServiceManagedHistory = true;   // disables MaxSessionMessages, ChatReducer + EnableCompaction
```

> **With model routing:** the strong model gets the *same* tool list, so build `StrongChatClient` on a client that supports hosted tools too — otherwise turns work until one is escalated and then fail. MentorAgent warns at startup when both are configured.

**Optional badge.** `options.ShowHostedToolsStatus = true` adds an amber pill to the Blazor widget header listing the active tools. A custom client does **not** need to mirror the list in its own configuration: on connect the server sends

```
HostedToolsDeclared(int flags)     // MentorHostedTools bit flags
```

with the set it actually enabled. Render the badge from that. A hand-kept copy drifts the moment the
server changes — it then claims tools the server dropped, or hides ones it gained. The event is purely
additive, so a client that ignores it behaves exactly as before.

```ts
const HOSTED = [[1,'Web search'],[2,'Code interpreter'],[4,'File search'],
                [8,'Image generation'],[16,'Remote MCP tools']] as const;

conn.on('HostedToolsDeclared', (flags: number) =>
  setHostedTools(HOSTED.filter(([bit]) => flags & bit).map(([, name]) => name)));
```

### Live activity over the wire

A hosted tool runs remotely and can take several seconds with nothing streamed. With `options.ShowHostedToolActivity` (default `true`) the server reports it as **SignalR hub events** — the SSE endpoint (`/mentor/chat`) has no frames for any of the signals below, so an SSE client shows no activity line or citations and **cannot receive generated images**. Use the hub if you enable image generation:

| Signal | Hub event | What to render |
|---|---|---|
| Tool started | `ActionExecuting(label)` | "Searching the web… · .NET 10 release notes", "Running code…", "Searching your documents…", "Generating the image…" |
| Tool finished | `ActionCompleted(key)` | clear the feedback line |
| Sources cited by web search **or file search** | `RagSourcesReady(sources)` | the citation chips you already render for RAG — same payload shape. A document cited from a vector store has no `sourceUrl` (there is nothing to open): render the title and the snippet |
| Generated images | `GeneratedImages(string[])` | attach to the message being committed; each entry is a `data:` URI or an absolute URL, ready for `<img src>` |
| Which hosted tools are on | `HostedToolsDeclared(int)` | sent **once on connect**, before any message: the active `MentorHostedTools` flags. Use it to render the capability badge instead of mirroring the list in the client — hosted tools are a server-side capability, and a hand-kept copy drifts the moment the server's configuration changes |

Only `GeneratedImages` is new. A client that ignores it keeps working exactly as before — it simply won't show generated images. A base64 image is bulky, so it is sent as its own message rather than folded into the stream, and only when the provider actually produced one.

```ts
// React / Angular / Vue — the whole client-side change
conn.on('GeneratedImages', (images: string[]) => { pendingImages.current = images; });

// And, if you show a capability badge, take its contents from the server rather than
// keeping a copy: flags are MentorHostedTools (1 web search, 2 code interpreter,
// 4 file search, 8 image generation, 16 hosted MCP).
conn.on('HostedToolsDeclared', (flags: number) => setHostedTools(decodeHostedTools(flags)));
```

**Also declared in the system prompt.** MentorAgent lists the enabled hosted tools in the coordinator's instructions automatically. Without it a coordinator holding a long list of application actions — and told never to invent capabilities — answers from memory instead of searching or running code.

**Limits by design.** Hosted tools never reach MentorAgent's function-calling middleware (there is no local invocation to intercept), so role checks, HITL confirmation, action feedback and per-tool metrics do not apply to them.

### Keeping them from firing when they are not needed

These are the expensive tools — a web search is billed per call, a file-search turn measured **8 249**
input tokens against **2 870** for a plain one — and they used to be declared on *every* message,
including *"hello"*. Three independent controls, doing three different jobs:

```csharp
options.EnableToolFiltering = true;        // required — all three live inside the semantic filter
options.EmbeddingGenerator  = embeddings;  // required — semantic, never keyword matching

options.FilterHostedTools           = true;   // default: declare a hosted tool only when relevant
options.HostedToolFilterMinScore    = 0.15f;  // default: a coarse pre-cut, keep it LOW
options.HostedToolDomainCheck       = true;   // default false: is this message about my app at all?
options.MaxHostedToolCallsPerSession = 10;    // 0 = unlimited: the only hard ceiling
```

- **Relevance** (`FilterHostedTools`) answers *which tool, if any*. Probabilistic: it lowers how often
  a paid tool fires on a message that did not need it, without promising a maximum.
- **Domain gating** (`HostedToolDomainCheck`) answers *should this application spend anything on this
  message at all*. A different question: *"draw me a dog"* scores high against the image tool because
  it genuinely is an image request, and is nonsense for a shop backend — which pays for the picture
  anyway. A small model decides, once per turn and **only** when a hosted tool already passed
  relevance, so ordinary conversation costs nothing.
- **The session cap** (`MaxHostedToolCallsPerSession`) is the maximum, and it is what survives a
  message the other two get wrong. ⚠️ The counter lives in the orchestrator scope: over **SignalR** it
  spans the whole connection, but a `POST /mentor/chat` call is its own session — a stateless HTTP
  client therefore gets a per-request cap, not a per-user one.

Tune from data, not guesswork — every score is logged at Debug:

```
[MentorAgent] Tool filtering: no action cleared minScore=0.35 (best: get_product 0.256); the 3 best-ranked
              that need no confirmation were sent: get_product 0.256, search_products 0.242, search_customers 0.234.
[MentorAgent] Hosted tool relevance: hosted:web_search scored 0.040 (min 0.15) — skipped.
[MentorAgent] Hosted tool relevance: hosted:image_generation scored 0.352 (min 0.15) — declared.
[MentorAgent] Hosted-tool domain check → OUT of scope (from the turn's classifier call).
[MentorAgent] Hosted tools withheld: the request is outside this application's scope.
              hosted:image_generation would have run.
[MentorAgent] Tool filtering: 10/63 tools sent (7 core + 3/51 matched + 0/5 hosted, minScore=0.35).
```

**Withholding a tool is only half the job.** The system prompt is built once while the tool list is
decided per turn, so a model told "you can generate images" and then handed no image tool resolves the
contradiction by *inventing* — a real turn answered an image request with a fabricated URL introduced
as "the image I created for you". MentorAgent therefore tells the model, in that same request, that
the capability is gone and why, and requires a plain refusal. Nothing is sent on turns where nothing
was withheld. It says the capabilities are not *for this request*, never that the request is outside the
application: the gate is strict about paid tools, and a request it turns down may still be one the
application's own actions answer (BUG-106). There is nothing to configure and nothing for your client to
handle: the reply simply says *"I can't generate images right now, but I can help you with…"* instead of
inventing a link.

> **Fail-open by design.** All three controls run inside the semantic tool filter, which is installed
> only with `EnableToolFiltering` **and** an `EmbeddingGenerator`. Without them nothing is filtered and
> hosted tools keep firing on every turn — with a startup warning naming the options being ignored,
> because a control that is switched on but never runs is worse than one that is off. Losing a
> capability because a model was not configured is worse than costing more than expected, and there is
> deliberately **no keyword fallback**.

---

## AI providers

```csharp
// Azure OpenAI — Chat Completions
options.ChatClient = new AzureOpenAIClient(endpoint, credential)
    .GetChatClient("gpt-4o").AsIChatClient();

// OpenAI direct
options.ChatClient = new OpenAIClient("sk-...")
    .GetChatClient("gpt-4o").AsIChatClient();

// Ollama (local) — through its OpenAI-compatible endpoint: same SDK, no extra package
options.ChatClient = new OpenAIClient(
        new System.ClientModel.ApiKeyCredential("ollama"),   // Ollama ignores it; the SDK requires one
        new OpenAIClientOptions
        {
            Endpoint       = new Uri("http://localhost:11434/v1"),
            NetworkTimeout = TimeSpan.FromMinutes(20),          // a CPU model can take minutes on a long prompt
        })
    .GetChatClient("llama3.2").AsIChatClient();
// Ollama's default context (4096 tokens) is shorter than MentorAgent's prompt and is truncated from the
// start without an error — create a variant with `PARAMETER num_ctx 16384` (ollama create).

// Azure AI Foundry — requires AIAgent, and:
//    <PackageReference Include="Microsoft.Agents.AI.Foundry" Version="[1.17.0-preview.260804.1]" />
options.Agent = new AIProjectClient(endpoint, credential)
    .AsAIAgent(model: "gpt-4o", instructions: "You are a helpful assistant.");
```

### Embedding model (optional)

Configure an embedding model to enable **semantic tool filtering** and **semantic memory relevance** (see [Token & cost optimization](#token--cost-optimization)). Everything works without it.

```csharp
// Azure OpenAI
options.EmbeddingGenerator = new AzureOpenAIClient(endpoint, credential)
    .GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

// OpenAI direct
options.EmbeddingGenerator = new OpenAIClient("sk-...")
    .GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();
```

---

## The three-level agent model

MentorAgent uses a three-level orchestration architecture.

### Level 1 — Direct actions

Plain C# methods on any DI-registered class become AI tools:

```csharp
public class OrderService
{
    [Description("Get order details by order ID")]
    public async Task<Order> GetOrderAsync(string orderId) => ...;

    // Tool name = method name in snake_case ("Async" dropped): cancel_order
    [MentorAction(Description = "Cancel an order", RequiresConfirmation = true)]
    public async Task<string> CancelOrderAsync(string orderId) => ...;
}
```

Register in DI and scan:
```csharp
builder.Services.AddScoped<OrderService>();
options.ScanAssemblies = [typeof(Program).Assembly];
```

Only public instance methods declared on the class itself are discovered (not inherited or static ones, and
not an empty `[Description]`), at every level. A method with `[Description]` alone has no roles and no
confirmation — those exist only on `[MentorAction]` — so give a method that changes data `[MentorAction]`.

### Level 2 — Specialized agents (Handoff)

```csharp
[MentorAgent(Name = "OrderAgent", Description = "Handles all order-related operations")]
public class OrderAgent : IMentorAgent
{
    [Description("Process a refund for an order")]
    public async Task<string> ProcessRefundAsync(string orderId, decimal amount) => ...;
}

builder.Services.AddScoped<OrderAgent>();
```

### Level 3 — Collaborative teams (Group Chat)

```csharp
[MentorTeam(
    Name          = "AnalysisTeam",
    Description   = "Analyzes business proposals before execution",
    TriggerOn     = ["analyze", "evaluate", "review"],
    HandoffTo     = ["OrderAgent"])]
public class AnalysisTeam : IMentorTeam
{
    [TeamMember(
        Role         = "DataAnalyst",
        Tools        = [typeof(ReportTools)],
        Instructions = "Analyze quantitative data and KPIs.")]
    public object? Analyst { get; set; }

    [TeamMember(
        Role         = "RiskAnalyst",
        Instructions = "Evaluate risks and compliance. Reply APPROVED or REJECTED.")]
    public object? RiskAnalyst { get; set; }

    [TeamTerminationCondition]
    public bool ShouldTerminate(string lastMessage, string lastSpeaker)
        => lastSpeaker == "RiskAnalyst" &&
           (lastMessage.Contains("APPROVED") || lastMessage.Contains("REJECTED"));
}
```

---

## Built-in AI tools

On top of the actions you declare, MentorAgent registers a handful of internal tools on the
coordinator. You never declare or register these — they appear based on your configuration, and they
are the reason the assistant can navigate, delegate and remember without you wiring anything.

| Tool | Appears when | What it does |
|---|---|---|
| `navigate_to` | Always (useful only with at least one `[MentorPage]`) | The assistant asks the client to change page. Over SignalR this surfaces as the **`NavigationRequested`** event, over SSE as a **`navigation`** frame — *your* client decides how to route (React Router, Angular Router, MAUI Shell) |
| `route_to_specialist` | At least one `[MentorAgent]`, agent-source agent (e.g. `Novior.MentorAgent.Declarative`) or `RemoteAgents` entry | `route_to_specialist(request, specialist?)`: hands the turn to a Level-2 specialist or a remote A2A agent through the handoff workflow and returns the specialist's words, or `NOT DELEGATED` / `DELEGATION FAILED`. On a host with `RemoteAgents` the answer opens with a note saying whether it is this application's data or the remote system's |
| `{action_name}` | A client registered UI actions for the current page | One tool per registered action, injected per call, visible only while that page is active |
| `remember` / `forget_all` | `UseMemoryContext = true` | Saves a durable fact about the user, or clears their memory on request. With `MemoryAutoCapture` the writer runs after the turn instead and `remember` is omitted |
| `load_skill` | `EnableSkills = true` | Loads the full instructions of a skill on demand, instead of paying for them on every turn |
| `read_skill_resource` | `EnableSkills = true` + a skill has resource files | Reads one resource file attached to a skill |

**What this means for a headless client:** these tools do not execute anything in your frontend by
themselves. `navigate_to` and the UI actions reach you as **events on the wire**; everything else runs
server-side. A React client that ignores the `NavigationRequested` event simply will not navigate —
the turn still completes normally.

---

## Agent Skills

Progressive disclosure: the AI sees only the skill name and description (~100 tokens) until it decides to load the full instructions.

**File-based** (place in `Skills/` folder):
```
Skills/
  refund-policy/
    SKILL.md         ← instructions + resources
    policy.md        ← attached resource (text files only — every file is read as UTF-8 text, so convert PDFs first)
```

**Class-based:**
```csharp
[MentorSkill(Name = "shipping", Description = "Shipping and tracking operations",
    Instructions = "## Shipping\n- Standard delivery takes 3-5 working days...")]
// No Instructions / InstructionsFile → Skills/shipping/SKILL.md is used if it exists,
// otherwise the description alone.
public class ShippingSkill { }
```

```csharp
options.EnableSkills = true;
options.SkillsFolder = "Skills";
```

---

## Page context and UI actions

When using SignalR, the client sends a page context snapshot before each message. Server-side, the AI sees this context in its system prompt and can invoke UI actions that are executed client-side.

**Client sends (before each message):**
```javascript
await connection.invoke('UpdatePageContext', {
    pageName: 'Orders',
    contextData: { activeFilter: 'Pending', visibleRows: '15' },   // values are strings (or null) — a number fails binding
    uiActions: [
        { name: 'highlight_row', description: 'Highlights a row', parameterHint: 'integer: row ID' },
        { name: 'open_modal',    description: 'Opens the create order modal' }
    ]
});
await connection.invoke('SendMessage', 'Highlight order 42', null);
```

**Server invokes the UI action — client receives:**
```javascript
connection.on('UIActionRequested', (actionName, paramJson) => {
    if (actionName === 'highlight_row') highlightRow(JSON.parse(paramJson));
    if (actionName === 'open_modal')    openModal();
});
```

---

## HITL — Confirming actions

When `RequiresConfirmation = true`, the server sends a `ConfirmationRequired` event and blocks until the user responds. The client must call `POST /mentor/approve` — **not** a hub method.

> ⚠️ **Why not `RespondToApproval` via hub?** ASP.NET Core SignalR processes hub messages sequentially per connection. While `SendMessage` is awaiting the confirmation TCS, the dispatcher cannot process any other hub message from the same connection. Calling `RespondToApproval` via hub would queue forever — a deadlock.

```javascript
// 1. Receive the confirmation request
connection.on('ConfirmationRequired', (actionId, toolName, message) => {
    showConfirmDialog(message, {
        // Send the same credentials the hub connection used: approve (and cancel) are owner-checked —
        // an authenticated connection answered without a token gets 401, another user 403.
        onConfirm: () => fetch(`/mentor/approve?actionId=${actionId}&approved=true`,
                               { method: 'POST', headers: { Authorization: `Bearer ${myAccessToken}` } }),
        onCancel:  () => fetch(`/mentor/approve?actionId=${actionId}&approved=false`,
                               { method: 'POST', headers: { Authorization: `Bearer ${myAccessToken}` } })
    });
});
```

> If the server is on a different origin, use the full URL: `http://localhost:5169/mentor/approve?actionId=...&approved=true`.

**What `POST /mentor/approve` answers.** `200`: the waiting turn received the answer. `404`: nothing is
waiting for it any more — it was answered already, the turn was stopped, or the connection that raised it
has closed (a connection's turn ends with it). `401` / `403`: the caller is not the connection's
principal. A `404` is not a fault: close the dialog.

### What triggers a confirmation

Three sources, all server-side — the client only ever sees `ConfirmationRequired`:

```csharp
builder.Services.AddMentorAgent(options =>
{
    // 1. Your own actions
    // [MentorAction(Description = "...", RequiresConfirmation = true)]

    // 2. Every tool of an MCP server
    options.McpServers = [
        new MentorMcpServer {
            Name = "filesystem", Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/data"],
            RequiresConfirmation = true,
        }
    ];

    // 3. By tool name — the way to gate tools you don't own
    options.RequiresApproval = tool => tool.StartsWith("delete_") || tool is "write_file";
});
```

### Native (Agent Framework) approval mode

```csharp
options.HitlMode = MentorHitlMode.Native;   // default: MentorHitlMode.Blocking
```

`Blocking` (default) parks the turn on a `TaskCompletionSource` — one model round-trip, streaming stays alive. `Native` uses the AF standard instead (`ApprovalRequiredAIFunction` → `ToolApprovalRequestContent` → `ToolApprovalResponseContent`), which costs one extra round-trip per approved call but makes the flow interoperable with AF workflows and AF-native hosts.

**Your clients need no changes.** Both modes emit the same `ConfirmationRequired(actionId, toolName, message)` event and accept the same `POST /mentor/approve` reply, so React/Angular/Vue/WASM code written for one mode works unchanged with the other.

A declined action reads the same to the model in both modes — the action did not run, nothing was checked or changed, no other reason is to be given (BUG-115, rc.14). Stop pressed while a Native confirmation waits leaves that request open in the stored conversation; the next message on the same connection closes it, as not run, in the same request as the new question (BUG-116, rc.14 — before, every later turn failed until a reset). The same holds when the run that carried the user's answer fails or is stopped: a confirmed action may then have run, and is reported to the model as of unknown outcome, never run again.

### Every level is gated

Level-2 specialists and Level-3 team members run their own function-calling loop inside the workflow, out of the coordinator middleware's reach, so their tools are wrapped in a `GatedAIFunction` — the gate travels with the tool. `RequiredRoles`, `RequiresConfirmation`, action feedback, `NavigateTo`, `OnToolResult`/`OnException` and per-tool metrics apply identically whether the coordinator calls a tool directly or delegates via `route_to_specialist`.

> Nested tools always use the blocking confirmation flow, even under `HitlMode = Native` — an AF `ToolApprovalRequestContent` raised inside a workflow never surfaces to the orchestrator. Same event, same `POST /mentor/approve`, asked exactly once.

---

## Page navigation

Register pages in the server project — the AI uses them to navigate autonomously and to understand what pages exist in the application.

```csharp
// Server project — scanned via options.ScanAssemblies
// One class per page, placed anywhere in the assembly.

[MentorPage(Url = "/orders", Name = "Orders",
    Description = "Order list with filters and status management")]
public class OrdersPage { }

[MentorPage(Url = "/products", Name = "Products",
    Description = "Product catalog with stock and pricing",
    HasUIActions = true,     // waited on only inside a Blazor Server circuit — never for hub/SSE/A2A turns
    ReadyTimeout = 3000)]    // ms — default is 2000
public class ProductsPage { }

[MentorPage(Url = "/fulldemo", Name = "Full Demo",
    Description = "Complete feature demo — UIActions, HITL, navigation")]
public class FullDemoPage { }
```

> ⚠️ **If a page is missing its `[MentorPage]` attribute, the AI will say the page does not exist** — even if the route is valid. Always add the attribute for every page you want the AI to be aware of.

The AI calls `navigate_to("/orders")` automatically after relevant actions, or when the user asks to go to a page by name.

> **`HasUIActions` on a headless server.** `navigate_to` waits for `PageContext.SignalReady()` only when the turn runs inside an interactive Blazor Server circuit. A turn that arrives over the SignalR hub (WebAssembly, React, MAUI…), SSE or A2A has no circuit, so nothing on the server could ever signal "ready": `navigate_to` does not wait (a Debug log records it) and returns at once. The new page's UI actions reach the model with the client's next `UpdatePageContext` snapshot — that is, on the user's next message. Setting `HasUIActions` costs nothing here, so one `[MentorPage]` class can serve both a Blazor Server host and headless clients.

---

## Contextual memory

```csharp
options.UseMemoryContext   = true;
options.MemoryContextCount = 10;    // max facts injected per session

// Reliable capture (default true): a dedicated post-turn LLM call extracts durable user facts
// (name, role, team, preferences) and stores them — no dependence on the model calling remember().
options.MemoryAutoCapture       = true;
// Inject only the memories semantically relevant to the message (identity/preference facts always
// kept) instead of the last N. Requires EmbeddingGenerator (see AI providers).
options.MemoryRelevanceFiltering = true;
```

**How capture works** — on Path A (a `ChatClient` is configured) `MemoryAutoCapture` is the writer: after each user message a small extraction call saves facts reliably, even for phrasings like *"Ciao, mi chiamo Antonio"*. The redundant `remember` tool is dropped on this path; `forget_all` stays. On Path B (a pre-built `Agent`, no `ChatClient`) it falls back to the `remember` tool. Verify in the logs: `[MentorAgent:Memory] Auto-capture saved 1 fact(s): user_name`.

> ⚠️ The default store is **in-memory** (lost on restart, not shared across instances). For production register a persistent store **before** `AddMentorAgent()`:
```csharp
builder.Services.AddSingleton<IMentorMemoryStore, RedisMemoryStore>();
```
> With authentication configured, memory is isolated **per user** (see [Authentication](#authentication--per-user-memory-rate-limiting-and-roles)).

---

## RAG — Retrieval-Augmented Generation

```csharp
builder.Services.AddScoped<IMentorRagSource, MyVectorDbSource>();

options.UseRag         = true;
options.RagResultCount = 3;
options.RagMinScore    = 0.7f;  // relevance threshold — nothing below it is injected; optional, see below
options.ShowRagSources = true;  // Blazor widget only: hub clients receive RagSourcesReady either way
```

Retrieval is **semantic and always-on**: your `IMentorRagSource` scores documents (e.g. cosine similarity) and `RagMinScore` filters them, so a pure command or greeting simply retrieves nothing above the threshold and injects nothing — no keyword pre-gate needed. When you do not set `RagMinScore`, the source's `DefaultMinScore` applies if it declares one; a source with cosine scores should, because the option's default (2) suits keyword scores.

Implement `IMentorRagSource`. To filter by the signed-in user, the page or the conversation, implement the `SearchAsync(MentorRagRequest, CancellationToken)` overload as well, and add `Metadata` to a result for the model to weigh. `Metadata` also reaches the client with the chips and may be repeated by the model, so nothing in it should be hidden from the user. `RecentMessages` holds the last three turns of the conversation: on the hub, the connection's; over SSE, where each `/mentor/chat` request is its own conversation, it is empty. The MentorAgent README, under *RAG*, has the details:
```csharp
public class MyVectorDbSource : IMentorRagSource
{
    public float? DefaultMinScore => 0.6f;   // cosine similarity: 0.5 loose … 0.75 strict

    public async Task<IReadOnlyList<MentorRagResult>> SearchAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var results = await _vectorDb.SearchAsync(query, maxResults);
        return results.Select(r => new MentorRagResult(
            Content:   r.Text,
            SourceUrl: r.Url,
            Title:     r.Title,
            Score:     r.Score)).ToList();
    }
}
```

---

## Streaming responses

Every reply is streamed token by token — there is nothing to enable. Over **SSE** each chunk arrives as
a `data:` line; over **SignalR** as a `StreamingChunk` event. Your client appends chunks to the current
bubble and re-renders; the same `CancellationToken` runs through the whole pipeline (RAG, LLM call,
tool execution), so stopping a turn really stops the work rather than just hiding the output.

```ts
// SSE — the whole streaming client
const res  = await fetch('/mentor/chat?message=' + encodeURIComponent(text));
const read = res.body!.pipeThrough(new TextDecoderStream()).getReader();
for (;;) {
  const { value, done } = await read.read();
  if (done) break;
  for (const line of value.split('\n')) {
    if (!line.startsWith('data: ')) continue;
    const evt = JSON.parse(line.slice(6));
    if (evt.type === 'chunk') appendToCurrentBubble(evt.text);
  }
}
```

**Stopping a turn.** Show a ■ Stop button while a turn is in flight. Over **SignalR**, call
`POST /mentor/cancel?connectionId=<connection.connectionId>` (owner-checked: send the same credentials as the hub).
Over **SSE** there is no connection id — abort the request instead; the server cancels the turn when the client goes away:

```ts
const ctrl = new AbortController();
fetch('/mentor/chat?message=' + encodeURIComponent(text), { signal: ctrl.signal });
stopButton.onclick = () => ctrl.abort();
```

Whatever was already streamed stays in the transcript.

> ⚠️ Cancel is an **HTTP endpoint, not a hub method**, and this is not a style choice: SignalR
> dispatches at most one hub invocation at a time per connection, so a `CancelRequest` hub call would
> sit in the queue behind the very `SendMessage` it is meant to abort and only run once that turn had
> already finished. The same reasoning applies to [`/mentor/approve`](#hitl--confirming-actions).

---

## Token & cost optimization

Novior.MentorAgent.Server minimizes the tokens sent on every request. Some optimizations are always on; two are opt-in.

**Always on:** a slim, cache-friendly system prompt (stable prefix, volatile data last) and per-call token logging:
```
[MentorAgent] Tokens — model: gpt-4.1, in: 1979, out: 62, call total: 2041, 812ms | session: 1979+62=2041 over 1 call(s)
```

### Semantic tool filtering

Every tool is serialized as a JSON schema into each request — the biggest per-call cost when you have many tools (L1 actions + MCP). With filtering, only the tools **semantically relevant** to the message are sent; the AI still chooses freely among them. It **requires** `EmbeddingGenerator` (see [AI providers](#ai-providers)) — without one, filtering is skipped and all tools are sent (with a warning); there is no keyword fallback.

```csharp
options.EmbeddingGenerator  = new AzureOpenAIClient(endpoint, credential)
    .GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

options.EnableToolFiltering = true;
options.ToolFilterMaxTools  = 12;    // max matched business tools (core tools always kept)
options.ToolFilterMinScore  = 0.35f; // cosine-similarity threshold (higher = stricter)
options.ToolFilterMinTools  = 0;     // default: the threshold alone (a trade-off - read the core README first)
```

Core tools (navigation, memory, routing, teams, skills, UI actions) are always kept. When nothing clears the threshold, the three best-ranked actions that need no confirmation and no role travel anyway — an MCP tool only when its server marks it `readOnlyHint` — because cosine is not calibrated and a question that needs data can score below a greeting (BUG-106); give every action that changes data `RequiresConfirmation`. When only actions that need confirmation clear it, the three best-ranked that do not travel with them (BUG-099). Log (Debug): `Tool filtering: 10/47 tools sent (7 core + 3/40 matched + 0/0 hosted, minScore=0.35)`, preceded on such a turn by `no action cleared minScore=0.35 (best: …); the 3 best-ranked that need no confirmation were sent: …`.

### History compaction

As a conversation grows it is re-sent on every call. Compaction shrinks it intelligently (collapse old tool results → keep the last N turns → hard token-budget backstop) instead of a blunt cut.

```csharp
options.EnableCompaction         = true;
options.CompactionTokenThreshold = 4000;  // token budget that triggers compaction
options.CompactionMaxTurns       = 8;     // recent turns kept intact
```

> Local history only (Path A / `ChatClient`: the built-in provider or your `ChatHistoryProviderFactory` / `ChatHistoryProvider`) — not installed with service-managed history (Foundry, Responses API with `store`). It changes what is sent to the model, never the stored conversation (BUG-104).

### Semantic RAG & memory

Both inject context only when relevant, with no keyword heuristics: RAG via the vector search + `RagMinScore` (see [RAG](#rag--retrieval-augmented-generation)); memory via `MemoryRelevanceFiltering` (see [Contextual memory](#contextual-memory)). On Path A, `MemoryAutoCapture` also drops the `remember` tool schema from every call.

---

## Middleware & extensibility

Robustness, tracing and an admin cost view are all configured on the server — a remote client sees
the *effects* (a blocked message, a friendlier error, a redacted tool result) but configures none of it.

Every hook below is optional and defaults to today's behaviour, so you can adopt them one at a time:

```csharp
// Built-in (no code): LLM safety checks on input and output.
options.EnableSafetyCheck       = true;   // moderate the user message
options.EnableOutputSafetyCheck = true;   // moderate the reply (buffers → no live streaming that turn)
options.SafetyCheckTimeout      = TimeSpan.FromSeconds(15);         // both checks (Zero = no limit)
options.SafetyCheckFailure      = MentorSafetyCheckFailure.Allow;  // no verdict: Allow = fail open, Block = refuse

// Custom hooks (replace/extend the built-ins):
options.InputGuardrail  = (msg, ct)   => Task.FromResult(IsSafe(msg));      // replaces EnableSafetyCheck
options.OutputGuardrail = (reply, ct) => Task.FromResult(IsSafeReply(reply)); // replaces EnableOutputSafetyCheck
options.OnToolResult    = (tool, result) => Truncate(result, maxChars: 2000); // transform a tool result
options.OnException     = ex => ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase)
    ? "The service is busy, please retry shortly." : null;
options.ConfigureChatClientPipeline = b => b.UseLogging();   // insert your own DelegatingChatClient / AF middleware
```

**Why each hook exists.** `InputGuardrail` / `OutputGuardrail` replace the built-in LLM moderation when
you already own that decision (an existing classifier, a per-tenant policy) — note that the output
guardrail must buffer the reply, so that turn loses live streaming. `OnToolResult` is the supported
place to trim or redact what a tool returns *before it reaches the model*, which is where oversized
payloads and unwanted personal data actually cost you tokens. `OnException` maps a raw provider error
onto something a user can read. `ConfigureChatClientPipeline` is the escape hatch: any
`DelegatingChatClient` or Agent Framework middleware of your own, inserted outermost. The pipeline
is built with the host's `IServiceProvider`, so middleware that resolves services (`UseLogging()`
takes its `ILoggerFactory` from DI) works without extra wiring. The built-in output check, like the
input check, runs on `ClassifierChatClient` and is metered; `SafetyCheckTimeout` and
`SafetyCheckFailure` apply to both.

---

## Observability (OpenTelemetry)

Off by default. Turn it on and MentorAgent emits GenAI-convention traces and metrics that any
OpenTelemetry backend already understands — you supply the exporter, the library never chooses one:

```csharp
options.EnableObservability = true;
options.ObservabilityIncludeSensitiveData = builder.Environment.IsDevelopment(); // dev only

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("MentorAgent").AddOtlpExporter())
    .WithMetrics(m => m.AddMeter("MentorAgent").AddOtlpExporter());
```

Emits GenAI-convention spans/metrics for the chat client (LLM) calls + MentorAgent per-turn/tool spans and counters under the source/meter named by `ObservabilitySourceName` (default `"MentorAgent"`).

> ⚠️ `ObservabilityIncludeSensitiveData` adds prompts and completions to the spans. That is the whole
> conversation — user input included — landing in your tracing backend, so keep it to Development.

---

## Token & cost dashboard

Admin-only, Azure-style: **per-model** breakdown (cheap / strong / embedding) with a model selector, **temporal charts** (tokens / requests / latency), and a per-model cost table. Supply prices, then read the snapshot from the endpoint (or `IMentorMetrics.GetSnapshot()` in-process):

```csharp
options.ModelPricing = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
{
    ["gpt-4.1"] = new ModelPrice(2.00m, 8.00m),                  // cheap chat
    ["o3"]      = new ModelPrice(2.00m, 8.00m),                  // strong routing
    ["text-embedding-3-small"] = new ModelPrice(0.02m, 0.00m),  // embedding
};
options.DashboardRole = "Admin";   // role required for the endpoint; "" leaves it open (dev only)
```

`MapMentorAgentServer()` exposes **`GET /mentor/admin/metrics`** returning a `MentorMetricsSnapshot` — now carrying the per-model breakdown (`Models`) and hourly time series (`MetricsRetention`, 7d) plus tokens, cost, deflection and top actions — gated by `DashboardRole`. Fetch it from your React/Vue admin UI, or bind `<MentorDashboard Snapshot="..."/>` in a WASM client to get the identical charts. Never expose it to end users. Cost appears only for priced models — key `ModelPricing` by the model id in the snapshot (for Azure OpenAI, your deployment name).

> A non-empty `DashboardRole` requires ASP.NET Core authentication/authorization to be configured (`app.UseAuthentication()` / `app.UseAuthorization()`); otherwise the endpoint has authorization metadata with no middleware to enforce it. Use `DashboardRole = ""` only for local development.

**Localization.** `<MentorDashboard/>` is translated through `MentorLocalizer` (10 languages, English fallback). A WASM/Blazor client has no MentorAgent DI, so pass the language: `<MentorDashboard Snapshot="..." Language="MentorLanguage.Italian" />`.

### Persistence (optional)

By default the snapshot is in-RAM and resets on restart. Register an `IMentorMetricsStore` **before** `AddMentorAgent()` for durability or an external source — the endpoint then returns `await store.QueryAsync() ?? metrics.GetSnapshot()`. The order matters: `AddMentorAgent()` only adds the seed-and-flush service when a store is already registered, so a store added after it is read by the endpoint but never persisted to:

```csharp
// Local durability: seed on startup + timed/shutdown flush (JSON/DB).
builder.Services.AddSingleton<IMentorMetricsStore, FileMetricsStore>();

// External source: read the aggregate OpenTelemetry already exported (Prometheus / Azure Monitor).
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IMentorMetricsStore, PrometheusMetricsStore>();   // or AzureMonitorMetricsStore
```

Working `FileMetricsStore`, `PrometheusMetricsStore` and `AzureMonitorMetricsStore` ship in the `MentorAgentServer` sample (`Metrics/`). The external readers query the same backend the OpenTelemetry export writes to — so persistence and multi-instance aggregation come from [Observability](#observability-opentelemetry), and the dashboard just reads it.

`MetricsPersistenceInterval` (default 30 seconds) controls how often the registered store is flushed;
`MetricsRetention` (default 7 days) bounds how much of the hourly time series the snapshot carries.

---

## Model routing

Cheap model for simple turns, strong model for complex ones — a real cost lever. Set `StrongChatClient` and pick a **strategy** (all avoid keyword matching on user text):

```csharp
options.StrongChatClient = new AzureOpenAIClient(endpoint, credential).GetChatClient("gpt-4o").AsIChatClient();
options.RoutingStrategy  = MentorRoutingStrategy.Semantic;   // Semantic | Classifier | Cascade | Custom
```

- **`Semantic`** — embeds the message, escalates on cosine similarity ≥ `RoutingThreshold` (0.35) to a "complex" exemplar. Multilingual, ~free; requires `EmbeddingGenerator`.
- **`Classifier`** — a tiny LLM call labels the turn `SIMPLE`/`COMPLEX`.
- **`Cascade`** — serves on cheap, judges completeness, re-runs on strong only if it fell short.
- **`Custom`** — your predicate via `UseStrongModelAsync` (async, whole conversation) or legacy `UseStrongModel`.

Active only when `StrongChatClient` is set; the chosen model is logged; any routing failure falls back to cheap. The [dashboard](#token--cost-dashboard) attributes tokens and cost **per model**, so cheap vs strong spend is broken out separately (a configured strong model shows up even before any turn escalates to it).

> ⚠️ If you also enable [hosted tools](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp),
> build `StrongChatClient` on a client that supports them too: the strong model receives the *same*
> tool list, so otherwise everything works until a turn escalates and that one fails with an
> unknown-parameter error. MentorAgent warns at startup when both are configured.

---

## Structured outputs

When you need a **typed object** rather than prose — an extraction step, a form pre-fill, a value your
own C# code will branch on — asking for JSON in the prompt and parsing the answer is unreliable.
`IMentorStructured` derives a JSON schema from your type, constrains the model to it, and hands back
the deserialized instance:

```csharp
public record ExtractedOrder(string Customer, string[] Products, decimal Total);

// Inject IMentorStructured (registered by AddMentorAgent); the result is T? — null when nothing could be produced
var order = await structured.GenerateAsync<ExtractedOrder>(userText, "Extract the order details.");
Console.WriteLine(order.Total);      // already a decimal, no parsing
```

This is a **separate call**, not part of the chat turn: use it from your own endpoints and background
jobs, where the caller is code rather than a person.

---

## Rich responses (tables & lists)

`EnableRichResponses` (default `true`) nudges the coordinator to format structured data as **Markdown tables / lists**:

```csharp
options.EnableRichResponses = true;   // false → terse plain-text replies
```

The **Blazor/WASM widget renders this automatically** (XSS-safe — model text is HTML-encoded before any tag is emitted). If you drive the SSE/hub from a **custom React/Vue client**, render the Markdown on your side (e.g. `react-markdown` + `remark-gfm`) to get the tables.

> ⚠️ If you render Markdown yourself, **do not** inject the model's output as raw HTML. Treat it as
> data: a Markdown renderer that escapes HTML (the default in `react-markdown`) is the safe choice.
> The Blazor widget HTML-encodes every piece of model text before emitting any tag, for this reason.

This is Level 1 — Markdown the model writes. [Cards](#generative-ui--cards-level-2) are the level above.

---

## Generative UI — cards (Level 2)

A tool returns a structure instead of a string, and the client renders it:

```csharp
[MentorAction(Description = "Shows an order")]   // tool name: get_order (from the method name)
public MentorCard GetOrder(int id) => new("order")
{
    Title    = $"Order #{id}",
    Subtitle = "Mario Rossi",
    Accent   = MentorCardAccent.Success,
    Fields   = [ new("Status", "Shipped"), new("Total", "€ 2.599,98") ],
    Actions  = [ new("Open", MentorCardActionKind.Navigate, $"/orders/{id}") ],
};
```

A result is a card by its shape, not its .NET type: an object with a non-empty string `kind`, or an array of them (see *Generative UI — cards* in the MentorAgent README).

Cards reach remote clients two ways, both **additive** — a client that ignores them still receives the full answer as text:

| Transport | How it arrives |
|---|---|
| SignalR | `Cards` hub event, payload `MentorCard[]` |
| SSE | a `{"type":"cards","cards":[…]}` frame in the stream |

```
data: {"type":"chunk","text":"Here are your orders."}
data: {"type":"cards","cards":[{"kind":"order","title":"Order #1001","subtitle":"Mario Rossi",
       "fields":[{"label":"Status","value":"Shipped"}],
       "actions":[{"label":"Open","kind":"navigate","value":"/orders/1001"}],
       "accent":"success"}]}
data: {"type":"completed"}
```

The **SSE** frame is camelCase with **string** enums — you read `"kind": "navigate"`, not a number whose meaning would shift the day a member is inserted into the enum. The **SignalR** `Cards` event uses the hub's default JSON protocol: property names are camelCase too, but enums arrive as **numbers** (`kind`: 0 = SendMessage, 1 = Navigate, 2 = UIAction; `accent`: 0 = Default, 1 = Success, 2 = Warning, 3 = Danger, 4 = Info). Normalise before rendering if you read both transports:

```ts
const KINDS = ['sendMessage', 'navigate', 'uiAction'] as const;
const ACCENTS = ['default', 'success', 'warning', 'danger', 'info'] as const;
const norm = (c: any): Card => ({
  ...c,
  accent: typeof c.accent === 'number' ? ACCENTS[c.accent] : c.accent,
  actions: c.actions?.map((a: any) => ({ ...a, kind: typeof a.kind === 'number' ? KINDS[a.kind] : a.kind })),
});
conn.on('Cards', (cards: any[]) => cards.map(norm).forEach(render));
```

### Rendering them in a custom client

```ts
type CardAction = { label: string; kind: 'sendMessage' | 'navigate' | 'uiAction'; value: string };
type Card = {
  kind: string; title?: string; subtitle?: string; imageUrl?: string;
  accent: 'default' | 'success' | 'warning' | 'danger' | 'info';
  fields?: { label: string; value?: string }[];
  actions?: CardAction[];
};

function onFrame(frame: any) {
  if (frame.type !== 'cards') return;
  for (const card of frame.cards as Card[]) render(card);
}

function onCardAction(a: CardAction) {
  if (a.kind === 'navigate')    router.push(a.value);
  if (a.kind === 'sendMessage') sendMessage(a.value);      // your existing send path
  if (a.kind === 'uiAction')    runLocalAction(a.value);   // your own client-side registry
}
```

**You can act on `value` without validating it.** Every card is built by server-side application code — the model only decides *when* the tool runs — so labels and URLs are yours, not something the conversation talked the assistant into producing. That property is what makes it reasonable to give a chat message buttons at all.

`uiAction` is the one kind a headless client must implement itself: there is no page-registered handler outside Blazor, so map the name onto whatever your frontend does.

### Turning it off

```csharp
options.EnableGenerativeCards = false;   // default true
```

The cards still go to the model, so the assistant keeps answering — it describes the data instead of your client showing it.

---

## Declarative agents (YAML)

Level-2 specialists can be defined in files instead of C# classes, via the optional [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) package:

```csharp
builder.Services.AddMentorAgentDeclarative(o => o.Directory = "Agents");
```

```yaml
kind: Prompt
name: ShippingAgent
description: Answers questions about deliveries
instructions: |
  You handle shipping questions only. Never invent a tracking number.
tools:
  - kind: function
    name: get_all_orders
```

They join the same handoff graph as `[MentorAgent]` classes, so **no protocol change**: `route_to_specialist` reaches them and every transport works unchanged.

The `tools` section names tools your application already exposes, and they arrive already wrapped in MentorAgent's gate — `RequiredRoles` and human approval stay in force inside the agent's own function-calling loop. Only `kind: function` entries are kept, and every entry needs a `kind`. Entries of kind `webSearch`, `codeInterpreter`, `fileSearch` or `mcp` are **removed** from the agent with a warning (e.g. `removed web_search ...`). Web search, code interpreter, file search and MCP are configured by the host (`options.HostedTools`, `options.McpServers`). A definition file therefore cannot grant itself a capability the application does not have.

> **Treat definition files as code** and load them only from deploy-time paths: a file chooses the
> model, writes the system instructions and names the callable tools.

For agents from a database or a configuration service, implement `IMentorAgentSource` and register it directly.

---

## Onboarding tour

A short guided tour shown the first time a user opens the assistant, **generated from this server's own surface** — the pages registered with `[MentorPage]` and the descriptions of the assistant's tools. A hardcoded tour is correct the day it is written and wrong three releases later; this one cannot describe a screen that no longer exists.

```csharp
options.EnableOnboardingTour = true;
```

The steps are served as JSON:

```bash
curl http://localhost:5169/mentor/tour
```

```jsonc
[
  { "title": "Benvenuto su ShopFlow", "body": "Gestisci prodotti, ordini e clienti.",
    "url": null, "tryAsking": null },
  { "title": "Gestione Ordini", "body": "Visualizza, filtra e modifica lo stato degli ordini.",
    "url": "/data/orders", "tryAsking": "Quanti ordini sono in attesa?" }
]
```

`tryAsking` is the field that earns the feature: users rarely fail to *find* an in-app assistant, they fail to know what to ask it, and an example drawn from the app's own tools answers that better than any generic hint. Render it as a button that sends the text as an ordinary message.

### Rendering it in a custom client

```ts
type TourStep = { title: string; body: string; url: string | null; tryAsking: string | null };

const steps: TourStep[] = await fetch('/mentor/tour').then(r => r.json());

// Show once per user — the server does not track who has seen it.
if (steps.length && !localStorage.getItem('tour.seen')) {
  showTour(steps, {
    onAsk:   (q: string) => sendMessage(q),        // your existing send path
    onOpen:  (url: string) => router.push(url),
    onClose: () => localStorage.setItem('tour.seen', '1'),
  });
}
```

Four things the endpoint guarantees, so your client does not have to:

- **Every `url` is a real registered page.** A model asked to describe screens will invent a plausible one; generated URLs are checked against the registered pages and an unknown one is stripped, keeping the step's text. You can navigate to `url` without validating it.
- **Generated once per process.** The response is identical for every user and is cached, so calling it on every page load costs nothing and cannot run up model spend.
- **Never empty for lack of a model.** With no `ChatClient`, or on a generation failure, it falls back to a deterministic tour built from the same discovery data.
- **No per-user data.** The payload is public screen names and capability descriptions — the same things the assistant states in conversation — which is why the endpoint is not role-gated, unlike `/mentor/admin/metrics`.

> **Seen-state is the client's job.** The server has no idea who has already taken the tour, and
> deliberately so: it would mean per-user storage for a cosmetic flag. `localStorage` is enough.

> **No voice options here.** Streaming text-to-speech, barge-in and hands-free (`VoiceStreaming`,
> `VoiceBargeIn`, `VoiceHandsFree`, `VoiceRate`) are browser behaviour and live in the **client's**
> options — `Novior.MentorAgent.Blazor`, or your own Web Speech API code. A headless server never speaks;
> it receives a transcript as an ordinary message. `EnableVoiceInput` / `EnableVoiceOutput` exist on
> `MentorOptions` only as a declaration of intent for the Blazor widget.

---

## Evaluation & regression testing

`MentorEvaluator` wraps the **Agent Framework's native evaluation** (`agent.EvaluateAsync` + `LocalEvaluator`). Inject it in your tests to gate CI on token/quality regressions:

```csharp
var report = await evaluator.RunAsync(
    [ new EvalCase("Hello", "A short greeting.", MaxTokens: 300) ],
    new MentorEvalOptions { SystemInstructions = mySystemPrompt, Judge = true, MinQuality = 0.6, MaxTotalTokens = 4000 });
report.ThrowIfFailed();
```

Plug native evaluators for production-grade quality & safety — `Checks` (e.g. `EvalChecks.ToolCalledCheck(...)`) and `Evaluators` (`FoundryEvals`, or MEAI quality/safety evaluators) both gate the report.

---

## MCP — Model Context Protocol

### MCP Client — consume external MCP servers

```csharp
options.McpServers = [
    new MentorMcpServer {
        Name      = "filesystem",
        Command   = "npx",
        Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/data"]
    }
];
```

### MCP Server — expose actions as MCP tools

The MCP server is in this package (since 1.0.0-rc.15; up to rc.14 it came with `MentorAgent`, which no longer references ASP.NET Core so that MAUI builds for every target). `AddMentorAgentServer()` registers it. A host without the hub — a Blazor Server app — adds this package and registers it alone:

```csharp
options.McpServerEnabled = true;

// Only without AddMentorAgentServer(), after AddMentorAgent():
builder.Services.AddMentorAgentMcp();
```

```csharp
app.MapMentorAgentMcp();                                          // path: options.McpServerPath (default /mcp)
app.MapMentorAgentMcp("/my-mcp");                                 // a path passed here wins over options.McpServerPath
app.MapMentorAgentMcp(configure: e => e.RequireAuthorization());  // protect the endpoint
```

Every `[MentorAction]` method becomes an MCP tool **except the gated ones**. An MCP request has no signed-in user, so an action with `RequiredRoles` is withheld unless you tell MentorAgent who the caller is. An action that needs a confirmation (`RequiresConfirmation` or `RequiresApproval`) is withheld **always**, because nobody is on the far end of an MCP call to answer a dialog — from 1.0.0-rc.15 that includes every `[MentorAction]` declaring neither `ReadOnly = true` nor `RequiresConfirmation` (`RequireConfirmationForWrites`); declare `RequiresConfirmation = false` on a write that must stay published. A startup warning lists the withheld actions. An action declared `ReadOnly = true` (or through `IsReadOnlyAction`) is published with `readOnlyHint`, and some MCP clients skip their own confirmation for such a tool: declare it only on actions that really only read. Until you protect it, the endpoint is open. It carries no CORS policy (MCP clients are not browsers); a browser-based client needs the opt-in described in the MentorAgent README (MCP Server, Step 2).

```csharp
builder.Services.AddHttpContextAccessor();
builder.Services.AddMentorAgent(options => options.McpServerEnabled = true);

// Publishes a role-gated action only to an authenticated caller that holds the role (IsInRole: a
// "roles" claim works once the identity's RoleClaimType names it — with JwtBearer and
// MapInboundClaims = false, set TokenValidationParameters.RoleClaimType = "roles"). Asked once per
// MCP request; never cached per caller (BUG-103).
builder.Services.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
    (options, http) => options.McpCallerPrincipal = () => http.HttpContext?.User);
```

Every `tools/call` passes through `OnToolResult`, counts in the dashboard's top actions and against `RateLimitPerUser` (an allowance separate from chat messages, per `NameIdentifier`, `sub` or `oid`; a caller with none is counted by remote address — which `AddHttpContextAccessor()` above makes available — or else shares one allowance with every such caller), and writes one Information line with a fingerprint of the caller (BUG-107).

Connect Claude Desktop, VS Code Copilot, or any MCP client to `/mcp`.

---

## A2A — Agent-to-Agent

### A2A Consumer — call remote A2A agents

```csharp
options.RemoteAgents = [
    new MentorRemoteAgent {
        Name         = "InventoryAgent",
        Description  = "Manages warehouse and inventory",
        AgentCardUrl = "https://inventory.example.com",
        Headers      = new Dictionary<string, string> {
            ["Authorization"] = $"Bearer {apiKey}"
        }
    }
];
```

### A2A Server — expose as a federatable agent

The A2A agent is in this package too (since 1.0.0-rc.15, like the MCP server above). `AddMentorAgentServer()` registers it; a host without the hub calls `builder.Services.AddMentorAgentA2A()` after `AddMentorAgent()`.

```csharp
options.A2AServerEnabled = true;
options.A2AServerPath    = "/a2a";                          // where MapMentorAgentA2A() maps the task handler (default "/a2a")
options.A2AServerUrl     = "https://myapp.example.com/a2a"; // the FULL public URL, path included: the card publishes it verbatim
```

```csharp
app.MapMentorAgentA2A();                                          // POST {A2AServerPath} + GET /.well-known/agent-card.json
app.MapMentorAgentA2A("/agent");                                  // a path passed here wins over A2AServerPath; A2AServerUrl must then end in /agent
app.MapMentorAgentA2A(configure: e => e.RequireAuthorization());  // protects the task handler AND the agent card
```

The agent card is always served at `/.well-known/agent-card.json`, whatever the path. With `A2AServerUrl` left `null` the card publishes the mapped path alone (e.g. `/a2a`), which only a caller on the same host can resolve — set it for any remote peer.

What a peer gets — the caller is a program, not a person:

- **A card it can follow.** It advertises the `JSONRPC` binding at `A2AServerUrl`, which is why that must be the endpoint's full URL. It also lists your **ungated** `[MentorAction]`s as skills (actions with `RequiredRoles`, a confirmation or `RequiresApproval` are left out), the input/output modes and streaming.
- **An answer from tools, or a refusal.** A served turn is told that no human is present: call the tool first, state only what a tool returned in this turn, and ask no follow-up questions. If it still ends without calling a tool that returns data (a navigation, a UI action, a memory write and a skill do not count), the reply is thrown away and the same request runs once more with a tool call **required**. If that second attempt returns no data either and the classifier model decides the reply states something about the application's records — a figure, or that an entry does not exist — the peer receives `NOT GROUNDED: …` instead. A policy read from a skill is returned as it is. There are never more than two attempts.
- **Its name for you, as data.** A MentorAgent caller sends the name it uses for this host as message metadata (`mentoragent.addressedAs`), so a request like "how many products does InventoryAgent have?" is understood to be about this host. Only a single token of letters, digits and `- _ .` (64 characters at most) is accepted.
- **No onward delegation.** A turn that arrives over A2A is not offered this host's own `RemoteAgents` (they are left out of its handoff workflow, its prompt and the `route_to_specialist` description). Your local specialists, declarative agents, teams and tools still serve it. Two hosts can therefore be each other's remote agent without looping, but **chaining A → B → C through a MentorAgent host is not supported in 1.0**. Point A at C directly.
- **A failed task, never an empty one.** A turn that produced no reply is reported to the caller as **failed** ("this agent could not produce a reply to the request"), and a warning is logged on this host.
- **Any host kind can serve.** Headless servers and Blazor Server hosts both answer A2A tasks.

On the consuming side, when this host has `RemoteAgents`, every `route_to_specialist` result opens with its source: a local specialist's answer says no remote agent was contacted, a remote answer is marked as that system's data, and a remote agent that was named but never took part produces `NOT <NAME>'S ANSWER` plus a warning — so the assistant cannot present a local answer as the remote one.

---

## Security

```csharp
// AI-based safety check (detects prompt injection and jailbreaks)
options.EnableSafetyCheck = true;   // one classifier call per message: ~0.7–2 s on gpt-4.1 — point ClassifierChatClient at a small model to cut it

// Per-user rate limiting
options.RateLimitPerUser = 20;      // requires authentication for true per-user isolation

// Role-based actions — the tool name is the method name in snake_case: delete_record
// The caller must be authenticated and hold ANY ONE of the listed roles (not all of them).
[MentorAction(Description = "Deletes a record", RequiredRoles = ["Admin"])]
public Task DeleteRecordAsync(string id) => ...;

// Confirmation dialogs for destructive actions — tool name: cancel_order
[MentorAction(Description = "Cancels an order", RequiresConfirmation = true)]
public Task CancelOrderAsync(string id) => ...;
```

### Authentication — per-user memory, rate limiting, and roles

`AddMentorAgentServer()` **gives the MentorAgent core the caller of each request**: the hub connection's user (`HubCallerContext.User`) for hub clients, `HttpContext.User` for `/mentor/chat` and for A2A tasks. MentorAgent reads it before the host's `AuthenticationStateProvider`, which it asks only inside a Blazor circuit. That makes **per-user memory, per-user rate limiting and `RequiredRoles` work for any client** — React, Vue, Angular, WASM, MAUI — and in a Blazor Web App too, whatever the order of `AddRazorComponents()` and `AddMentorAgentServer()`. Memory and the rate limit identify the user by its `NameIdentifier` claim, else `sub`, else `oid`, else its name. A key from any claim but `NameIdentifier` carries the claim's name (`sub:…`, `name:…`), so a name never matches another user's `NameIdentifier`.

**A provider of your own is not asked on the hub, SSE or A2A.** To add claims or roles to the user, use an `IClaimsTransformation`: ASP.NET Core applies it to `HttpContext.User`, and so to the hub connection's user. Your own code that resolves `AuthenticationStateProvider` outside a circuit still gets Blazor's provider, which throws there: that is Blazor's behaviour, not MentorAgent's.

Configure any ASP.NET Core authentication scheme on the server:

```csharp
// Web API with JWT Bearer
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => { /* configure your JWT issuer */ });
builder.Services.AddAuthorization();

builder.Services.AddMentorAgent(options => { options.RateLimitPerUser = 20; });
builder.Services.AddMentorAgentServer();   // registers the identity bridge

var app = builder.Build();
app.UseCors("MentorAgentClients");   // only for clients on another origin, see CORS
app.UseAuthentication();
app.UseAuthorization();
app.MapMentorAgentServer(configureTransports: e => e.RequireAuthorization());
```

Call them in this order: CORS, then authentication, then authorization, then the endpoints (see
[CORS](#cors--required-for-cross-origin-clients) for what goes wrong otherwise).

The client must authenticate its connection — e.g. pass the access token to the SignalR hub:

```javascript
// React / Angular / Vue / plain JS — you build the connection, so you set it directly
const connection = new signalR.HubConnectionBuilder()
    .withUrl('/mentor-hub', { accessTokenFactory: () => myAccessToken })
    .withAutomaticReconnect()
    .build();
```

```csharp
// Blazor WASM (Novior.MentorAgent.Blazor) — the package builds the connection, so you supply the token
// through the options. Same mechanism, same query-string parameter on the wire.
builder.Services.AddMentorAgentBlazor(options =>
{
    options.HubUrl = "https://api.example.com/mentor-hub";
    options.AccessTokenProvider = () => Task.FromResult(tokenStore.Token);
});
```

With an absolute `HubUrl` like this one, the WASM client also sends confirmations and Stop (`POST /mentor/approve`, `POST /mentor/cancel`) to the **hub's origin** with `Authorization: Bearer <token>` from the same `AccessTokenProvider`, so a signed-in user's answers pass the owner check. With a relative `HubUrl` (client served by the same host) they go to the host `HttpClient`'s `BaseAddress`; either way the WASM app must register an `HttpClient`: the standalone WebAssembly template registers one, the client project of a Blazor Web App does not.

> **A WebSocket handshake cannot send an `Authorization` header**, so SignalR appends the token as
> `?access_token=…`. With JWT bearer on the server, forward it in `OnMessageReceived` or the hub
> sees an anonymous caller and every gated action fails closed — correctly, and indistinguishably
> from a bug:
>
> ```csharp
> options.Events = new JwtBearerEvents
> {
>     OnMessageReceived = ctx =>
>     {
>         var token = ctx.Request.Query["access_token"];
>         if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/mentor-hub"))
>             ctx.Token = token;
>         return Task.CompletedTask;
>     }
> };
> ```

> Without authentication configured, a caller is keyed by `AnonymousIdentity`. By default (`PerSession`), each hub connection and each SSE request gets its own memory and its own rate-limit counter. `Shared` puts them all on the single key `"anonymous"` (shared memory, one global rate limit). Either way, `RequiredRoles` actions **fail closed** (blocked). MentorAgent never replaces a Blazor host's `AuthenticationStateProvider`: it reads the request's caller first, as described above.

---

## Attribute reference

### `[MentorAction]` parameters

| Parameter | Description |
|---|---|
| `Description` | Natural language description used as the AI tool description |
| `Category` | Published only as the skill's tag on the A2A agent card. Not used for any grouping |
| `RequiresConfirmation` | Shows a confirmation banner before executing. Use for destructive or irreversible operations |
| `RequiredRoles` | ASP.NET Core identity roles: the caller needs **any one** of them (not all), must be authenticated. On the hub, `/mentor/chat` and A2A the user is the request's caller; in a Blazor circuit, the one the host's `AuthenticationStateProvider` reports. Empty = no role check |
| `ProactiveHint` | Appended to the tool's description as `Guidance: <hint>` for the assistant's own tools (coordinator, specialists, team members) — not on the MCP server surface, and not used by semantic tool filtering |
| `NavigateTo` | URL the AI navigates to automatically after successful execution |

### `[MentorAgent]` parameters

| Parameter | Required | Description |
|---|---|---|
| `Name` | ✅ | Agent name — key in the Handoff graph and in the coordinator's system prompt |
| `Description` | ✅ | Capabilities description used by the coordinator to decide when to delegate |
| `HandoffTo` | — | Names of other `[MentorAgent]` this agent can hand off to (case-insensitive match) |
| `Instructions` | — | Custom system prompt, used exactly as written. Auto-generated from Name + Description when omitted — the generated prompt includes a grounding rule against stating facts no tool returned; a custom prompt replaces it, so include your own |

### `[MentorTeam]` parameters

| Parameter | Required | Description |
|---|---|---|
| `Name` | ✅ | Team name |
| `Description` | ✅ | Description used by coordinator to decide when to activate |
| `TriggerOn` | — | Keywords that hint activation (not hard rules — the AI decides) |
| `MaxIterations` | — | Max turns before forced termination. Default: `10` |
| `HandoffTo` | — | L2 agents to delegate execution to after team approves |

### `[TeamMember]` parameters

| Parameter | Required | Description |
|---|---|---|
| `Role` | ✅ | Role name within the team (e.g. `"DataAnalyst"`) |
| `Instructions` | ✅ | System prompt for this member |
| `Tools` | — | Read-only tool classes this member can call during discussion. They are the member's only: not also the coordinator's actions, nor on `/mcp` or the A2A card (a `[MentorSkill]` class keeps its coordinator tools). Discovered like any action: public instance methods declared on the class, with `[MentorAction]` or a non-empty `[Description]` |

### `[MentorPage]` parameters

| Parameter | Required | Description |
|---|---|---|
| `Url` | ✅ | Page URL (e.g. `"/orders"`) |
| `Name` | ✅ | Human-readable page name injected into the system prompt |
| `Description` | — | Optional feature description shown to the AI |
| `HasUIActions` | — | If `true`, `navigate_to` waits for `PageContext.SignalReady()` — only inside an interactive Blazor Server circuit; hub, SSE and A2A turns never wait (see [Page navigation](#page-navigation)). Default: `false` |
| `ReadyTimeout` | — | Timeout in ms for `SignalReady()` (Blazor Server circuits only). Default: `2000` |

### `[MentorSkill]` parameters

| Parameter | Description |
|---|---|
| `Name` | Unique skill name in kebab-case (e.g. `"expense-report"`) |
| `Description` | One-sentence description shown in the skill catalogue |
| `InstructionsFile` | Path to a markdown file (relative to content root or absolute) |
| `Instructions` | Inline markdown. Takes precedence over `InstructionsFile` |

---

## Persistent conversation history

By default, conversation history lives in memory and is lost on app restart — and every hub connection gets its own `InMemoryChatHistoryProvider`, built inside its coordinator.

> ⚠️ **`MentorOptions` is a singleton.** An object assigned to `ChatHistoryProvider` serves **every connection and every user** in the process, so a provider that keeps one list replays one user's conversation to all the others (BUG-055). For a multi-user server use **`ChatHistoryProviderFactory`**: it is invoked once per coordinator (once per hub connection) and receives that scope's `IServiceProvider`, so the provider can partition its storage by the caller:
>
> ```csharp
> // ✅ isolated in-memory history, one per connection
> options.ChatHistoryProviderFactory = _ => new InMemoryChatHistoryProvider();
>
> // ✅ shared backing store, partitioned by the caller
> options.ChatHistoryProviderFactory = sp =>
>     new CosmosChatHistoryProvider(cosmosClient, "my-db", "conversations",
>         partitionKey: sp.GetRequiredService<IUserContext>().UserId);
> ```
>
> Keep the instance property only for a single-user host, or a provider that partitions its own storage internally. Setting both stops the host when it starts.
>
> **Keep the transcript in the session, not in a field.** The provider is built once per coordinator and kept for the whole hub connection — across **new conversations** (the hub's `ResetSession`, the widget's reset button) and across sessions restored through `/mentor/session`. A provider that stores messages in a field carries the previous conversation into the one the user has just started (BUG-102). Store them in the session's `StateBag`, as `InMemoryChatHistoryProvider` does; the Agent Framework's `ProviderSessionState<T>` does the bookkeeping:
>
> ```csharp
> public sealed class MyHistoryProvider : ChatHistoryProvider
> {
>     private readonly ProviderSessionState<List<ChatMessage>> _transcript =
>         new(_ => [], nameof(MyHistoryProvider), AIJsonUtilities.DefaultOptions);
>
>     protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
>         InvokingContext context, CancellationToken ct = default)
>         => ValueTask.FromResult<IEnumerable<ChatMessage>>([.. _transcript.GetOrInitializeState(context.Session)]);
>
>     protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken ct = default)
>     {
>         var history = _transcript.GetOrInitializeState(context.Session);
>         history.AddRange(context.RequestMessages);
>         history.AddRange(context.ResponseMessages ?? []);
>         _transcript.SaveState(context.Session, history);
>         return ValueTask.CompletedTask;
>     }
> }
> ```
>
> A provider backed by an external store does the same with the key of its rows: keep it in the session (a conversation id in the `StateBag`), not in the provider.

Instance form (single-user hosts, or a provider that partitions internally):

```csharp
// CosmosDB
options.ChatHistoryProvider = new CosmosChatHistoryProvider(cosmosClient, "my-db", "conversations");

// Custom (implement ChatHistoryProvider from Microsoft Agent Framework)
options.ChatHistoryProvider = new MyRedisChatHistoryProvider(redisConnection);
```

The connection a provider like this holds is shared; the rows it reads and writes are keyed by the session, as above — never by a field of the provider.

---

## Session serialize and restore

**Remote clients: use the session endpoints.** The conversation lives in the DI scope of the client's hub connection. A controller or minimal API resolves the *request's* scope — a different, empty orchestrator — so an `IMentorOrchestrator` injected there always serializes nothing. `MapMentorAgentServer()` maps the transport that reaches the right scope:

| Endpoint | Answers |
|---|---|
| `GET /mentor/session?connectionId=<id>` | `200` + snapshot · `204` no conversation yet (do not store it over a good snapshot) · `404` unknown connection |
| `POST /mentor/session?connectionId=<id>` (body: the snapshot) | `200` restored · `400` not a snapshot this server issued for this user, edited, or unusable by this host (live conversation untouched) · `415` content type not JSON (a body that does not parse is a `400`) · `404` unknown connection |

Both are owner-checked like `/mentor/approve` (`401` / `403` for a caller that is not the connection's principal, before the body is read): send the same credentials the hub connection used.

**The snapshot is opaque (BUG-110).** Until rc.13 it was the conversation in clear: a client could read it, edit it — an edited transcript restored as history the model trusts — and restore another user's snapshot into its own connection. Since rc.14 it is protected with ASP.NET Core Data Protection under a purpose that names the connection's user: store it verbatim, as text. It restores only for that user, on this application's key ring. On a host without authentication every connection's user is the same anonymous one, so there a snapshot restores into any connection of the application: it still cannot be read or edited, but binding it to a person needs signed-in users. Snapshots saved before rc.14 are refused (`400`); save the conversation again. A snapshot is also refused when this host keeps its history another way (on the service vs locally, or under another history provider) — it would fail every turn after it, or restore empty.

> **Key ring.** To restore after a restart or on another instance, persist and share the Data Protection keys, as you already do for authentication cookies: `builder.Services.AddDataProtection().PersistKeysToFileSystem(...)` (or Azure Blob Storage / Key Vault, Redis). Windows and Azure App Service persist them by default; a container without a volume does not. The client-side save/restore example is under [What Novior.MentorAgent.Server exposes](#what-noviormentoragentserver-exposes).

> A `404` straight after connecting can mean the request overtook the hub's `OnConnectedAsync`: restore after the first hub call, or retry once.

**In-process code** that already runs inside the conversation's scope (a hub filter, a Blazor Server component) can call the orchestrator directly — `IMentorOrchestrator` is registered by `AddMentorAgent()`. The API, shown on a controller for brevity (a controller only sees the conversation if it shares that scope — see below):

```csharp
// Inject in a service or controller. ISnapshotStore is yours: Redis, a database table...
public class SessionController(IMentorOrchestrator mentor, ISnapshotStore store) : ControllerBase
{
    [HttpPost("session/save")]
    public async Task<IActionResult> Save()
    {
        // This snapshot is the conversation IN CLEAR: keep it on the server, keyed by the signed-in
        // user. Never hand it to the client — it could read it, edit it (history the model trusts),
        // or send back someone else's: what /mentor/session protects against.
        // null means "nothing to save yet" — do not store it over a good snapshot.
        JsonElement? snapshot = await mentor.SerializeSessionAsync();
        if (snapshot is null) return NoContent();
        await store.SaveAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, snapshot.Value);
        return Ok();
    }

    [HttpPost("session/restore")]
    public async Task<IActionResult> Restore()
    {
        // Restore on reconnect (e.g. after server restart). A snapshot that is not ours — a stale
        // entry from an older deployment — is refused here, and the live conversation is untouched.
        if (await store.LoadAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!) is not { } snapshot)
            return NoContent();
        try { await mentor.RestoreSessionAsync(snapshot); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        return Ok();
    }
}
```

Remember that the orchestrator is **scoped**: on Blazor Server it belongs to the circuit, so a controller resolves a *different* scope from the one the widget is using. Save and restore from the same scope that holds the conversation — a hub method, or a component — or the snapshot will be of an empty session. The full lifetime table — which service is per-circuit and which is a singleton — is in the [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) README, under *Blazor Server vs Blazor WASM*.

> Over the hub the scope is the **connection**, so a reconnect is a new scope and therefore a new
> conversation. That is deliberate — it is what keeps two browsers apart — and it is exactly what
> `SerializeSessionAsync` / `RestoreSessionAsync` (over HTTP: `GET`/`POST /mentor/session`) exist to bridge. Snapshot
> while the connection is alive — after each completed turn — because on disconnect the server disposes the
> connection's scope and the old id answers `404`. Restore on the new connection id after
> `withAutomaticReconnect()` brings the connection back, or the user silently starts over.

> The lower-level `IMentorSessionManager` is registered too, but every method on it takes the coordinator `AIAgent`, which is deliberately not handed out to application code — running it directly would bypass the guardrails, the rate limiter, the role gate and the confirmation gate. Use the orchestrator.

The widget's **reset** call (`ResetSession` hub method) starts a new `AgentSession` on the same connection. The history provider is kept, so a provider that stores the transcript in a field — rather than in the session — carries the old conversation into the new one (see [Persistent conversation history](#persistent-conversation-history)).

---

## All configuration options

The `AddMentorAgent` callback runs twice: once on a snapshot, to decide what to register, then on the real
options. `EnableObservability`, `ObservabilitySourceName`, `McpServerEnabled`, `A2AServerEnabled`, and
`AppName` as the MCP server's name are read from that snapshot, so set them in the callback: a
`Configure<MentorOptions>` registered after `AddMentorAgent` does not reach them. The A2A agent card is built
from the real options, when `MapMentorAgentA2A()` runs.
The options are checked when the host starts: an invalid configuration stops it, with the reason, before the
first connection.

### Core

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `AppName` | `string` | *(required)* | Application name for the system prompt |
| `AppDescription` | `string` | `""` | Domain description for richer AI context |
| `ChatClient` | `IChatClient?` | `null` | AI provider (recommended) |
| `Agent` | `AIAgent?` | `null` | Pre-built AI agent (alternative) |
| `EmbeddingGenerator` | `IEmbeddingGenerator<string, Embedding<float>>?` | `null` | Optional embedding model — enables semantic tool filtering |
| `ScanAssemblies` | `Assembly[]` | *(required)* | Assemblies to scan for agents, actions, pages |
| `Language` | `MentorLanguage` | `English` | Language for AI responses |
| `MentorshipLevel` | `MentorshipLevel` | `Standard` | AI proactivity: Minimal / Standard / Proactive |
| `EnableSuggestions` | `bool` | `true` | Shows the starter suggestion chips in the **Blazor widget's** welcome panel. Nothing is generated or sent to headless clients — no hub event or SSE frame carries suggestions — so it has no effect on a React/Angular/Vue/MAUI client |
| `EnableActionFeedback` | `bool` | `true` | Emit `ActionExecuting` / `ActionCompleted` (hub events) while one of your function tools runs; `false` removes those. Hosted-tool lines such as "Searching the web…" are controlled separately by `ShowHostedToolActivity` |
| `EnableVoiceInput` | `bool` | `false` | Declares that voice input is expected. A headless client implements capture itself (Web Speech API, MAUI speech-to-text) and sends the transcript as an ordinary message |
| `EnableVoiceOutput` | `bool` | `false` | Declares that replies are meant to be spoken. Synthesis happens on the client — the server streams the same text either way |
| `EnableOnboardingTour` | `bool` | `false` | Tells the Blazor widget to show the [onboarding tour](#onboarding-tour). `GET /mentor/tour` is mapped by `MapMentorAgentServer()` **regardless of this flag** — a headless client that fetches it gets the steps (generated once per process, one model call on the first request; `[]` when no `[MentorPage]` exists), so decide in the client whether to show them |
| `EnableGenerativeCards` | `bool` | `true` | Forward `MentorCard`s returned by a tool to clients as [cards](#generative-ui--cards-level-2). `false` sends the data to the model as text instead |

> **Not listed here on purpose.** `MentorOptions` also carries `Theme`, `Position`, `PrimaryColor`,
> `BotName`, `WelcomeMessage`, `InputPlaceholder`, `AvatarUrl` and the playback options
> `VoiceStreaming`, `VoiceBargeIn`, `VoiceHandsFree`, `VoiceRate`. They style or drive the **Blazor
> widget** and have no effect on a headless backend — your React/Angular/Vue/MAUI client renders its
> own UI and, for the voice ones, does its own Web Speech API work. Set them only if you also serve
> `<ChatWidget />` from a Blazor project; for `Novior.MentorAgent.Blazor` clients they live in
> `MentorAgentBlazorOptions` on the client side instead.

### Multimodal image input

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `EnableImageInput` | `bool` | `false` | Accept image attachments on `SendMessage` / `POST /mentor/chat` (see [Multimodal image input](#multimodal-image-input)). Requires a vision-capable `ChatClient` |
| `MaxImageBytes` | `int` | `4194304` | Max decoded size per inline image (4 MB) |
| `MaxImagesPerMessage` | `int` | `4` | Max images accepted per user turn |
| `AllowedImageTypes` | `IReadOnlyList<string>` | `png, jpeg, gif, webp` | MIME **allow-list**; anything else is dropped with a warning |

### Hosted tools

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `HostedTools` | `MentorHostedTools` | `None` | Provider-hosted tools: `WebSearch`, `CodeInterpreter`, `FileSearch`, `ImageGeneration`, `HostedMcp` (flags) — see [Hosted tools](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp) |
| `FileSearchVectorStoreIds` | `IReadOnlyList<string>?` | `null` | Vector stores searched by `FileSearch`. **Required** when it is on — without ids the tool is skipped (fail-closed) |
| `FileSearchMaxResults` | `int?` | `null` | Upper bound on file-search matches |
| `HostedImageModel` | `string?` | `null` | Model used by `ImageGeneration` (e.g. `gpt-image-1-mini`), read by providers that take it from the tool payload (OpenAI). **On Azure OpenAI it is not enough on its own**: the image deployment must also travel as the `x-ms-oai-image-generation-deployment` request header — add it as a pipeline policy where you build the `AzureOpenAIClient`, or every image turn fails with *"imagegen deployment must be provided through header"* |
| `HostedImageSize` | `string?` | `null` | Generated image size as `WIDTHxHEIGHT` (e.g. `"1024x1024"`). **The cost knob** — a larger image is billed more. An unparsable value is ignored with a warning |
| `HostedMcpServers` | `IReadOnlyList<MentorHostedMcpServer>?` | `null` | Remote MCP servers the **provider** connects to, used by `HostedMcp`. **Required** when it is on (fail-closed). Per server: `Name`, `Url`, `Description`, `AllowedTools`, `RequireApproval` (default `true`), `AlwaysRequireApprovalTools` / `NeverRequireApprovalTools`, `Headers` |
| `ShowHostedToolsStatus` | `bool` | `false` | Amber badge in the Blazor widget header listing the active hosted tools |
| `FilterHostedTools` | `bool` | `true` | Score each hosted tool against the user's message and declare it only when relevant, instead of on every turn. Needs `EnableToolFiltering` + `EmbeddingGenerator`; without them nothing is filtered and a warning is logged (**fail-open**) |
| `HostedToolFilterMinScore` | `float?` | `null` → **0.15** | Threshold for hosted tools only — deliberately **lower** than `ToolFilterMinScore`: their scores run on a different scale, and this is a coarse pre-cut now that `HostedToolDomainCheck` decides. Raising it makes the verdict flip between rewordings of the same request. Scores logged at Debug |
| `HostedToolDomainCheck` | `bool` | `false` | Ask a small model whether the message concerns this application before a hosted tool runs. Different question from `FilterHostedTools`: *"draw me a dog"* is a genuine image request and nonsense for a shop backend. Once per turn, only when a hosted tool already passed relevance, so ordinary turns cost nothing. Fails open. When it withholds a tool the model is told so **in that same request** (via `ChatOptions.Instructions`, which is per-request and never stored in service-managed history) and instructed to refuse plainly — without it, a model whose prompt still advertises the capability fills the gap by inventing a result |
| `HostedToolDomainScope` | `string?` | `null` | The scope the classifier judges against; `null` derives it from `AppName` + `AppDescription` + page names, plus the enabled hosted capabilities and the rule that they do not widen the subject |
| `HostedToolDomainClassifier` | `Func<string, CancellationToken, Task<bool>>?` | `null` | Replaces the model call with your own decision (`true` = in scope) — an existing intent service, per-user policy, or to make the check free |
| `MaxHostedToolCallsPerSession` | `int` | `0` | Hard cap per hub connection / SSE session; beyond it hosted tools stop being declared. `0` = no cap. Scoring lowers the frequency, only a counter bounds the worst case. ⚠️ The counter lives in the orchestrator scope, so over **SignalR** it spans the whole connection, while a **`POST /mentor/chat`** call is its own session — a stateless HTTP client gets a per-request cap, not a per-user one |
| `ShowHostedToolActivity` | `bool` | `true` | Emit live hosted-tool activity to clients: `ActionExecuting` / `ActionCompleted`, `RagSourcesReady` for the pages a web search used **and the documents a file search matched**, `GeneratedImages` for generated images |

### Token & cost optimization

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `EnableToolFiltering` | `bool` | `false` | Send only the tools semantically relevant to the message. **Requires `EmbeddingGenerator`**; without it, all tools are sent |
| `ToolFilterMaxTools` | `int` | `12` | Max matched business tools (core tools always kept) |
| `ToolFilterMinTools` | `int` | `0` | Once one action clears `ToolFilterMinScore`, at least this many best-ranked actions are sent, whatever their score (up to `ToolFilterMaxTools`). `0` = the threshold alone. A trade-off, not a fix: it can hand the model a read action it needed, or a wrong one it picks instead |
| `ToolFilterMinScore` | `float` | `0.35` | Minimum cosine similarity (0–1) for a tool to be relevant |
| `EnableCompaction` | `bool` | `false` | Compact long conversation history before each call — what is sent, never what is stored (BUG-104). Locally kept history only (Path A); not installed with `UseServiceManagedHistory` |
| `CompactionTokenThreshold` | `int` | `4000` | Token budget that triggers compaction |
| `CompactionMaxTurns` | `int` | `8` | Recent turns kept intact |

> `EmbeddingGenerator` also powers **semantic memory** (`MemoryRelevanceFiltering`, see Memory). RAG relevance is handled by the vector search + `RagMinScore` (see RAG) — no keyword gating.

> Also: `AddMentorAgentServer()` builds the coordinator **once per SignalR connection** (not per message), so external MCP servers are connected once and the conversation session persists across messages.

### Middleware, observability & dashboard

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `InputGuardrail` | `Func<string,CancellationToken,Task<bool>>?` | `null` | Custom input guardrail (`true` = safe); replaces the built-in check |
| `OutputGuardrail` | `Func<string,CancellationToken,Task<bool>>?` | `null` | Moderate the completed reply (`true` = safe); buffers the reply then reveals it (no live streaming that turn) |
| `OnToolResult` | `Func<string,object?,object?>?` | `null` | Transform/redact a tool result before it returns to the model. A hook that throws withholds the result: the model is told the action ran and not to run it again |
| `OnException` | `Func<Exception,string?>?` | `null` | Map an exception to a user-facing message (`null` → default). For a tool that throws, the chat never shows the exception's own text while the hook is set: `null` shows the localized "The action could not be completed." The model gets the hook's message, else the exception's, and may repeat it in its reply: return a message for any exception whose text must not reach the user |
| `ConfigureChatClientPipeline` | `Func<ChatClientBuilder,ChatClientBuilder>?` | `null` | Insert custom middleware into the Path A pipeline |
| `EnableObservability` | `bool` | `false` | Emit OpenTelemetry traces/metrics + MentorAgent spans/counters |
| `ObservabilityIncludeSensitiveData` | `bool` | `false` | Include prompt/response content — **Development only** |
| `ObservabilitySourceName` | `string` | `"MentorAgent"` | ActivitySource/Meter name to `.AddSource()/.AddMeter()` |
| `ModelPricing` | `IReadOnlyDictionary<string,ModelPrice>?` | `null` | Per-model prices for the dashboard cost estimate (none built in) |
| `DashboardRole` | `string` | `"Admin"` | Role required for `GET /mentor/admin/metrics` (`""` = open, dev only) |
| `MetricsPersistenceInterval` | `TimeSpan` | `30s` | How often a registered `IMentorMetricsStore` is flushed; also flushes on graceful shutdown. Without a store the snapshot is in-RAM and resets on restart |
| `MetricsRetention` | `TimeSpan` | `7d` | How much of the hourly time series the snapshot carries — the window the dashboard charts can show |
| `StrongChatClient` | `IChatClient?` | `null` | Strong model to escalate to (`ChatClient` is the cheap default). Routing active only when set |
| `RoutingStrategy` | `MentorRoutingStrategy` | `Custom` | `Semantic` / `Classifier` / `Cascade` / `Custom` — how the cheap↔strong decision is made |
| `UseStrongModelAsync` | `Func<IReadOnlyList<ChatMessage>,CancellationToken,Task<bool>>?` | `null` | `Custom`: async, context-aware router (precedence over `UseStrongModel`) |
| `UseStrongModel` | `Func<string,bool>?` | `null` | `Custom`: legacy sync predicate on the latest user message |
| `RoutingComplexExemplars` | `IReadOnlyList<string>?` | `null` | `Semantic`: example "complex" turns (null → built-in set) |
| `RoutingThreshold` | `float` | `0.35` | `Semantic`: cosine floor to escalate |
| `RoutingClassifierClient` | `IChatClient?` | `null` | `Classifier`/`Cascade`: dedicated judge client (defaults to cheap `ChatClient`) |
| `ClassifierChatClient` | `IChatClient?` | `null` | Client for MentorAgent's own one-word decisions (input and output safety, hosted-tool scope, the request check of `RequireLookupForRecordQuestions` and `RequireActionForChangeRequests`, fact extraction). A small deployment here cuts the wait before every reply; defaults to `ChatClient` |

> Also available as **services** (resolve from DI): `IMentorStructured` — typed `GenerateAsync<T>`, see [Structured outputs](#structured-outputs) — `MentorEvaluator`, the token/quality regression harness, see [Evaluation & regression testing](#evaluation--regression-testing) — and `IMentorTour`, the [onboarding tour](#onboarding-tour) source, which you can replace with your own registration to script the tour by hand.

### Memory

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `UseMemoryContext` | `bool` | `false` | Enable automatic user memory |
| `AnonymousIdentity` | `MentorAnonymousIdentity` | `PerSession` | Who a signed-out caller is: `PerSession` = one key per hub connection and one per `/mentor/chat` request (no memory or rate-limit sharing between anonymous callers, and none carried from one SSE request to the next); `Shared` = the previous single `"anonymous"` key, for single-user hosts |
| `MemoryContextCount` | `int` | `10` | Max memories injected into the prompt on each call (last N, or the most relevant N with `MemoryRelevanceFiltering`) |
| `MemoryRelevanceFiltering` | `bool` | `false` | Inject only the memories **semantically** relevant to the current message (embedding cosine; identity/preference facts always kept) instead of the last N — saves tokens. Requires `EmbeddingGenerator`; without it, falls back to last-N |
| `MemoryAutoCapture` | `bool` | `true` | The reliable memory writer: a post-turn extraction saves durable user facts instead of relying on the model to call `remember`. On Path A (a `ChatClient` is set) it is the **only** writer — the redundant `remember` tool + prompt are dropped (saves tokens); on Path B it falls back to the `remember` tool. `forget_all` always kept. One small model call per user message; set `false` to opt out |

### Agent Skills

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `EnableSkills` | `bool` | `false` | Enable skill discovery and `load_skill` / `read_skill_resource` tools |
| `SkillsFolder` | `string` | `"Skills"` | Folder to scan for file-based skills (`SKILL.md`) |
| `SkillSources` | `IList<AgentSkillsSource>` | empty | Extra sources using the Agent Framework's own abstraction — a database, a remote catalogue, or Microsoft's `AgentFileSkillsSource`. Your own skills win a name collision |
| `SkillFilter` | `Func<AgentSkill, bool>?` | `null` | Keeps or drops a skill from `SkillSources`. Never applies to skills MentorAgent discovered itself |
| `SkillsRefreshInterval` | `TimeSpan?` | `null` | Set: the composed `SkillSources` (wrapped in the Agent Framework's `CachingAgentSkillsSource`) are held **once per process** and shared by every session; the list is re-fetched when a session is built after the interval has passed. `null`: every new session (circuit, hub connection, SSE request) re-reads the sources when its coordinator is built and keeps that list for its lifetime — nothing is cached for the process. Worth setting for a remote or expensive source |

### RAG

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `UseRag` | `bool` | `false` | Enable RAG. Requires a registered `IMentorRagSource` |
| `RagResultCount` | `int` | `5` | Number of documents retrieved per query |
| `RagMinScore` | `float` | `2` | Minimum relevance score. `0` = no filtering. For keyword search: `2` ≈ two content matches. For vector/cosine similarity: use `0.5`–`0.75`. Not set: the source's `DefaultMinScore` when it declares one |
| `RagSystemPromptTemplate` | `string` | `"Use the following documents to answer:\n{documents}"` | Prompt template. `{documents}` is the placeholder |
| `ShowRagSources` | `bool` | `false` | Show citation chips in widget |

> RAG is fully semantic: vector search + `RagMinScore` inject nothing on pure commands, so no keyword gating is needed.

### MCP

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `McpServers` | `MentorMcpServer[]?` | `null` | External MCP servers as L1 tools |
| `MentorMcpServer.Shared` | `bool?` | `null` | One connection per process (`true`) or per session (`false`). Unset: URL servers shared, command servers per session |
| `McpServerEnabled` | `bool` | `false` | Expose as MCP server. Also call `app.MapMentorAgentMcp()`; `AddMentorAgentServer()` registers the server (without it: `AddMentorAgentMcp()`) |
| `McpServerPath` | `string` | `"/mcp"` | Path `app.MapMentorAgentMcp()` maps when called without a path; a path passed to the method wins |
| `ShowMcpStatus` | `bool` | `false` | Show MCP status badge in widget |

### A2A

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `RemoteAgents` | `MentorRemoteAgent[]?` | `null` | Remote A2A agents in the Handoff workflow |
| `A2AServerEnabled` | `bool` | `false` | Expose as A2A agent. Also call `app.MapMentorAgentA2A()`; `AddMentorAgentServer()` registers the agent (without it: `AddMentorAgentA2A()`) |
| `A2AServerPath` | `string` | `"/a2a"` | Path `app.MapMentorAgentA2A()` maps the task handler at when called without a path; a path passed to the method wins. The card stays at `/.well-known/agent-card.json` |
| `A2AServerUrl` | `string?` | `null` | Full public URL of this agent's A2A endpoint, path included (e.g. `https://myapp.example.com/a2a`) — published verbatim in the agent card. `null` = the card publishes the mapped path only (same-host callers). **Required** when used as remote by other agents |
| `AgentCard` | `AgentCardInfo?` | `null` | A2A Agent Card metadata |
| `ShowA2AStatus` | `bool` | `false` | Show A2A status badge in widget |

### Security & Limits

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `EnableSafetyCheck` | `bool` | `false` | AI-based prompt injection detection |
| `SafetyCheckTimeout` | `TimeSpan` | `00:00:15` | Time limit for the input/output safety checks (built-in or custom); `Zero` = no limit, Stop still cancels |
| `SafetyCheckFailure` | `MentorSafetyCheckFailure` | `Allow` | A check with no verdict (timeout, error): `Allow` = fail open with a warning, `Block` = refuse the message / withhold the reply |
| `RefuseOutOfScope` | `bool` | `false` | Refuse a message that plainly has nothing to do with the application instead of answering it; defaults to answering when unsure (BUG-105). With the safety check on it rides in the same classifier call (a longer prompt: the question and the scope); alone it costs one small call per turn, and a turn that is about to declare a hosted tool still asks the tool gate its own question. Counted in `GuardrailBlockCount` |
| `WarmUpAtStartup` | `bool` | `false` | Build one coordinator at start-up (catalogue embeddings, shared MCP sessions, agent cards) so the first connection does not pay for them. Runs once the host has started; a failed warm-up never fails the boot |
| `MaxMessageLength` | `int` | `4000` | Max message length (0 = unlimited) |
| `RateLimitPerUser` | `int` | `0` | Max messages per user within `RateLimitWindowSecs` (0 = disabled). Signed-out callers are counted per connection under the default `AnonymousIdentity = PerSession`. MCP `tools/call` has its own allowance with the same limit, per `NameIdentifier`, `sub` or `oid`; a caller with none is counted by remote address when `IHttpContextAccessor` is registered, otherwise all such callers share one |
| `RateLimitWindowSecs` | `int` | `60` | Rate limiting window in seconds |
| `RequireConfirmation` | `bool` | `true` | On/off for the confirmation dialogs of your actions, MCP-client servers and `RequiresApproval`. A hosted MCP server's approvals (`RequireApproval`, `AlwaysRequireApprovalTools`) still show the banner |
| `RequireConfirmationForWrites` | `bool` | `true` | A `[MentorAction]` declaring neither `ReadOnly = true` nor `RequiresConfirmation` asks for confirmation, so it is also withheld from `/mcp` and the A2A card. `[Description]`-only methods are not covered. `false` = the rc.14 behaviour |
| `HitlMode` | `MentorHitlMode` | `Blocking` | `Blocking` (MentorAgent's flow) or `Native` (AF `ApprovalRequiredAIFunction`). Same client protocol either way — see [HITL](#hitl--confirming-actions) |
| `RequiresApproval` | `Func<string, bool>?` | `null` | Forces approval for a tool **by name** — the way to gate MCP tools and skills you don't own |
| `AutoApprovalRules` | `IList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>` | empty | `Native` mode only. Approves a call before any banner appears, and enables "don't ask again". **Waives the prompt, never `RequiredRoles`** |
| `MaxAutoApprovalIterations` | `int?` | `null` | Cap on re-runs caused by auto-approval. Each one is a fresh billable model call and a per-request limit cannot bound it |
| `IncludeWorkflowExceptionDetails` | `bool` | `false` | Include stack traces in responses. **Never enable in production** |

### Session & History

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `MaxSessionMessages` | `int` | `50` | Max messages in session history, tool calls and results included; trimmed by whole turns, oldest first. Not read when `ChatReducer` is set |
| `ChatReducer` | `IChatReducer?` | `null` | Replaces the default history reducer (`MentorChatReducer`, which keeps tool calls with their results). `new MessageCountingChatReducer(50)` restores the behaviour up to rc.14. Not used with a provider you supply or with `UseServiceManagedHistory` |
| `ChatHistoryProvider` | `ChatHistoryProvider?` | `null` | One provider instance **shared by every connection and user** — single-user hosts only, or a provider that partitions its own storage |
| `ChatHistoryProviderFactory` | `Func<IServiceProvider, ChatHistoryProvider>?` | `null` | Builds a provider **once per coordinator** (per hub connection) — the isolation the default has. **Prefer this on any multi-user server.** Setting both stops the host when it starts |
| `UseServiceManagedHistory` | `bool` | `false` | Set when the client keeps the conversation on the **service** (Responses API, Foundry, Copilot Studio) — required to avoid AF's "Only ConversationId or ChatHistoryProvider" error. Disables `MaxSessionMessages`, `ChatReducer` and `EnableCompaction` |

---

> **Provider package versions.** With Azure OpenAI's **Responses** API, hold `OpenAI` at 2.10
> (`Microsoft.Extensions.AI.OpenAI` `[10.6.0]`): with 2.11 or later `GetResponsesClient()` throws
> `MissingMethodException` when called. Chat Completions needs no hold. The details are in the
> [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) README, under *Provider package versions*. Up to
> 1.0.0-rc.14 the core package stopped such builds with `error MENTOR001`; the check is gone.

## Requirements

- .NET 10.0+
- `Novior.MentorAgent` package (required dependency — installed automatically)
- An AI provider (Azure OpenAI, OpenAI, Ollama, etc.)

---

## Related Packages

| Package | Purpose |
|---------|---------|
| [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) | **Required** — AI orchestration engine |
| [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) | Blazor WASM client |
| [Novior.MentorAgent.Abstractions](https://www.nuget.org/packages/Novior.MentorAgent.Abstractions) | Shared foundation (transitive — no need to install) |
| [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) | **Optional** — define Level-2 specialist agents in YAML instead of C# |

---

## License

Apache License 2.0 — the full text is in the `LICENSE` file inside this package, with the copyright notice in `NOTICE`. Versions up to and including 1.0.0-rc.14 were released under the MIT License, which still applies to them.
