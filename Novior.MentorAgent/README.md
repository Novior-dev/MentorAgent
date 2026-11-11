# MentorAgent

MentorAgent is an open-source .NET library that puts AI agents inside your applications, under your rules: your C#
methods become tools, and on each one you declare which roles may use it and whether the user must confirm it first.
Use the ready-made chat widget in Blazor and MAUI, or connect any front end (React, Angular, Vue, mobile) over SignalR
or SSE; AI assistants and other agents reach the same tools over MCP and A2A.

**Website:** [mentoragent.net](https://mentoragent.net) · **Documentation:** [docs.mentoragent.net](https://docs.mentoragent.net)

> **New package IDs.** From 1.0.0 the packages, their namespaces and their assemblies are `Novior.MentorAgent.*`; up to 1.0.0-rc.15 they were `MentorAgent.*`. See [Migrating from the MentorAgent.* packages](#migrating-from-the-mentoragent-packages).

## Package Family

| Package | Install when |
|---------|-------------|
| **Novior.MentorAgent** ← you are here | Blazor Server app (the most common case), or a .NET MAUI Blazor Hybrid app |
| [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) | Web API, headless backend, or any client that is not Blazor Server (WASM, React, mobile) — adds the SignalR hub (`/mentor-hub`), the SSE endpoint (`/mentor/chat`) and the HTTP approve/cancel endpoints. Also the **MCP server** and the **A2A agent** (`AddMentorAgentMcp()` / `MapMentorAgentMcp()`, `AddMentorAgentA2A()` / `MapMentorAgentA2A()`): a Blazor Server app that publishes either one needs this package too, without the hub |
| [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) | Blazor WASM / Blazor Auto **client** project |
| [Novior.MentorAgent.Abstractions](https://www.nuget.org/packages/Novior.MentorAgent.Abstractions) | **Never install directly** — transitive dependency included automatically |
| [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) | **Optional** — define Level-2 specialist agents in YAML instead of C# |

The chat widget serves Blazor (Server, WebAssembly and Auto) and .NET MAUI Blazor Hybrid apps; any other front end,
or a headless backend, connects through `Novior.MentorAgent.Server`, which also serves MCP and A2A. Multi-agent
orchestration, cost control, contextual memory, RAG, Agent Skills and voice are all configured with a single
`AddMentorAgent()` call.

Built on top of **[Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/)**
(`Microsoft.Agents.AI`), MentorAgent turns the complexity of multi-agent systems into an attribute-driven
programming model, and works with any provider that offers an `IChatClient` or an `AIAgent`.

**Documentation:** guides and tutorials are at [docs.mentoragent.net](https://docs.mentoragent.net); this README is
the package's full reference.

**See it run:** the [demo](https://github.com/Novior-dev/MentorAgent/tree/main/demo) is a small expense-claims app with MentorAgent inside. Clone the repository, set a model key in the user secrets and `dotnet run`.

---

## Table of Contents

- [What is MentorAgent?](#what-is-mentoragent)
- [What can it do?](#what-can-it-do)
- [Architecture overview](#architecture-overview)
- [Getting started](#getting-started)
  - [Installation](#installation)
  - [Minimal setup](#minimal-setup)
  - [Add the widget to your layout](#add-the-widget-to-your-layout)
- [AI providers](#ai-providers)
- [The three-level agent model](#the-three-level-agent-model)
  - [Level 1 — Actions on a service](#level-1--actions-on-a-service)
  - [Level 2 — Specialized agent with Handoff](#level-2--specialized-agent-with-handoff)
  - [Level 3 — Collaborative team (Group Chat)](#level-3--collaborative-team-group-chat)
- [Page navigation](#page-navigation)
- [Page context and UI actions](#page-context-and-ui-actions)
- [Agent Skills](#agent-skills)
- [Contextual memory](#contextual-memory)
- [RAG — Retrieval-Augmented Generation](#rag--retrieval-augmented-generation)
- [MCP — Model Context Protocol](#mcp--model-context-protocol)
  - [MCP Client — consuming external MCP servers](#mcp-client--consuming-external-mcp-servers)
  - [MCP Server — exposing MentorAgent as an MCP server](#mcp-server--exposing-mentoragent-as-an-mcp-server)
- [A2A — Agent-to-Agent](#a2a--agent-to-agent)
  - [A2A Consumer — calling remote A2A agents](#a2a-consumer--calling-remote-a2a-agents)
  - [A2A Server — exposing MentorAgent as an A2A agent](#a2a-server--exposing-mentoragent-as-an-a2a-agent)
- [Persistent conversation history](#persistent-conversation-history)
- [Blazor Server vs Blazor WASM](#blazor-server-vs-blazor-wasm)
- [Built-in AI tools](#built-in-ai-tools)
- [Streaming responses](#streaming-responses)
- [Session serialize and restore](#session-serialize-and-restore)
- [Security](#security)
- [Token & cost optimization](#token--cost-optimization)
- [Middleware & extensibility](#middleware--extensibility)
- [Observability (OpenTelemetry)](#observability-opentelemetry)
- [Token & cost dashboard](#token--cost-dashboard)
- [Model routing](#model-routing)
- [Structured outputs](#structured-outputs)
- [Rich responses (tables & lists)](#rich-responses-tables--lists)
- [Generative UI — cards (Level 2)](#generative-ui--cards-level-2)
- [Declarative agents (YAML)](#declarative-agents-yaml)
- [Voice input and output](#voice-input-and-output)
- [Onboarding tour](#onboarding-tour)
- [Multimodal image input](#multimodal-image-input)
- [Hosted tools (web search, code interpreter, file search, images, remote MCP)](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp)
- [Human-in-the-loop tool approval](#human-in-the-loop-tool-approval)
- [Evaluation & regression testing](#evaluation--regression-testing)
- [Widget customization](#widget-customization)
  - [Themes](#themes)
  - [CSS variables — full reference](#css-variables--full-reference)
  - [Fonts and Content-Security-Policy](#fonts-and-content-security-policy)
  - [The admin dashboard is themed separately](#the-admin-dashboard-is-themed-separately)
- [All configuration options](#all-configuration-options)
- [Building your own UI — IMentorStateService](#building-your-own-ui--imentorstateservice)
  - [Driving a turn — IMentorOrchestrator](#driving-a-turn--imentororchestrator)
  - [Complete event reference](#complete-event-reference)
  - [Replacing the confirmation dialog](#replacing-the-confirmation-dialog)
- [Extensibility](#extensibility)
- [Requirements](#requirements)
- [Migrating from the MentorAgent.* packages](#migrating-from-the-mentoragent-packages)
- [Related Packages](#related-packages)
- [License](#license)

---

## What is MentorAgent?

MentorAgent turns a .NET application into an AI-assisted one. It provides:

- A **floating chat widget** — one `<ChatWidget />` tag in your layout, in Blazor (Server, WebAssembly, Auto) and in
  .NET MAUI Blazor Hybrid; it brings its own CSS and JS. Any other frontend talks to the same assistant through
  `Novior.MentorAgent.Server` (a SignalR hub and an SSE endpoint).
- A **Main Coordinator agent** that understands the application context and routes requests.
- An **attribute-driven discovery system** that scans your assemblies and turns plain C# methods and classes into AI tools and agents — zero boilerplate.
- Full integration with **Microsoft Agent Framework**, supporting Handoff Workflows, Group Chat teams, and any AI provider compatible with the `IChatClient` abstraction.
- **RAG** — inject relevant documents into every AI response, read through an `IMentorRagSource` you implement over your own store, or `MentorVectorStoreRagSource` over any `Microsoft.Extensions.VectorData` collection.
- **MCP** — consume external MCP servers as additional tools, and expose MentorAgent's own actions as an MCP server for other AI clients.
- **A2A** — connect to remote AI agents via the Agent-to-Agent protocol, and expose MentorAgent as a federatable A2A agent.

The developer defines *what the AI can do* using attributes. MentorAgent handles everything else: prompt building, agent wiring, session management, rate limiting, safety checks, memory, RAG retrieval, MCP connections, A2A federation, Agent Skills, and UI.

Before every LLM call, MentorAgent automatically enriches the system prompt with **real-time context**: the current URL, the active page name, the authenticated user and their roles, the data registered by the current page via `IMentorPageContext`, the user's contextual memory, the Agent Skills catalogue, and — when RAG is enabled — the most relevant documents retrieved from your vector store.

UI actions registered by the current page are injected as **individually named AI tools** (one tool per action, each with its own description and parameter schema), rather than a generic dispatcher. This gives the AI precise knowledge of exactly what it can do on the current page, reducing hallucinations and enabling direct invocation: `highlight_row(5)` instead of `invoke_ui_action("highlight_row", 5)`.

---

## What can it do?

| Feature | Description |
|---|---|
| 🤖 **Multi-agent orchestration** | Coordinator + specialized agents connected via Handoff Workflow |
| 👥 **Group Chat teams** | Multiple agents collaborate in a structured conversation before executing |
| 🛠️ **Tool discovery** | C# methods become AI tools via `[Description]` or `[MentorAction]` |
| 🎯 **UI Actions** | AI invokes page-level actions (highlight rows, open modals, pre-fill forms) as individually named tools with typed parameters and async support |
| 📖 **Agent Skills** | Domain knowledge + execution packages — knowledge loaded on demand via `load_skill` (progressive disclosure), methods registered as L1 tools |
| 🧠 **Contextual memory** | Remembers user preferences and actions across sessions |
| 🗺️ **Page navigation** | AI navigates to any page decorated with `[MentorPage]` |
| 💬 **Session management** | Conversation history managed by Agent Framework (`AgentSession`) |
| 💾 **Persistent history** | Pluggable `ChatHistoryProvider` (CosmosDB, Redis, custom) |
| 📚 **RAG** | Retrieval-Augmented Generation — inject relevant documents into every AI response, read through an `IMentorRagSource` you implement over your own store, or `MentorVectorStoreRagSource` over any `Microsoft.Extensions.VectorData` collection |
| 🔌 **MCP Client** | Consume external MCP servers as additional Level-1 tools for the coordinator |
| 🖥️ **MCP Server** | Expose your Level-1 actions (`[Description]` and `[MentorAction]` methods) as MCP tools — any MCP client can connect (Claude Desktop, VS Code, etc.). Gated actions (`RequiresConfirmation`, `RequiredRoles`, `RequiresApproval`) are withheld — an MCP caller cannot confirm anything — except a role-gated one published to a caller that `McpCallerPrincipal` identifies as holding the role |
| 🌐 **A2A Consumer** | Connect to remote A2A agents; the coordinator delegates to them through the same handoff workflow as local specialists, and every answer says which system it came from. A request that arrived over A2A is answered locally, never passed on to another peer |
| 📡 **A2A Server** | Expose MentorAgent as a federatable A2A agent — other orchestrators can discover and call it |
| 🎨 **Customizable widget** | Themes, colors, position, avatar, bot name |
| 🌍 **Multi-language** | Configurable language for AI responses and the widget UI |
| 🎤 **Voice input/output** | Browser-native Speech Recognition and Speech Synthesis — speaks while the answer streams, stops when the user takes the floor, optional hands-free loop |
| 🧭 **Onboarding tour** | First-run guide generated from the app's own pages and tools, with one-tap example questions |
| 🃏 **Generative UI cards** | A tool returns a card — fields, accent, buttons — and the widget renders it instead of the model describing it |
| 📄 **Declarative agents** | Specialist agents defined in YAML, reusing your existing tools with their role and approval gates |
| 🖼️ **Multimodal image input** | Send images to the assistant — upload, clipboard paste, drag & drop or URL — validated by an allow-list and delivered as native AF image content |
| 🌐 **Hosted tools** | Let the model provider run web search, a code-interpreter sandbox and file search on its own infrastructure — one option, no code |
| 🔒 **Safety check** | Optional AI-based prompt injection and jailbreak detection |
| ⏱️ **Rate limiting** | Per-user message limit with configurable time window (requires authentication for true per-user isolation) |
| ⏹️ **Stop button** | Cancel any in-flight AI request mid-stream — partial response is preserved in the chat |
| ✅ **Confirmation dialogs** | Destructive actions ask for user confirmation before execution — including MCP tools, and in the Agent Framework's native `ApprovalRequiredAIFunction` flow when you want AF interop |
| 🔐 **Role-based actions** | Actions restricted by ASP.NET Core identity roles |
| 📦 **Pluggable memory store** | Default in-memory, replaceable with Redis/EF Core/MongoDB |
| 💸 **Token & cost optimization** | Slim cache-friendly prompt, semantic tool filtering (embeddings), history compaction, RAG/memory gating |
| 🔌 **Any AI provider** | Azure OpenAI, OpenAI, Ollama, Azure AI Foundry, Anthropic, and more |

---

## Architecture overview

```
┌──────────────────────────────────────────────────────────────────────┐
│                         Blazor Application                           │
│                                                                      │
│   ┌──────────────┐      ┌────────────────────────────────────────┐   │
│   │  ChatWidget  │◄────►│          MentorOrchestrator            │   │
│   │  (Razor UI)  │      │          (Main Coordinator)            │   │
│   └──────────────┘      └────────────────┬───────────────────────┘   │
│                                          │                            │
│   ┌──────────────────────────────────────┤                            │
│   │   Per-call context injection         │                            │
│   │   · AppContextProvider (page,user)   │                            │
│   │   · RagContextProvider (documents)   │                            │
│   │   · SkillsContextProvider (catalogue)│                            │
│   │   · UIActionsMiddleware (per-action  │                            │
│   │     tools from IMentorPageContext)   │                            │
│   └──────────────────────────────────────┘                            │
│                                          │                            │
│              ┌───────────────────────────┼──────────────┐             │
│              ▼                           ▼              ▼             │
│        [MentorAgent]             [MentorAgent]    [MentorTeam]       │
│        OrderAgent                CustomerAgent    AnalysisTeam       │
│        (L2 Handoff)              (L2 Handoff)    (L3 GroupChat)      │
│              │                           │              │             │
│        [MentorAction]            [MentorAction]  [TeamMember]        │
│        C# methods                C# methods      sub-agents          │
│                                                                      │
│   ┌──────────────────────────────────────────────────────────────┐   │
│   │                    External Integrations                     │   │
│   │  MCP Client  │  MCP Server   │  A2A Consumer │  A2A Server        │   │
│   │  (tools from │  ([MentorAct] │  (remote       │ (agent-card.json + │   │
│   │   ext. MCPs) │   as MCP)     │   agents as L2)│  /a2a endpoint)    │   │
│   └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│   ┌──────────────────────────────────────────────────────────────┐   │
│   │  Built-in tools (each only when its feature is on)           │   │
│   │  navigate_to │ load_skill │ read_skill_resource               │   │
│   │  remember    │ forget_all │ invoke_ui_action (Path B only)    │   │
│   └──────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────┘
                               │
               Microsoft Agent Framework
               (ChatClientAgent, HandoffWorkflow,
                GroupChatWorkflow, AgentSession)
```

The **MentorOrchestrator** builds the Main Coordinator — a `ChatClientAgent` on Path A, your own `AIAgent` on Path B — and gives it every capability as a tool: the L1 actions the turn's user may run (see [Role-based actions](#role-based-actions)), MCP tools, the built-in tools, one tool per `[MentorTeam]` (a `GroupChatWorkflow` exposed under the team's snake_case name) and, when at least one specialist exists, `route_to_specialist`. That tool runs a `HandoffWorkflow` over the `[MentorAgent]` specialists, declarative agents and remote A2A agents and returns what the specialists said to the coordinator, which writes the reply. The built-in tools are conditional: `navigate_to` is always there; `load_skill` only when skills are enabled and at least one exists, `read_skill_resource` only when a skill has resources or comes from `SkillSources`; `forget_all` only with `UseMemoryContext`, and `remember` only when memory is on but auto-capture cannot run (Path B, or `MemoryAutoCapture = false`); `invoke_ui_action` only on Path B. Before every LLM call, three `AIContextProvider`s enrich the system prompt: `AppContextProvider` (page name, URL, user, roles, memory), `RagContextProvider` (retrieved documents), and `SkillsContextProvider` (skill catalogue). UI actions registered by the current page are injected as individual named tools by `UIActionsMiddleware` at the `IChatClient` level — with their own function-calling loop handled internally before any response reaches the outer pipeline. MCP tools from external servers are added as Level-1 tools alongside local `[MentorAction]` methods. Remote A2A agents sit in the same handoff workflow as local specialists, with three differences. A turn that itself **arrived** over A2A is never offered this host's remote agents — it is answered here, so chaining A → B → C through MentorAgent hosts is not supported in 1.0. On a host with remote agents, every `route_to_specialist` result opens with a note saying whose answer it is: a local specialist's (no remote agent was contacted), the remote system's, or `NOT X'S ANSWER` when a named remote agent never took part. And the router is told that a request addressed to a remote agent goes to it even when a local specialist covers the same subject.

---

## Getting started

### Installation

```bash
dotnet add package Novior.MentorAgent
```

`Novior.MentorAgent` brings no AI provider SDK: add the one you use, at the version you choose (up to 1.0.0-rc.14 it
carried Azure OpenAI's). For the Azure OpenAI example below:

```bash
dotnet add package Azure.AI.OpenAI --prerelease
dotnet add package Microsoft.Extensions.AI.OpenAI   # AsIChatClient() on the OpenAI SDK clients
dotnet add package Azure.Identity                    # only for DefaultAzureCredential / Entra ID
```

With the Azure **Responses** API, read [Provider package versions](#provider-package-versions) first.

### Minimal setup

In `Program.cs`:

```csharp
using Azure;                           // AzureKeyCredential
using Azure.AI.OpenAI;                 // AzureOpenAIClient (package Azure.AI.OpenAI)
using Novior.MentorAgent.Abstractions.Models; // MentorLanguage, MentorTheme, ...
using Novior.MentorAgent.Extensions;          // AddMentorAgent
using Microsoft.Extensions.AI;         // AsIChatClient

builder.Services.AddMentorAgent(options =>
{
	options.AppName        = "My App";
	options.AppDescription = "An order management application";
	options.Language       = MentorLanguage.Italian;

	// Choose your AI provider (see "AI providers" section below)
	options.ChatClient = new AzureOpenAIClient(
			new Uri("https://myresource.openai.azure.com"),
			new AzureKeyCredential(builder.Configuration["AzureOpenAI:Key"]!))
		.GetChatClient("gpt-4o-mini")
		.AsIChatClient();

	// Assemblies to scan for agents, actions, and pages
	options.ScanAssemblies = [typeof(Program).Assembly];
});
```

### Add the widget to your layout

In `MainLayout.razor` (or `App.razor`):

```razor
@using Novior.MentorAgent.Abstractions.Components

<ChatWidget />
```

That's it. The widget injects its own CSS and JS automatically — no changes to `App.razor` or `_Host.cshtml` are required. Only `ChatWidget` does this: a surface composed from the package's components without `ChatWidget` must add the `<link>` to `_content/Novior.MentorAgent.Abstractions/css/MentorAgent.css?v=1.3.0` and the `<script>` for `_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js?v=1.3.0` itself, or it renders unstyled and voice, paste and drag-and-drop stay off. The `?v=` is the version of the package you install: change it when you upgrade.

> **The widget must be interactive.** In a Blazor Web App make the app interactive as a whole — `<Routes @rendermode="InteractiveServer" />` in `App.razor` (the template's *Interactivity location: Global*). In a layout that renders statically, `<ChatWidget />` shows its button and nothing happens when it is clicked. A page rendered statically has no circuit either, so it cannot register `IMentorPageContext` data or UI actions. For Auto / WebAssembly apps see [Blazor Server vs Blazor WASM](#blazor-server-vs-blazor-wasm).

### Namespaces

The attributes and page services used in the rest of this README live in these namespaces. Add them once to `_Imports.razor`:

```razor
@* [MentorPage], [MentorAction], [MentorAgent], [MentorTeam], [MentorSkill] *@
@using Novior.MentorAgent.Attributes
@* IMentorPageContext, IMentorAgent, IMentorTeam, IMentorStateService *@
@using Novior.MentorAgent.Abstractions.Interfaces
@* <ChatWidget /> *@
@using Novior.MentorAgent.Abstractions.Components
```

and to the C# files that declare tools, agents, teams or skills (`[TeamMember]` is in the global namespace):

```csharp
using System.ComponentModel;               // [Description]
using Novior.MentorAgent.Attributes;              // [MentorAction], [MentorAgent], [MentorTeam], [MentorSkill]
using Novior.MentorAgent.Abstractions.Interfaces; // IMentorAgent, IMentorTeam
```

---

## AI providers

MentorAgent supports any provider via two entry points: `IChatClient` (recommended for most cases) or `AIAgent` (required for providers that don't expose `IChatClient`). The provider's packages are yours to add: the snippets below need `Azure.AI.OpenAI` (Azure), `Microsoft.Extensions.AI.OpenAI` (`AsIChatClient` on the OpenAI clients; it brings `OpenAI`), `Azure.Identity` (`DefaultAzureCredential`), and for the last two `Microsoft.Agents.AI.Foundry` or `Microsoft.Agents.AI.Anthropic`. One combination needs a version hold: [Provider package versions](#provider-package-versions).

```csharp
// ── Azure OpenAI — Chat Completions
options.ChatClient = new AzureOpenAIClient(endpoint, credential)
	.GetChatClient("gpt-4o-mini").AsIChatClient();

// ── Azure OpenAI — Responses API (service-managed history)
// GetResponsesClient() takes no argument — the deployment name goes to AsIChatClient.
// The Responses client is experimental in the OpenAI SDK: without the pragma the build fails with OPENAI001.
#pragma warning disable OPENAI001
options.ChatClient = new AzureOpenAIClient(endpoint, credential)
	.GetResponsesClient().AsIChatClient("gpt-4o-mini");
#pragma warning restore OPENAI001
// Required with Responses: the service owns the conversation, and the Agent Framework refuses to combine
// its conversation id with a local ChatHistoryProvider — without this every turn fails with
// "Only ConversationId or ChatHistoryProvider may be used, but not both".
options.UseServiceManagedHistory = true;

// ── OpenAI direct
options.ChatClient = new OpenAIClient("sk-...")
	.GetChatClient("gpt-4o").AsIChatClient();

// ── Ollama (local) — through its OpenAI-compatible endpoint: same SDK, no extra package
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
// On a slow local model also raise SafetyCheckTimeout, or point the checks at a fast ClassifierChatClient.
// WarmUpChatModel, with WarmUpAtStartup, loads the local model when the application starts instead of on
// the first turn.
// Ollama ignores tool_choice: RequireLookupForRecordQuestions and RequireActionForChangeRequests have no
// effect with it, and a warning says so at the first turn (see Semantic tool filtering). For them, use
// llama-server or vLLM below.

// ── llama-server (llama.cpp, local) — the same SDK on its OpenAI-compatible endpoint. Tool calls need the
//    Jinja chat template; -c sets the context (0, the default, takes the model's):
//      llama-server --jinja -hf bartowski/Qwen2.5-7B-Instruct-GGUF:Q4_K_M -c 16384 --port 8080
options.ChatClient = new OpenAIClient(
		new System.ClientModel.ApiKeyCredential("local"),   // the SDK requires one
		new OpenAIClientOptions
		{
			Endpoint       = new Uri("http://localhost:8080/v1"),
			NetworkTimeout = TimeSpan.FromMinutes(20),
		})
	.GetChatClient("qwen2.5-7b-instruct").AsIChatClient();
// llama.cpp's documentation does not say whether tool_choice "required" is honoured: the warning tells you
// at the first turn that ignored it.

// ── vLLM — the same SDK. Tool calls need --enable-auto-tool-choice and the parser of the model's family:
//    hermes for Qwen 2.5 and Hermes, mistral for Mistral, llama3_json (with its --chat-template) for
//    Llama 3.1 and 3.2; the others are on vLLM's tool-calling page. tool_choice "required" is supported
//    from vLLM 0.8.3:
//      vllm serve Qwen/Qwen2.5-7B-Instruct --enable-auto-tool-choice --tool-call-parser hermes
options.ChatClient = new OpenAIClient(
		new System.ClientModel.ApiKeyCredential("local"),
		new OpenAIClientOptions { Endpoint = new Uri("http://localhost:8000/v1") })
	.GetChatClient("Qwen/Qwen2.5-7B-Instruct").AsIChatClient();

// ── Sampling of the coordinator — for a local model whose defaults suit chat rather than tool calls.
//    Any value left null is the provider's. The coordinator's only: the classifiers keep their own settings,
//    and a reasoning model may refuse a temperature (leave it unset there).
//    In configuration: "Mentor": { "CoordinatorSampling": { "Temperature": 0.2 } }
options.CoordinatorSampling = new MentorSamplingOptions { Temperature = 0.2f, TopP = 0.9f };

// ── Azure AI Foundry (AIProjectClient) — requires AIAgent, and:
//    <PackageReference Include="Microsoft.Agents.AI.Foundry" Version="[1.17.0-preview.260804.1]" />
options.Agent = new AIProjectClient(endpoint, credential)
	.AsAIAgent(model: "gpt-4o-mini", instructions: "You are a helpful assistant.");

// ── Anthropic Claude — requires AIAgent, and: dotnet add package Microsoft.Agents.AI.Anthropic --prerelease
options.Agent = new AnthropicClient() { APIKey = apiKey }
	.AsAIAgent(model: "claude-haiku-4-5", instructions: "...");
```

> ⚠️ Set either `ChatClient` **or** `Agent`, not both.

### Embedding model (optional)

An embedding model is what makes **semantic tool filtering**, **semantic memory relevance** and **semantic model routing** possible — but setting it switches none of them on. Each has its own flag, off by default: `EnableToolFiltering = true`, `MemoryRelevanceFiltering = true`, and `RoutingStrategy = MentorRoutingStrategy.Semantic` (together with a `StrongChatClient`) — see [Token & cost optimization](#token--cost-optimization). Optional — everything works without it: a flag turned on without a generator logs a warning and falls back (all tools sent, most recent memories, no routing), never to a keyword heuristic.

```csharp
// Azure OpenAI — a separate embedding deployment
options.EmbeddingGenerator = new AzureOpenAIClient(endpoint, credential)
	.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

// OpenAI direct
options.EmbeddingGenerator = new OpenAIClient("sk-...")
	.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();
```

### Path A vs Path B — feature matrix

| Feature | `ChatClient` (Path A) | `AIAgent` (Path B) |
|---|---|---|
| L1 actions | ✅ | ✅ |
| L2 Handoff agents | ✅ | ❌ requires `IChatClient` — skipped, with a warning when the coordinator is built |
| L3 Group Chat teams | ✅ | ❌ requires `IChatClient` — skipped, with a warning when the coordinator is built |
| A2A remote agents | ✅ | ❌ requires `IChatClient` — skipped, with a warning when the coordinator is built, and the peers' cards are not fetched |
| MCP client tools | ✅ | ✅ |
| RAG | ✅ | ✅ |
| Agent Skills | ✅ | ✅ |
| UI Actions (per-action tools) | ✅ via `UIActionsMiddleware` | ⚠️ single generic `invoke_ui_action` dispatcher |
| Safety check (input and output) | ✅ | ⚠️ runs only when `ClassifierChatClient` is set — otherwise skipped, with a warning when the coordinator is built |
| Session history reduction | ✅ configurable | ✅ configured in the agent |
| Custom `ChatHistoryProvider` | ✅ via options | ⚠️ must be set in the pre-built agent |
| Declarative (YAML) agents | ✅ | ❌ skipped with a warning — they need a `ChatClient` |
| Hosted tools (`HostedTools`) | ✅ | ❌ declare them on the pre-built agent itself (warning when the coordinator is built) |
| Semantic tool filtering (`EnableToolFiltering`) | ✅ | ❌ no effect (warning when the coordinator is built) — filter the tools on your agent |
| Model routing (`StrongChatClient`) | ✅ | ❌ |
| Memory auto-capture (`MemoryAutoCapture`) | ✅ | ✅ with a `ClassifierChatClient`; without one ⚠️ falls back to the `remember` tool |
| Structured outputs (`IMentorStructured`) | ✅ | ❌ returns `default` with a warning |

> When using `AIAgent` (Path B), `ChatHistoryProvider` **cannot** be added after construction — configure it directly in the agent you pass to `options.Agent`.

**Testing without a model.** MentorAgent does not publish a fake `IChatClient` for tests. To run an application
without a model, give `ChatClient` an `IChatClient` of your own that returns fixed responses.

---

## The three-level agent model

MentorAgent organizes AI capabilities in three progressive levels of complexity.

### Level 1 — Actions on a service

The simplest level. Decorate C# methods with `[Description]` (standard .NET, zero dependencies) or `[MentorAction]` (adds confirmation, roles, navigation, hints).

Which methods become actions, what they are called and how roles and confirmations add up is set by
rules with stable names, each one covered by a test: see
[ACTION-RULES.md](https://github.com/Novior-dev/MentorAgent/blob/main/ACTION-RULES.md).

```csharp
// Register the service in DI as usual
builder.Services.AddScoped<OrderService>();
```

```csharp
public class OrderService
{
	// Simple — [Description] from System.ComponentModel, zero dependencies
	[Description("Returns the list of active orders")]
	public async Task<List<Order>> GetActiveOrdersAsync() { ... }

	// Advanced — full [MentorAction] with all parameters
	[MentorAction(
		Description          = "Cancels an existing order",
		Category             = "Orders",             // tag of this skill on the A2A agent card
		RequiresConfirmation = true,                 // shows confirmation banner before executing
		RequiredRoles        = ["Manager"],          // any one of the listed roles is enough
		ProactiveHint        = "Suggest checking active orders first", // appended to the tool description as "Guidance: ..."
		NavigateTo           = "/orders")]           // AI navigates here automatically after success
	public async Task<bool> CancelOrderAsync(int orderId, string reason) { ... }
}
```

MentorAgent discovers the service via `ScanAssemblies` and exposes its methods as AI tools automatically. The AI calls the right method based on the user's natural language request.

**`[MentorAction]` parameters summary:**

| Parameter | Description |
|---|---|
| `Description` | **Required.** Natural language description used as the AI tool description. An `IMentorActionGovernor` can replace it for the model; the confirmation banner keeps it |
| `Category` | Published as the tag of this action's skill on the A2A agent card. It groups nothing and is not used anywhere else |
| `RequiresConfirmation` | Shows a confirmation banner before executing. Use for destructive or irreversible operations |
| `RequiresConfirmationDeclared` | Read-only, not set in the attribute: `true` when the attribute writes `RequiresConfirmation`, true or false. It tells an action that declares nothing, which asks with `RequireConfirmationForWrites` on, from one that declares `RequiresConfirmation = false`, which does not: `RequiresConfirmation` reads `false` for both |
| `ReadOnly` | Declares that the action only reads — it changes no data. MentorAgent takes your word for it: the action is published to MCP clients with `readOnlyHint`, which some clients take as leave to skip their own confirmation — so a write declared `ReadOnly` by mistake runs from them with nobody asked — and with `RequireLookupForRecordQuestions` on it is one the assistant may be required to call before answering a question about a value of your records. Ignored when the action also needs confirmation. Declare `[Description]` methods by name with `options.IsReadOnlyAction` |
| `RequiredRoles` | ASP.NET Core identity roles allowed to invoke the action — holding **any one** of them is enough (`["Manager", "Admin"]` means Manager *or* Admin). The user must be authenticated and an `AuthenticationStateProvider` must be registered, otherwise the call is refused. The model of the coordinator — or of the specialist, team member or agent source that holds the action — is offered it only in a turn whose user holds one of the roles, and the coordinator counts it among its actions only then; for everyone else it is left out, as from the MCP server's `tools/list`. Empty = no role check, accessible to everyone including anonymous users |
| `ProactiveHint` | Guidance for the model on when or how to use the action. Appended to the tool's description as `Guidance: <hint>` for the assistant's own tools (coordinator, specialists, team members); not added on the MCP server surface, and ignored by semantic tool filtering (tools are matched on `name: description` without the hint) |
| `NavigateTo` | URL the AI navigates to automatically after successful execution |
| `Enabled` | `false` ships the action off: it stays in `IMentorActionCatalog` and nowhere else — offered to no agent, not on `/mcp` or the A2A card — and a call to it does not run, until an `IMentorActionGovernor` turns it on (see [Governing actions from outside the code](#governing-actions-from-outside-the-code--imentoractiongovernor)). Default `true` |

#### Which methods become tools

The same two rules hold at every level — the coordinator's actions, a specialist's methods and a team
member's `Tools`:

- **Public instance methods declared on the class itself**, marked `[MentorAction]` or `[Description]` with
  a text. A method inherited from a base class is not discovered, nor is a static method, and a
  `[Description("")]` is ignored. Declare the method on the class you scan (or list in `Tools`).
- **A method with `[Description]` alone is a tool with no roles and no confirmation.** `RequiredRoles`,
  `RequiresConfirmation` and the default that makes writes ask exist only on `[MentorAction]`. Such a
  method is offered like any other action — with [tool filtering](#semantic-tool-filtering) on, also among
  the actions sent when a message matches nothing — so a method that changes data needs `[MentorAction]`,
  and one that only reads is declared with `options.IsReadOnlyAction`.

#### Tool names — the rule you need before configuring anything else

Several options identify a tool **by name**, not by method: `RequiresApproval`, `IsReadOnlyAction`,
`OnToolResult`, `EvalChecks.ToolCalledCheck`, the tool-filtering logs, the metrics `TopActions` list.
The attributes do not: a call is checked against the `RequiredRoles`, `RequiresConfirmation` and
`NavigateTo` of the method it runs, so a coordinator's action and a specialist's that share a name each
keep their own. The name is derived from the C# member, so you have to know the rule:

> **PascalCase → snake_case, with a trailing `Async` dropped first.**

| C# member | Tool name |
|---|---|
| `GetActiveOrdersAsync()` | `get_active_orders` |
| `CancelOrderAsync(int, string)` | `cancel_order` |
| `GetHTTPStatus()` | **get_h_t_t_p_status** — every capital starts a new word, so acronyms split letter by letter; name it `GetHttpStatus` to get **get_http_status** |
| `[MentorTeam(Name = "Business Analysis Team")]` | `business_analysis_team` |
| `[MentorSkill(Name = "expense-report")]` | *no tool* — a skill is loaded with `load_skill("expense-report")` under its own name, unchanged; only its **methods** become tools (`SubmitExpenseReportAsync` → `submit_expense_report`) |

Spaces and dashes become underscores, so an attribute `Name` is normalised the same way a method
name is. Gating an action therefore looks like this — note it is the *tool* name, not the method:

```csharp
options.RequiresApproval = tool => tool is "cancel_order" or "issue_refund";
```

Renaming a C# method renames its tool, which silently unhooks any option keyed on the old name.
There is no compile-time link between the two, so pin the ones that matter in a test:

```csharp
[Fact]
public void CancelOrder_StillRequiresApproval()
    => _options.RequiresApproval!("cancel_order").Should().BeTrue();
```

---

### Level 2 — Specialized agent with Handoff

Decorate a class with `[MentorAgent]` to create a dedicated AI agent connected to the Main Coordinator via a **Handoff Workflow**. The coordinator delegates by calling its `route_to_specialist` tool (with the specialist's name in `specialist` when the user names one): a router inside the workflow hands the request to the matching agent, and what the specialist says comes back to the coordinator as the tool result — the coordinator writes the reply the user sees. Delegation requires `ChatClient` (Path A).

```csharp
[MentorAgent(
	Name        = "OrderAgent",
	Description = "Handles everything related to orders: creation, tracking, cancellation",
	HandoffTo   = ["CustomerAgent", "InvoiceAgent"]  // can hand off further
)]
public class OrderAgent : IMentorAgent
{
	private readonly OrderService _orders;
	public OrderAgent(OrderService orders) => _orders = orders;

	[Description("Creates a new order for a customer")]
	public async Task<Order> CreateOrderAsync(int customerId, List<string> products) { ... }

	[Description("Retrieves the status of an order")]
	public async Task<OrderStatus> GetOrderStatusAsync(int orderId) { ... }
}
```

Register the agent in DI:

```csharp
builder.Services.AddScoped<OrderAgent>();
```

MentorAgent builds a `ChatClientAgent` from the class, wires it into the Handoff graph, and generates the system prompt from `Name` and `Description` (or you can supply a custom `Instructions`).

Implementing `IMentorAgent` is optional — it is an empty marker interface that MentorAgent never reads (discovery is by the attribute alone), useful to find and inject your agents in your own code and tests. It validates nothing: a missing `AddScoped<OrderAgent>()` is reported as a warning when the coordinator is built, and that agent's tools are dropped.

**`[MentorAgent]` parameters:**

| Parameter | Required | Description |
|---|---|---|
| `Name` | ✅ | Agent name — used as the key in the Handoff graph and in the coordinator's system prompt |
| `Description` | ✅ | Natural language description of the agent's capabilities. Used by the coordinator to decide when to delegate |
| `HandoffTo` | — | Names of other `[MentorAgent]` agents this agent can hand off to. Must match the `Name` values exactly (case-insensitive) |
| `Instructions` | — | Custom system prompt for this agent, used **exactly as written**. If omitted, MentorAgent auto-generates one from `Name` and `Description` — and the generated prompt carries a grounding rule (*state only facts a tool returned; if nothing provides what is asked, say so — never estimate or invent a number, a name, a date or a status*). A custom prompt replaces that rule too: **include your own**, or a specialist asked for a figure none of its tools returns may make one up |

> ⚠️ **Important:** agent names in `HandoffTo` must match exactly the `Name` in the target `[MentorAgent]` attribute (case-insensitive). A mismatch produces a warning in the logs when the coordinator is built — on the first message of a session, or at startup with `WarmUpAtStartup = true` — and skips that handoff edge.

**What a delegation returns.** The coordinator delegates by calling `route_to_specialist(request, specialist?)`. Inside, a small router agent (its own ~400-token prompt, no application tools) hands the request to exactly one specialist. The tool gives the coordinator what the specialists **said**, as plain text. Three other outcomes are reported explicitly, so the assistant never tells the user that something was forwarded when it was not:

| Result starts with | Meaning | Log |
|---|---|---|
| *(the specialist's answer)* | A specialist took the request | Debug: `route_to_specialist returned: …` (who spoke, never the text) |
| `NOT DELEGATED` | No specialist took it (the router answered itself or found no match). The coordinator answers with its own tools or says it could not be handled | Warning: `route_to_specialist ended without any specialist answering …` |
| `DECLINED BY THE USER` | A specialist's action asked for confirmation and the user declined it. The result names the declined action, and the coordinator says in one sentence that it did not proceed, with no reason and no retry. When another action ran in the same request, the result starts `DECLINED BY THE USER, IN PART`, names both, and the coordinator says what was and was not done; reads (declared `ReadOnly` or matched by `IsReadOnlyAction`) are not counted. The specialist's words are not passed on | Debug: `route_to_specialist: the user declined an action in the delegated request.` |
| `DELEGATION FAILED` | A specialist was reached and threw | Warning: `route_to_specialist: the specialist it was handed to failed — <error>` |

The router, the specialists and the team members all run on your `ChatClient` inside the token meter, so a delegated turn shows up in full on the [dashboard](#token--cost-dashboard).

---

### Level 3 — Collaborative team (Group Chat)

Use `[MentorTeam]` to define a **Group Chat** where multiple AI agents collaborate — debating, analyzing, and approving — before a result is returned to the user or handed off to an L2 agent for execution.

```csharp
[MentorTeam(
	Name          = "BusinessAnalysisTeam",
	Description   = "Analyzes business data and produces an approved action plan",
	MaxIterations = 8,                                      // forced termination after 8 turns
	TriggerOn     = ["analysis", "business plan", "budget"], // keywords that activate this team
	HandoffTo     = ["OrderAgent", "CustomerAgent"])]
public class BusinessAnalysisTeam : IMentorTeam            // IMentorTeam is optional, recommended
{
	[TeamMember(
		Role         = "DataAnalyst",
		Tools        = [typeof(ReportTools), typeof(OrderTools)],
		Instructions = "Analyze the data and propose solutions with numbers.")]
	public object? Analyst { get; set; }

	[TeamMember(
		Role         = "BusinessApprover",
		Instructions = "Review the analyst's proposal. Reply APPROVED or REJECTED with reasons.")]
	public object? Approver { get; set; }

	[TeamTerminationCondition]
	public bool ShouldTerminate(string lastMessage, string lastSpeaker)
		=> lastSpeaker == "BusinessApprover" &&
		   (lastMessage.Contains("APPROVED") || lastMessage.Contains("REJECTED"));
}
```

MentorAgent builds a `GroupChatWorkflow` from this declaration and gives it to the coordinator as a tool named after the team (`business_analysis_team` — see [Tool names](#tool-names--the-rule-you-need-before-configuring-anything-else)); the discussion's text is the tool's result. The team is not a node of the handoff graph, and `HandoffTo` hands nothing off by itself: the agents it names are added to the tool's description, and when the final message carries `[TEAM:APPROVED]` and no member wrote `[TEAM:REJECTED]` — the tokens every member is instructed to emit, the second only for a definitive no (a request for changes carries neither; both are stripped before the text reaches the user) — the result ends with an instruction to execute the approved actions, which the coordinator does through `route_to_specialist`. It also says that an action may ask the user to confirm first, and that a declined confirmation is the user's decision, not a problem. A discussion whose final message carries no verdict is not approved, and neither is a "yes" followed by a member's `[TEAM:REJECTED]`. Without a `[TeamTerminationCondition]` the discussion ends at the first verdict; a host's condition decides alone when to stop, within `MaxIterations`. A plain "APPROVED" satisfies the termination condition above but does not trigger that step.

Register every class listed in `[TeamMember(Tools = ...)]` in DI (`builder.Services.AddScoped<ReportTools>();`) — a tool class the container cannot resolve is skipped with a warning and the member runs without it. Teams require `ChatClient` (Path A).

A class listed in `Tools` is that member's: its methods are **not** also the coordinator's actions, nor published on `/mcp` or the A2A card, even though it is in DI. At startup an Information line names each such class, the members that list it and its methods. The exception is a `[MentorSkill]` class, whose methods are coordinator tools by the skill's own role. Member tools are discovered by the same rules as the coordinator's ([Which methods become tools](#which-methods-become-tools)), and keep what `[MentorAction]` declares (`ReadOnly`, roles, confirmation). A member is told *"These tools are read-only."* only when every one of its tools is declared read-only (`ReadOnly = true` or `IsReadOnlyAction`) and none asks for confirmation.

**`[MentorTeam]` parameters:**

| Parameter | Required | Description |
|---|---|---|
| `Name` | ✅ | Team name — used as the key in the routing graph and shown in logs |
| `Description` | ✅ | Description of the team's purpose. Used by the coordinator to decide when to activate it |
| `TriggerOn` | — | Keywords injected into the coordinator's prompt as a strong directive: *ALWAYS delegate to this team when the request involves* these words, *do NOT answer directly*. The model still makes the call (there is no code-level route), but choose specific keywords — a broad one such as `"order"` sends ordinary requests through a multi-turn Group Chat |
| `MaxIterations` | — | Maximum number of turns in the Group Chat before forced termination. Default: `10` |
| `HandoffTo` | — | L2 agents to delegate execution to after the team approves a plan |

**`[TeamMember]` parameters:**

| Parameter | Required | Description |
|---|---|---|
| `Role` | ✅ | Role name within the team (e.g. `"DataAnalyst"`, `"Approver"`) |
| `Instructions` | ✅ | System prompt for this member — defines what it should do and how it should respond |
| `Tools` | — | Tool classes this member can call during the discussion — the member's only, not the coordinator's. Should be **read-only** — write operations belong in L2 agents via `HandoffTo` |

- **`[TeamTerminationCondition]`** is required when you need custom exit logic. Required method signature: `bool ShouldTerminate(string lastMessage, string lastSpeaker)`. Without it, members take turns round-robin and the discussion stops at the first message that carries `[TEAM:APPROVED]` or `[TEAM:REJECTED]`, or after `MaxIterations` turns.
- **`TriggerOn`** keywords are injected into the Coordinator's prompt as an *always delegate to this team* directive — the model still decides, but pick specific words, because a broad one routes ordinary requests through the Group Chat.
- `[TeamMember]` tools should be **read-only** (queries, reports). Write operations should be delegated to L2 agents via `HandoffTo`.
- Implementing `IMentorTeam` is optional but recommended for the same reasons as `IMentorAgent`.

---

## Page navigation

Decorate Blazor pages with `[MentorPage]` to let the AI navigate to them on user request.

```razor
@* Simple page — navigation only *@
@attribute [MentorPage(Url = "/orders", Name = "Orders")]

@* Page with UI actions — on Blazor Server and in MAUI Blazor Hybrid, navigate_to waits for SignalReady() *@
@attribute [MentorPage(Url = "/orders-interactive", Name = "Interactive Orders",
	HasUIActions = true,
	ReadyTimeout = 3000,
	Description  = "Order management with inline editing and filtering")]
```

Pages are discovered at startup from `ScanAssemblies` — they don't need to be visited first. When `HasUIActions = true` and the page runs in the same process as the turn — a Blazor Server circuit or a MAUI Blazor Hybrid app — `navigate_to` waits for the new page to call `PageContext.SignalReady()` (up to `ReadyTimeout`), so its UI actions are registered before the AI continues. A signal the page being left gave when it opened does not count. To the page already shown (the same path, whatever the query) `navigate_to` does not wait, since Blazor does not initialize that page again. A turn that arrives over the hub (WebAssembly, React, a MAUI app with its own native UI…), SSE or A2A does **not** wait — nothing there can call `SignalReady()`; the new page's actions reach the model with the client's next page-context snapshot, i.e. on the user's next message. On WebAssembly `SignalReady()` is a no-op and costs nothing, so the same page code works on both.

**A component with two routes**, such as `/orders` and `/orders/{id}`: when `navigate_to` goes from one of its routes to the other, Blazor keeps the same instance and does not run `OnInitialized` again, so no new signal comes. The wait runs to `ReadyTimeout` and logs its warning, then the turn goes on with the page's actions, which are still registered. Call `SignalReady()` at the end of `OnParametersSet` too (BUG-194).

**`[MentorPage]` parameters:**

| Parameter | Required | Description |
|---|---|---|
| `Url` | ✅ | Page URL (e.g. `"/orders"`). Used by the `navigate_to` tool |
| `Name` | ✅ | Human-readable page name injected into the system prompt |
| `Description` | — | Optional description of the page's features shown to the AI |
| `HasUIActions` | — | If `true`, `navigate_to` waits for `PageContext.SignalReady()` on the new page — only where the page runs in the turn's process (a Blazor Server circuit or a MAUI Blazor Hybrid app); hub (WebAssembly, React, native MAUI…), SSE and A2A turns do not wait, and the page's actions reach the model on the next message. Default: `false` |
| `ReadyTimeout` | — | Timeout in milliseconds for `SignalReady()`. Used only when `HasUIActions = true` and the page runs in the turn's process (a Blazor Server circuit or a MAUI Blazor Hybrid app). Default: `2000` |

---

## Page context and UI actions

`IMentorPageContext` is a scoped service injectable in any Blazor page. It has two responsibilities:

1. **Share visible data** — key/value pairs serialized live into the AI system prompt before every call (filter state, selected item, visible row count, etc.).
2. **Register UI actions** — C# lambdas the AI can invoke directly on the current page without going through a business service (highlight a row, open a modal, pre-fill a form).

### How UI actions work (architecture)

Each registered UI action becomes its **own named AI tool** before every LLM call — injected dynamically via `UIActionsMiddleware` (a `DelegatingChatClient` wrapping the raw model). The AI sees `highlight_row(parameter: integer: order ID)` directly in its tool list, not a generic dispatcher. This:

- Eliminates hallucinated action names (the AI sees exact names, not strings to guess)
- Carries each action's parameter description (`parameterHint`) in its tool description — the JSON schema itself is the same for every action (one optional `parameter` of any JSON type), so the hint is what tells the model what to pass
- Skips, with a warning, an action whose name matches a tool the coordinator already has (an L1 action, an MCP tool, `navigate_to`…) — give UI actions names of their own
- Enables reliable multi-action chains

UI actions are scoped to the **current page**: they appear in the tool list only while the page is mounted, and disappear the moment the page calls `PageContext.Clear(pageName)` in `Dispose()`, with the name it gave `SetPageName`. Blazor disposes the old page after it has initialized the new one, so a bare `Clear()` there would also remove what the new page has just registered. Give every page its own name: when two components share one, the old page's `Clear(pageName)` takes it for the current page and clears everything.

### Registering UI actions

Four overloads are available, from simple to fully typed and async:

```razor
@using Novior.MentorAgent.Abstractions.Interfaces
@inject IMentorPageContext PageContext
@implements IDisposable

@code {
protected override void OnInitialized()
{
    PageContext
        .SetPageName("Orders")
        // ── Share current UI state with the AI ───────────────────────────────
        .Set("ActiveFilter",  "Pending")
        .Set("VisibleRows",   _orders.Count)
        .Set("SelectedOrder", _selectedOrder)

        // ── 1. Simple action — no parameter ─────────────────────────────────
        .RegisterUIAction(
            "open_create_modal",
            "Opens the new order creation dialog",
            _ => OpenCreateModal())

        // ── 2. Typed parameter — no manual casting required ──────────────────
        .RegisterUIAction<int>(
            "highlight_row",
            "Highlights an order row by its ID",
            id => HighlightRow(id),          // id is int, not object?
            parameterHint: "integer: order ID")

        // ── 3. Typed async — awaited before the AI continues ─────────────────
        .RegisterUIActionAsync<int>(
            "select_order",
            "Selects an order and loads its details panel",
            async id => {
                await InvokeAsync(() => { _selectedId = id; StateHasChanged(); });
            },
            parameterHint: "integer: order ID")

        // ── 4. Complex typed parameter (DTO deserialized from JSON) ──────────
        .RegisterUIActionAsync<OrderFormModel>(
            "prefill_form",
            "Pre-fills the order form with the provided data",
            async model => {
                await InvokeAsync(() => { _form = model; StateHasChanged(); });
            },
            parameterHint: "JSON: { customerId, items, notes }");

    // Signal the AI that the page is ready — last line of OnInitialized, required when HasUIActions = true
    // (Blazor Server, MAUI Blazor Hybrid: navigate_to waits for it; WebAssembly: a no-op)
    PageContext.SignalReady();
}

// Clear this page's context and actions in Dispose, by the name given to SetPageName: Blazor disposes
// the old page after the new one has registered its own, which a bare Clear() would remove too
public void Dispose() => PageContext.Clear("Orders");
}
```

### UI action overloads reference

| Overload | Parameter | Execution | Use when |
|---|---|---|---|
| `RegisterUIAction(name, desc, Action<object?>)` | Raw `object?` (cast manually) | Synchronous | Simple no-param or legacy code |
| `RegisterUIAction<TParam>(name, desc, Action<TParam>)` | Auto-deserialized from JSON | Synchronous | Typed param, sync handler |
| `RegisterUIActionAsync(name, desc, Func<object?, Task>)` | Raw `object?` | Async | No-param async actions |
| `RegisterUIActionAsync<TParam>(name, desc, Func<TParam, Task>)` | Auto-deserialized from JSON | Async | Typed param, async handler *(recommended)* |
| `UnregisterUIAction(name)` | — | — | Remove a specific action dynamically (e.g. when a feature becomes unavailable) |

### Context data methods reference

| Method | Description |
|---|---|
| `SetPageName(name)` | Sets the current page name injected into the system prompt |
| `Set(key, value)` | Adds or updates a context entry (any serializable value) |
| `Remove(key)` | Removes a single context entry without clearing everything |
| `Clear()` | Removes all context data and all registered UI actions, whichever page registered them |
| `Clear(pageName)` | Removes what the page named `pageName` registered, or everything when it is still the current page. Call it from `Dispose()` with the page's own name, and call `SetPageName` before anything else |

### Automatic parameter schema generation

For the two typed overloads (`RegisterUIAction<TParam>` and `RegisterUIActionAsync<TParam>`), the `parameterHint` argument is **automatically generated from the type via reflection** when you omit it. You only need to provide it if you want to override the generated text. Blazor Server and WebAssembly use the same generator, and the hub keeps the hint a client sends, so the model sees `(parameter: …)` for WebAssembly, React and MAUI actions too.

| `TParam` | Auto-generated `parameterHint` |
|---|---|
| `int`, `long`, `short`, `byte` | `"integer"` |
| `float`, `double`, `decimal` | `"number"` |
| `bool` | `"boolean"` |
| `string` | `"string"` |
| `Guid` | `"string (GUID)"` |
| `DateTime`, `DateTimeOffset` | `"string (ISO 8601 date)"` |
| `Status` *(enum)* | `"string (Active\|Inactive\|Pending)"` |
| `int?` | `"integer?"` |
| `List<int>` | `"array<integer>"` |
| `Dictionary<string,int>` | `"object<string,integer>"` |
| `OrderFormModel` *(class)* | `"{ customerId: integer, productName: string, quantity: integer }"` |

This means the AI sees a precise, field-by-field description of what to pass — even for complex DTOs — without any manual work:

```csharp
// parameterHint omitted → auto-generated as "{ customerId: integer, productName: string, quantity: integer }"
.RegisterUIActionAsync<OrderFormModel>(
    "prefill_form",
    "Pre-fills the order form with the provided data",
    async model => {
        await InvokeAsync(() => { _form = model; StateHasChanged(); });
    })

// Override only when the auto-generated hint is not descriptive enough
.RegisterUIAction<int>(
    "highlight_row",
    "Highlights an order row by its ID",
    id => HighlightRow(id),
    parameterHint: "integer: order ID")   // ← manual override for extra clarity
```

> **Async handlers are awaited on Blazor Server** — the AI waits for the handler to complete before generating its next response. This makes multi-action chains reliable: the AI can call `select_order(42)` and then `highlight_row(42)`, knowing each step is done before it proceeds. A UI action requested through the hub (WebAssembly, React, MAUI…) is fire-and-forget: the model is told it succeeded once the request is sent to the client, not when the handler finishes.

The AI automatically receives current page context (URL, page name, registered data) injected into its system prompt on every call — with no extra configuration required.

---

## Agent Skills

Agent Skills are **domain knowledge + execution packages** with two complementary roles:

| Role | What it provides | When used |
|---|---|---|
| **Knowledge** | Instructions, policy, rules, workflows (markdown) | Loaded on demand via `load_skill` |
| **Tools** | `[Description]`/`[MentorAction]` methods on the skill class | Always available as L1 tools |

Rather than dumping all domain knowledge into the system prompt, MentorAgent uses **progressive disclosure**:

1. The **skill catalogue** (a short header plus one `name: description` line per skill — a few dozen tokens each) is always injected into the system prompt.
2. When the AI needs expertise, it calls `load_skill("skill-name")` to receive the full instructions — then uses that knowledge to call the right methods with the right parameters.
3. Attached resource files (policy docs, FAQ pages, templates) are accessible via `read_skill_resource`.

10 registered skills cost roughly **1,000 tokens** in the system prompt instead of the 50,000+ tokens you'd need to inline everything upfront.

### Setup

```csharp
builder.Services.AddMentorAgent(options =>
{
    // ...AppName and ChatClient as in Minimal setup — MentorAgent refuses to start without them
    options.EnableSkills   = true;
    options.SkillsFolder   = "Skills"; // relative to content root, or absolute path
    options.ScanAssemblies = [typeof(Program).Assembly];
});
```

### Dual-role skill class (knowledge + tools)

This is the most powerful pattern. The class provides **both** the domain instructions and the executable tools:

```csharp
// Register in DI — required when the class has methods
builder.Services.AddScoped<ExpenseReportSkill>();
```

```csharp
[MentorSkill(
    Name             = "expense-report",
    Description      = "Handles expense report filing, validation, and policy checks",
    InstructionsFile = "Skills/expense-report/SKILL.md")]   // or use Instructions = "..."
public class ExpenseReportSkill
{
    private readonly ExpenseRepository _repo;
    public ExpenseReportSkill(ExpenseRepository repo) => _repo = repo;

    // ── Methods become L1 tools — always registered on the coordinator ──────

    [Description("Submits a new expense report for the current user")]
    public async Task<string> SubmitExpenseReportAsync(
        string category, decimal amount, string notes) { ... }

    [MentorAction(
        Description          = "Approves a pending expense report",
        RequiresConfirmation = true,
        RequiredRoles        = ["Manager"])]
    public async Task<bool> ApproveExpenseReportAsync(int reportId) { ... }

    [Description("Returns the reimbursement policy for a given expense category")]
    public string GetPolicyForCategory(string category) { ... }
}
```

The flow when a user asks *"Submit a €47 expense for today's business lunch"*:

1. AI sees `submit_expense_report` in its tool list — the trailing `Async` is stripped, see [Tool names](#tool-names--the-rule-you-need-before-configuring-anything-else)
2. Before calling it, AI calls `load_skill("expense-report")` — learns that meals have a €50 limit, require a category and description
3. AI calls `submit_expense_report("Meals", 47, "Business lunch")` with policy-correct parameters

Without the skill, the AI guesses. **With the skill, the AI knows the rules before it acts.**

### Knowledge-only skill (no methods)

Use this when the skill provides guidance but all execution is handled by existing service tools:

```csharp
// No DI registration needed — no methods, no instance required
[MentorSkill(
    Name        = "refund-policy",
    Description = "Return and refund rules for the online store",
    Instructions = """
        ## Refund Policy
        - Items can be returned within 30 days of purchase.
        - Digital products are non-refundable once downloaded.
        - Damaged items: customer submits a photo via the support ticket tool.
        - Refunds are processed within 5–7 business days to the original payment method.
        """)]
public class RefundPolicySkill { }
```

### File-based skills (auto-discovery)

MentorAgent also discovers skills automatically from the filesystem — no class needed:

```
Skills/
  expense-report/
    SKILL.md          ← required: skill instructions in markdown
    policy.md         ← optional: resource accessible via read_skill_resource
    examples.md       ← optional: additional resource
  refund-policy/
    SKILL.md
  shipping-rules/
    SKILL.md
    carrier-list.txt
```

`SKILL.md` format — the first non-empty line after a heading becomes the catalogue description (only that line — keep it to one sentence on one line; without one, the folder name is used):

```markdown
# Expense Report Filing

Handles expense report submission, validation, approval workflow, and policy enforcement.

## Eligible Expenses
- Meals: up to €50/day domestic, €80/day international
- Travel: economy class only for flights under 4 hours
...
```

> **Class-based overrides file-based** — if both define a skill with the same name (case-insensitive), the class attribute wins, and it wins **whole**: a class registration carries no resources, so the folder's side-car files (`policy.md`, `examples.md` above) are no longer reachable through `read_skill_resource`. To keep resources, let the folder be the skill — put the methods in a class without a `[MentorSkill]` of the same name — or move the resource content into the instructions.

### How the AI sees skills

System prompt (catalogue only — always injected, ~100 tokens total):

```
## Available Skills
When a request requires specialised expertise in any area below,
call load_skill with the skill name to get full instructions before responding:
- **expense-report**: Handles expense report filing, validation, and policy checks
- **refund-policy**: Return and refund rules for the online store
- **shipping-rules**: Shipping carrier rules and delivery SLAs
```

Full instructions and resources are **never in the prompt by default** — only loaded when the AI decides it needs them.

### `[MentorSkill]` parameters

| Parameter | Description |
|---|---|
| `Name` | Unique skill name in kebab-case (e.g. `"expense-report"`). Key for `load_skill` |
| `Description` | One-sentence description shown to the AI in the skill catalogue |
| `InstructionsFile` | Path to a markdown file (absolute or relative to content root). When omitted, MentorAgent reads `{SkillsFolder}/{Name}/SKILL.md`. When that file is missing, or the path you gave does not exist (logged as a warning), the instructions are just the skill's name and `Description` |
| `Instructions` | Inline markdown text. Takes precedence over `InstructionsFile` |

### Agent Skills configuration options

| Option | Type | Default | Description |
|---|---|---|---|
| `EnableSkills` | `bool` | `false` | Enables skill discovery and the `load_skill` / `read_skill_resource` tools |
| `SkillsFolder` | `string` | `"Skills"` | Folder to scan for file-based skills. Relative to content root or absolute |
| `SkillSources` | `IList<AgentSkillsSource>` | empty | Additional sources, using the Agent Framework's own abstraction — see below |
| `SkillFilter` | `Func<AgentSkill, bool>?` | `null` | Keeps or drops a skill contributed by `SkillSources`. Does not apply to your own skills |
| `SkillsRefreshInterval` | `TimeSpan?` | `null` | Shares one list from `SkillSources` across every session in the process (wrapped in the framework's `CachingAgentSkillsSource`) and re-fetches it when a session is built after this long. Without it, every new session (circuit, hub connection, SSE request) re-reads the sources when its coordinator is built and keeps that list for its lifetime. Set it for a remote or expensive source |

### Skills from somewhere else — `AgentSkillsSource`

Skills do not have to come from your `Skills/` folder or from a `[MentorSkill]` class. Register any
Agent Framework `AgentSkillsSource` and what it yields reaches the assistant through the *same*
`load_skill` / `read_skill_resource` tools, the same catalogue prompt and the same gate:

```csharp
options.EnableSkills = true;
options.SkillSources =
[
    // Microsoft's own file source — skills written to the Agent Skills specification
    new AgentFileSkillsSource("/srv/shared-skills"),

    // …or your own: a database, a remote catalogue, a per-tenant store
    new MyDatabaseSkillsSource(connectionString),
];

// Optional: keep only what this deployment can actually run
options.SkillFilter = skill => skill.Frontmatter.Compatibility is null
                            || skill.Frontmatter.Compatibility.Contains("v2");

// Optional: fetch a remote catalogue once and share it across sessions, re-fetching it when a session
// starts after 15 minutes. Without it, every new session re-reads the sources when it starts.
options.SkillsRefreshInterval = TimeSpan.FromMinutes(15);
```

The list is composed with the framework's own decorators — `AggregatingAgentSkillsSource` preserves
your order, `FilteringAgentSkillsSource` applies `SkillFilter`, `DeduplicatingAgentSkillsSource`
keeps the first occurrence of a name, and `CachingAgentSkillsSource` is added when you set a
refresh interval. With the interval, that composition is held once per process and shared by every
session; a session keeps the list it started with, so a refresh reaches sessions built after it.

> **Your own skills always win a name collision.** Sources are consulted *after* MentorAgent's own
> discovery, and a duplicate name is skipped with a warning naming it. A source can add a
> capability; it cannot silently replace one of yours.

> **`AgentFileSkillsSource` reads a different dialect, and that is why it did not replace ours.**
> The framework's source validates each `SKILL.md` for **YAML frontmatter**; MentorAgent's
> convention is a heading followed by a prose paragraph, with frontmatter before the heading
> ignored. Both work — keep `SkillsFolder` for yours and register the framework's source for
> specification-compliant ones.

> **Requires a model.** The framework's `AgentSkillsSourceContext` demands an agent, and skills are
> discovered before the coordinator exists — so MentorAgent uses `Agent` when you supplied one and
> otherwise builds a plain agent over `ChatClient`. With neither set, registered sources are skipped
> with a warning and your own skills are unaffected.

---

## Contextual memory

Enable the Mentor's ability to remember preferences and past actions across sessions:

```csharp
options.UseMemoryContext  = true;
options.MemoryContextCount = 10; // last N memories injected into the system prompt

// Who a visitor who has not signed in is. The default gives each circuit/connection its own key,
// so one visitor's facts are never recalled for the next and one visitor cannot spend everybody's
// RateLimitPerUser allowance. A single-user application (MAUI, a desktop tool) wants the opposite:
// Shared puts every session on the literal key "anonymous", so with a persistent IMentorMemoryStore
// its memory survives a restart (the default RAM store loses everything on restart, whatever the key).
options.AnonymousIdentity = MentorAnonymousIdentity.PerSession;   // default
```

By default, memories are stored in RAM (`InMemoryMentorMemoryStore`). For real persistence, register your own store **before** `AddMentorAgent()`:

```csharp
// Redis
builder.Services.AddSingleton<IMentorMemoryStore, RedisMemoryStore>();

// EF Core
builder.Services.AddScoped<IMentorMemoryStore, EfCoreMemoryStore>();

// MongoDB
builder.Services.AddSingleton<IMentorMemoryStore, MongoMemoryStore>();

builder.Services.AddMentorAgent(options => {
	options.UseMemoryContext = true;
	...
});
```

> ⚠️ **User isolation:** With authentication configured, each user has their own memory (keyed by `ClaimTypes.NameIdentifier`, else `sub`, `oid` or the name). Without authentication the key depends on `AnonymousIdentity`. With the default, `PerSession`, every circuit, SignalR connection or app run gets its own opaque key: a visitor's facts are never recalled for anyone else, and they are not recalled on that visitor's next session either. `MentorAnonymousIdentity.Shared` puts every unauthenticated session on the literal key `"anonymous"`. That is right for a single-user app (MAUI, a desktop tool) and a cross-user leak on a website. A user known by name alone (Windows authentication) is keyed `name:<name>` since 1.0.0-rc.15: memories an earlier version stored under the bare name are not found until you rename their key.

### The `IMentorMemoryStore` contract

Five methods, all keyed on `userId` — there is no cross-user query, by design:

| Method | Contract |
|---|---|
| `SaveAsync(userId, key, value, ct)` | Upsert. Called on every captured fact, so make it idempotent on `(userId, key)` |
| `GetAsync(userId, key, ct)` | The value, or `null` when absent. Absence is not an error |
| `GetRecentAsync(userId, count, ct)` | The most recent `count` entries, **newest first**. This is what gets injected into the prompt |
| `DeleteAsync(userId, key, ct)` | Remove one entry. **No built-in tool calls this** — it exists for your own code, e.g. a "forget this preference" control in a settings page |
| `ClearAsync(userId, ct)` | Remove everything for that user. Backs the `forget_all` tool, and is your GDPR erasure hook |

`GetRecentAsync` returns `MemoryEntry` — `record MemoryEntry(string UserId, string Key, string Value, DateTime SavedAt)`.

> It lives in the **`Novior.MentorAgent.Data`** namespace, not `Novior.MentorAgent.Models` where the neighbouring
> types are. Add `using Novior.MentorAgent.Data;` to your implementation file.

A complete EF Core store:

```csharp
using Novior.MentorAgent.Data;      // MemoryEntry
using Novior.MentorAgent.Memory;    // IMentorMemoryStore

public sealed class EfCoreMemoryStore(AppDbContext db) : IMentorMemoryStore
{
    public async Task SaveAsync(string userId, string key, string value, CancellationToken ct = default)
    {
        var row = await db.Memories.FirstOrDefaultAsync(m => m.UserId == userId && m.Key == key, ct);
        if (row is null) db.Memories.Add(new MemoryRow(userId, key, value, DateTime.UtcNow));
        else { row.Value = value; row.SavedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetAsync(string userId, string key, CancellationToken ct = default)
        => (await db.Memories.FirstOrDefaultAsync(m => m.UserId == userId && m.Key == key, ct))?.Value;

    public async Task<IReadOnlyList<MemoryEntry>> GetRecentAsync(
        string userId, int count = 10, CancellationToken ct = default)
        => await db.Memories
                   .Where(m => m.UserId == userId)
                   .OrderByDescending(m => m.SavedAt)      // newest first — the order matters
                   .Take(count)
                   .Select(m => new MemoryEntry(m.UserId, m.Key, m.Value, m.SavedAt))
                   .ToListAsync(ct);

    public async Task DeleteAsync(string userId, string key, CancellationToken ct = default)
        => await db.Memories.Where(m => m.UserId == userId && m.Key == key).ExecuteDeleteAsync(ct);

    public async Task ClearAsync(string userId, CancellationToken ct = default)
        => await db.Memories.Where(m => m.UserId == userId).ExecuteDeleteAsync(ct);
}
```

Register it **before** `AddMentorAgent()` — registration order is what decides whether your store or
the built-in RAM one wins. Scoped is fine for EF Core; use singleton for Redis or Mongo clients.

### How automatic memory works

When `UseMemoryContext = true`, MentorAgent captures durable facts **reliably** — without depending on the model proactively calling a tool (which weaker models do inconsistently, e.g. saving on *"Mi chiamo Antonio"* but not on *"Ciao, mi chiamo Antonio"*).

- **Path A (a `ChatClient` is configured) — default:** after each user message a dedicated, minimal LLM call (`MemoryAutoCapture`, on by default) extracts durable personal facts and stores them directly. Because it is a separate focused task, it is robust regardless of how the coordinator replies. On this path the now-redundant `remember` tool and its forceful prompt block are **dropped** (fewer tokens per call); `forget_all` stays. Set `options.MemoryAutoCapture = false` to opt out — one fewer model call per message, but facts are then saved less reliably via the `remember` tool.
- **Path B (a pre-built `Agent`):** with a `ClassifierChatClient`, auto-capture runs through it — one small call per message — and the `remember` tool is dropped, as on Path A. Without one, auto-capture cannot run and the `remember` tool is the writer: its description asks the model to call it (the prompt block is Path A's; on Path B the agent's instructions are yours).

Examples of what gets saved automatically:

| User says | Saved as |
|---|---|
| `"My name is Antonio"` | `user_name = Antonio` |
| `"I work in the sales team"` | `user_team = sales team` |
| `"Always show me Pending orders first"` | `preferred_filter = Pending` |

Verify in the logs at **Debug** level (e.g. `"Logging": { "LogLevel": { "Novior.MentorAgent": "Debug" } }`): `[MentorAgent:Memory] Auto-capture saved 1 fact(s): user_name`. A failed extraction is logged as a warning (`[MentorAgent:Memory] Auto-capture failed — skipped.`) and never breaks the turn.

At the start of each session, stored memories are injected into the prompt so the AI greets the user by name and honours preferences immediately. With `MemoryRelevanceFiltering` (requires `EmbeddingGenerator`) only the memories **semantically** relevant to the current message are injected — identity/preference facts always kept.

### `InMemoryMentorMemoryStore` — production limitations

The default store has two hard limitations:
- **Data is lost on app restart** — memories do not survive deployments.
- **Does not scale across multiple instances** — in a load-balanced environment each server has its own isolated dictionary.

For any production deployment with more than one server instance, or where persistence across restarts is required, register a custom `IMentorMemoryStore` implementation.

---

## RAG — Retrieval-Augmented Generation

RAG enriches every AI response with documents retrieved from your vector store. Before the LLM call, MentorAgent searches for the most relevant documents and injects them into the coordinator's system prompt — grounding the AI's answers in your actual data.

### Setup

**Step 1** — Register your RAG source **before** `AddMentorAgent()`:

```csharp
// Implement IMentorRagSource with your preferred vector DB
builder.Services.AddScoped<IMentorRagSource, MyVectorDbRagSource>();

// Register exactly ONE implementation. IMentorRagSource is resolved as a single service,
// so with several registrations the last one wins. Alternatives:
// Azure AI Search
// builder.Services.AddScoped<IMentorRagSource, AzureSearchRagSource>();

// Qdrant
// builder.Services.AddScoped<IMentorRagSource, QdrantRagSource>();

// Any custom implementation
// builder.Services.AddScoped<IMentorRagSource, MyCustomRagSource>();

// A Microsoft.Extensions.VectorData collection needs no source of its own:
// AddMentorVectorStoreRag, see "A vector store source" below
```

**Step 2** — Enable RAG in options:

```csharp
builder.Services.AddMentorAgent(options =>
{
	options.UseRag                 = true;
	options.RagResultCount         = 5;    // number of documents to inject
	options.RagMinScore            = 0.5f; // optional: overrides the source's DefaultMinScore (see below)
	options.RagSystemPromptTemplate = "Use the following documents to answer:\n{documents}";
	options.ShowRagSources         = true; // show citation chips below AI messages
	options.RagNoResultsNotice     = true; // optional: tell the model when no document was found
});
```

### Implement `IMentorRagSource`

```csharp
public class MyVectorDbRagSource : IMentorRagSource
{
	private readonly MyVectorDb _db;
	public MyVectorDbRagSource(MyVectorDb db) => _db = db;

	// Cosine similarity is in [0, 1]: declare a threshold on that scale (0.5 loose … 0.75 strict).
	// Without it the RagMinScore default, 2, suits keyword scores and discards every document here.
	public float? DefaultMinScore => 0.6f;

	public async Task<IReadOnlyList<MentorRagResult>> SearchAsync(
		string query, int maxResults, CancellationToken ct = default)
	{
		var hits = await _db.SearchAsync(query, maxResults, ct);
		return hits.Select(h => new MentorRagResult(
			Content:   h.Text,
			SourceUrl: h.Url,
			Title:     h.Title,
			Score:     h.Score)).ToList();
	}
}
```

**Which threshold applies:** `RagMinScore` when you set it, in code or in configuration; otherwise the source's `DefaultMinScore`; otherwise `RagMinScore`'s default, `2`, which suits keyword sources whose scores are integer-like. A source with scores in [0, 1] that declares nothing has every document discarded, and a warning says so once, with the highest score it saw and where the threshold came from.

### Who is asking, and what the documents say about themselves

MentorAgent calls `SearchAsync(MentorRagRequest request, CancellationToken ct)`. Its default implementation calls the `(query, maxResults)` overload above, so a source written for that one needs no change. Implement it when the search depends on more than the text:

| `MentorRagRequest` | What it holds |
|---|---|
| `Query`, `MaxResults` | The user's message and `RagResultCount` |
| `RecentMessages` | The conversation before this message, oldest first: the user's messages and the assistant's replies of the last three turns of **this** conversation, text only. Empty on the first turn, and it starts again after a reset or a restore. Over SSE every `/mentor/chat` request is a conversation of its own, so it is always empty there |
| `User` | The caller of the hub, of `/mentor/chat` or of an A2A request (Novior.MentorAgent.Server), and in a Blazor circuit the user the host's `AuthenticationStateProvider` reports. `null` when the host has no authentication at all; an anonymous visitor, or a peer agent that did not authenticate, has an unauthenticated principal. Filter restricted documents on it |
| `PageName`, `PageData` | What the current page set with `IMentorPageContext.SetPageName` and `Set` |
| `TenantId` | The tenant of the turn, as `IMentorTenant.Id` names it; `null` without an `IMentorTenantResolver`. MentorAgent passes it and filters nothing: a source that holds several tenants' documents returns this one's. A source that implements only the `(query, maxResults)` overload never sees it, and a Warning says so at its first search with a tenant |

Each result can carry `Metadata`. The model sees it under the document's title, one `key: value` per entry, so it can tell the 2025 price list from the 2026 one. It does not stay with the model: it travels to the client with the citation chips (`metadata` in `RagSourcesReady`), and the model may repeat it in its answer. Put nothing in it that the user should not see.

```csharp
public async Task<IReadOnlyList<MentorRagResult>> SearchAsync(
	MentorRagRequest request, CancellationToken ct = default)
{
	var hits = await _db.SearchAsync(request.Query, request.MaxResults, VisibleTo(request.User), ct);
	return hits.Select(h => new MentorRagResult(h.Text, h.Url, h.Title, h.Score)
	{
		Metadata = new Dictionary<string, string> { ["valid from"] = h.ValidFrom.ToString("yyyy-MM-dd") },
	}).ToList();
}
```

The `(query, maxResults)` overload stays part of the interface, so a source that implements the request overload still implements that one too. Do not make each call the other: the search would never return.

### A vector store source

For a collection of `Microsoft.Extensions.VectorData` — pgvector, Qdrant, Azure AI Search, in memory and the others — you need no source of your own: register your store's connector, then `AddMentorVectorStoreRag` with the collection and what of a record is the document. The source is `MentorVectorStoreRagSource`, registered as scoped; call it before or after `AddMentorAgent`, and with `UseRag = true`. It is the application's one source: with another `IMentorRagSource` already registered it throws an `InvalidOperationException`.

```csharp
public sealed class Doc
{
	[VectorStoreKey]  public Guid Id { get; set; }
	[VectorStoreData] public string Text { get; set; } = "";
	[VectorStoreData] public string? Title { get; set; }
	[VectorStoreData] public string? Url { get; set; }
	[VectorStoreData] public string? Tenant { get; set; }
	[VectorStoreVector(1536, DistanceFunction = DistanceFunction.CosineDistance)]
	public ReadOnlyMemory<float> Vector { get; set; }
}

builder.Services.AddMentorVectorStoreRag(
	sp => sp.GetRequiredService<VectorStore>().GetCollection<Guid, Doc>("docs"),
	new MentorVectorStoreRagOptions<Doc>
	{
		Content   = d => d.Text,          // required: a record with no text is left out
		Title     = d => d.Title,
		SourceUrl = d => d.Url,
		Filter    = request => d => d.Tenant == request.TenantId,   // with a tenant resolver
	});
builder.Services.AddMentorAgent(options => { options.UseRag = true; /* EmbeddingGenerator = … */ });
```

| Option | What it does |
|---|---|
| `Content` | The text of the document, the part the model reads. Required |
| `Title`, `SourceUrl`, `Metadata` | The document's title, link and facts, in the prompt and on the citation chips as for any source |
| `Filter` | The records a request may see, as the collection's filter built from the `MentorRagRequest`: its `TenantId`, its `User`. Without it the whole collection is searched |
| `DistanceFunction` | The collection's distance function; without it, the one on the record's `[VectorStoreVector]` attribute |
| `VectorProperty` | The vector to search, for a record with more than one |
| `DefaultMinScore` | The threshold when the host sets no `RagMinScore`. Default 0.5 |
| `EmbeddingGenerator` | The model that embeds the message, when the documents were embedded with a model other than `MentorOptions.EmbeddingGenerator` |

**Scores are read as similarities**, higher is nearer, whatever the collection's distance function:

| Distance function of the collection | Score the source uses |
|---|---|
| `CosineSimilarity`, `DotProductSimilarity` | As it is |
| `CosineDistance`, the Postgres and pgvector default | 1 − distance |
| `NegativeDotProductSimilarity` | Its negation |

Without this, with pgvector `RagMinScore` would discard the nearest documents and keep the farthest. When neither the options nor the attribute name a function, or it is `EuclideanDistance`, a Manhattan or a Hamming distance, the registration fails at startup: each store has a default of its own, and those distances have no scale a threshold could be set on.

**The message's vector.** With `MentorOptions.EmbeddingGenerator`, the message is embedded once per turn and the source uses the same vector as routing, tool filtering and memory ([`IMentorQueryEmbedding`](#reusing-the-turns-embedding--imentorqueryembedding)), at no extra call. An `EmbeddingGenerator` in the options wins, and its vector too is computed once per turn, apart from the other model's. Without either, the collection receives the text and embeds it with its own generator.

**Tenants and failures.** With an `IMentorTenantResolver` and no `Filter`, the first search with a tenant is logged as a warning: it searches every tenant's documents. A collection that throws is handled like any source: a warning, the `mentoragent.rag_failures` counter, and the turn goes on without documents.

**PostgreSQL with pgvector.** Use `CommunityToolkit.VectorData.PgVector` (1.0.2): its `PostgresCollection<TKey, TRecord>` is the collection to pass. The table has to exist before the first search: create it once, with the connector's own method or with your migrations. Do not use `Microsoft.SemanticKernel.Connectors.PgVector` 1.74.0-preview: it is built on `Microsoft.Extensions.VectorData` 10.1, and with the 10.10 MentorAgent references every search fails with a `MissingMethodException`.

```csharp
// using CommunityToolkit.VectorData.PgVector;
var docs = new PostgresCollection<Guid, Doc>(connectionString, "docs", new PostgresCollectionOptions());

builder.Services.AddMentorVectorStoreRag(_ => docs, new MentorVectorStoreRagOptions<Doc> { Content = d => d.Text });
```

### When the conversation changes

A source that keeps state for a conversation — what it already showed, a running filter, a cursor — is told when the session's conversation changes: `OnConversationChangedAsync(MentorConversationChange change, CancellationToken ct)`, whose default does nothing. `change.Reason` is `Reset` (New conversation, `ResetSessionAsync`), `Restored` (a saved conversation took its place) or `Replaced` (a restored conversation the service no longer knew was replaced by a new one, BUG-110); `SessionId` and `TenantId` say whose. It is called with `UseRag` on, on the instance the session searches with: a scoped source clears its own state, a singleton keys it on `SessionId`. Keep it quick: it is bounded by `SafetyCheckTimeout`, and a source that throws or does not answer in time is logged as a warning while the conversation changes anyway.

```csharp
public sealed class MySessionSource : IMentorRagSource   // registered as scoped
{
	private readonly HashSet<string> _shown = [];

	public Task OnConversationChangedAsync(MentorConversationChange change, CancellationToken ct = default)
	{
		_shown.Clear();
		return Task.CompletedTask;
	}

	// SearchAsync(MentorRagRequest, …) and SearchAsync(string, int, …) as above
}
```

### Reusing the turn's embedding — `IMentorQueryEmbedding`

Routing, tool filtering and memory relevance already embed the current message once per turn. If your source searches by vector, ask for that vector instead of embedding the query again. The turn then makes one embedding round trip instead of two:

```csharp
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Rag;
using Microsoft.Extensions.AI;

public sealed class MyVectorRagSource(
	IMentorQueryEmbedding queryEmbedding,
	IEmbeddingGenerator<string, Embedding<float>> embeddings,
	MyVectorDb db) : IMentorRagSource
{
	public async Task<IReadOnlyList<MentorRagResult>> SearchAsync(
		string query, int maxResults, CancellationToken ct = default)
	{
		// Free when this same generator already embedded this message this turn; computed with it otherwise.
		var vector = await queryEmbedding.GetAsync(query, embeddings, ct);
		var hits   = await db.SearchAsync(vector, maxResults, ct);
		return hits.Select(h => new MentorRagResult(
			Content:   h.Text,
			SourceUrl: h.Url,
			Title:     h.Title,
			Score:     h.Score)).ToList();
	}
}
```

`IMentorQueryEmbedding` is registered (scoped) by `AddMentorAgent()`, and an empty text returns an empty vector without calling out.

To share the vector routing, tool filtering and memory already computed, pass the same instance you gave `MentorOptions.EmbeddingGenerator`: register one and use it for both, since it is compared by reference. A source whose documents were embedded with another model passes that model's generator and gets that model's vector, once per turn as well, never the other one's (BUG-229).

### RAG source citations in the widget

When `ShowRagSources = true`, citation chips appear below each AI message:

```
[AI response]
──────────────────────
📄 Product Manual v2  ↗
📄 Support FAQ        ↗
```

Chips are shown only for the documents the answer actually **cites**, not everything the search returned. MentorAgent numbers the injected documents and appends an instruction, in the configured `Language`, to cite them inline as `[1]`, `[2]`, … It adds this after your `RagSystemPromptTemplate`, so a custom template keeps it. When the reply is complete, each valid marker — `[1]`, a list such as `[1, 2]`, or a range such as `[1-3]` — becomes a chip for each document it names, in the order the answer cites them and once per document, linking to that document's `SourceUrl` and labelled with its `Title` (or the URL when there is no title). A number that names no document cites nothing, and neither does a range that ends on one. An answer that cites nothing, for example one from a skill or a tool, shows no chips, even when the search returned documents. With more than 3 cited sources, a "**+N more**" button collapses the rest.

### Architecture note

RAG is applied **only at the coordinator level**, not on L2/L3 specialist agents. The coordinator performs the vector search once per user message and receives the documents as per-call instructions. They are not written into the conversation. A delegated agent receives only the request text the coordinator passes to `route_to_specialist` (or to a team tool), so a specialist does **not** see the retrieved documents. If one needs your knowledge base, give it a tool that searches it. This avoids redundant searches and keeps costs low.

### RAG configuration options

| Option | Default | Description |
|---|---|---|
| `UseRag` | `false` | Enables RAG. Requires a registered `IMentorRagSource` |
| `RagResultCount` | `5` | Number of documents retrieved per query |
| `RagMinScore` | `2` | Minimum relevance score a document must reach to be injected into the prompt. Documents below this threshold are discarded. Scale depends on the `IMentorRagSource` implementation: for keyword search, `2` ≈ two content matches or one title match; for vector/cosine similarity, typical values are `0.5`–`0.75`. When you do not set it, the source's `DefaultMinScore` applies if it declares one. Set to `0` to disable filtering |
| `RagSystemPromptTemplate` | `"Use the following documents to answer:\n{documents}"` | Template injected into the system prompt. Use `{documents}` as placeholder |
| `ShowRagSources` | `false` | Show citation chips below AI messages in the widget |
| `RagNoResultsNotice` | `false` | When the search keeps no document — none returned, or every one below `RagMinScore` — the request tells the model so, in the language of the turn: a question the documents would cover is answered "not found in the documents" rather than from memory, and a greeting or an action as usual. Not after a search that failed, nor without a source |

> RAG is fully semantic: the vector search embeds every message and `RagMinScore` discards anything below the threshold, so a pure command like *"update the price of X"* simply retrieves nothing relevant and injects nothing — no keyword pre-gate needed, as long as `RagMinScore` is on your source's scale (see *Which threshold applies* above). With `RagNoResultsNotice` the model is also told when nothing was found, so a question the documents should answer is not answered from memory; the notice says that a message needing no document is answered as usual.
>
> A source that fails — it throws, or answers with no list — leaves the turn without documents: a Warning names the source, and with `EnableObservability` the failure is counted on `mentoragent.rag_failures`, with the source and the exception's type.
>
> The other side of it: the search runs on **every** message, before the model is called. Each message is one `SearchAsync` on your `IMentorRagSource`, with what it costs — an embedding call, the search, a reranker if your source has one — added to the time of every reply, commands included.

---

## MCP — Model Context Protocol

MentorAgent supports MCP in both directions: as a **client** (consuming tools from external MCP servers) and as a **server** (exposing `[MentorAction]` methods to any MCP-compatible client).

### MCP Client — consuming external MCP servers

External MCP servers (filesystem, databases, APIs, custom tools) become additional **Level-1 tools** for the coordinator — indistinguishable from local `[MentorAction]` methods.

Each server's tools must carry an `inputSchema`: from ModelContextProtocol 2.0 a tool without one is refused, and with it the server's whole tool list. The server is then left out with a warning — `Failed to connect to MCP server '<name>'` — whose exception names `inputSchema`, and the assistant goes on without it.

#### HTTP transport (remote server)

```csharp
builder.Services.AddMentorAgent(options =>
{
	options.McpServers = [
		new MentorMcpServer
		{
			Name      = "MyApiTools",
			ServerUrl = "https://mcp.example.com/mcp",
		}
	];
});
```

#### stdio transport (local process)

> **Windows note**: on Windows, `npx`, `uvx`, `python`, and other script launchers are `.cmd` or shell scripts that cannot be started directly by .NET's `Process.Start`. MentorAgent detects this automatically and wraps the command as `cmd.exe /c <command> <args>` — no changes needed in your configuration.

```csharp
options.McpServers = [
	new MentorMcpServer
	{
		Name      = "filesystem",
		Command   = "npx",
		Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
		AllowedTools         = ["read_file", "list_directory"], // null or empty = all tools
		RequiresConfirmation = true, // shows confirmation banner for every call

		// One child process for the whole application instead of one per visitor. Leave it unset
		// unless you know the server is stateless: a local MCP server can hold state for the person
		// who started it (@modelcontextprotocol/server-memory is a knowledge graph), and sharing
		// that would put every visitor in the same store. Servers reached over ServerUrl are shared
		// by default, because a remote endpoint is shared anyway.
		Shared = false,
	},
	new MentorMcpServer
	{
		Name      = "database",
		Command   = "my-db-mcp-server",
		Arguments = ["--connection-string", "Server=..."],
		EnvironmentVariables = new Dictionary<string, string>
		{
			["DB_PASS"] = builder.Configuration["DbPassword"]!
		}
	}
];
```

#### MCP status badge in the widget

When `ShowMcpStatus = true`, an MCP pill appears in the widget header. It shows the server's name when there is one server, or `online/total` when there are several, with a coloured dot for connecting / all online / some online / offline. Clicking it opens a detail bar that lists each server as connecting, online or offline.

```csharp
options.ShowMcpStatus = true; // default: false — recommended for development
```

**`MentorMcpServer` properties:**

| Property | Description |
|---|---|
| `Name` | Display name for the server |
| `ServerUrl` | HTTP/SSE endpoint URL (HTTP transport) |
| `Command` | Executable to launch (stdio transport, e.g. `"npx"`) |
| `Arguments` | Command-line arguments for the process |
| `EnvironmentVariables` | Environment variables injected into the process |
| `AllowedTools` | Whitelist of tool names to expose, compared without regard to case. `null` or an empty list = expose all |
| `RequiresConfirmation` | If `true`, every call to a tool from this server shows a confirmation banner |

---

### MCP Server — exposing MentorAgent as an MCP server

Every `[MentorAction]` method discovered by MentorAgent can be exposed as an **MCP tool**, allowing any MCP-compatible client (Claude Desktop, VS Code Copilot, custom agents) to call your application's business logic directly.

The MCP server is in the **`Novior.MentorAgent.Server`** package (`dotnet add package Novior.MentorAgent.Server`), also for a Blazor Server app: it needs ASP.NET Core, which `Novior.MentorAgent` itself does not reference, so that a MAUI app builds for every target. Up to 1.0.0-rc.14 it came with `MentorAgent` and `AddMentorAgent` registered it.

**Step 1** — Enable the MCP server in options, and register it:

```csharp
builder.Services.AddMentorAgent(options =>
{
	options.McpServerEnabled = true;
	options.AppName          = "MyApp"; // becomes the MCP server name
});
builder.Services.AddMentorAgentMcp();   // after AddMentorAgent — AddMentorAgentServer() already includes it
```

**Step 2** — Map the MCP endpoint in `Program.cs`:

```csharp
app.MapMentorAgentMcp();          // maps at options.McpServerPath (default: /mcp)
app.MapMentorAgentMcp("/my-mcp"); // custom path — a path passed here wins over McpServerPath
```

MCP clients can now connect to `https://yourapp/mcp` (or your `McpServerPath`) and discover all `[MentorAction]` tools.

The endpoint keeps the stateful Streamable HTTP transport of MentorAgent 1.0 and 1.1: `initialize` opens a session named by the `Mcp-Session-Id` header, and a `GET` without one answers 400. ModelContextProtocol 2.0 made servers stateless by default; MentorAgent keeps the sessions.

The endpoint is **open by default**: anyone who can reach the port can list and call every published tool. Gated ones are withheld (see Step 3). To require authentication, pass `configure`, which is applied to the mapped endpoint:

```csharp
app.MapMentorAgentMcp(configure: e => e.RequireAuthorization());
```

Requiring authorization decides who may connect. Which role-gated actions an authenticated caller may see is still decided by `McpCallerPrincipal` (Step 3).

Every `tools/call` goes through the same gates as a tool the model calls (BUG-107): `OnToolResult` runs on the result before the caller sees it — a hook that throws withholds it, and the caller gets an `isError` result — the call counts in the dashboard's top actions, and `RateLimitPerUser` bounds it in an allowance of its own, separate from chat messages. The caller is the principal `McpCallerPrincipal` resolves, else the request's user, counted by its `NameIdentifier`, `sub` or `oid` claim (else its name). A caller with none of them is counted by its remote address when the host registers `IHttpContextAccessor` (`builder.Services.AddHttpContextAccessor()`; behind a proxy, apply forwarded headers or every caller is the proxy); otherwise all such callers share one allowance, and one busy client can use it up for the others. Each call writes one Information line — tool, outcome, time, and a fingerprint of the caller, never the id itself. The outcome is `ok`, `error` (`OnToolResult` threw, and the caller got an error result), `failed` (the action threw), `cancelled`, `rate-limited`, `denied-role`, `withheld` (an action the server never publishes), `unknown` (a name nothing defines) or `denied-policy` (an [`IMentorActionPolicy`](#deciding-before-an-action-runs--imentoractionpolicy) does not allow the caller the tool); the caller gets the same answer for the last four. A policy's other answers are `blocked` (a `Stop`: the caller reads "Stopped by a rule:" and its reason), `nobody-to-confirm` (a `Confirm`: nobody can confirm a call here, and the caller is told to do it from the application) and `policy-failed` (it threw, or did not answer within `ActionPolicyTimeout`). An action that is off is logged as `disabled`, and the caller reads that it is an unknown tool; a governor that fails is logged as `governor-failed`, and the call is stopped.

**CORS.** The endpoint carries **no CORS policy**, and MCP clients need none: desktop and IDE clients, and the MCP Inspector's proxy, are not browsers. Steps 1 and 2 are all a host needs. Until rc.13 the endpoint carried a policy that allowed any origin, and on a pipeline without `app.UseCors()` every request failed with a 500 (BUG-108).

For a **browser-based** MCP client, opt in: call `app.UseCors()` after `UseRouting()` and before `UseAuthorization()`, apply the `MentorAgentMcp` policy that `AddMentorAgentMcp()` registers, and define that policy yourself — the one MentorAgent registers allows any origin, and it registers it only when you have not — with your origins and the session header exposed. Before or after `AddMentorAgentMcp()`, either works:

```csharp
builder.Services.AddCors(o => o.AddPolicy("MentorAgentMcp",
    p => p.WithOrigins("https://tools.example.com").AllowAnyHeader().AllowAnyMethod()
          .WithExposedHeaders("Mcp-Session-Id")));
// ...
app.UseCors();
app.MapMentorAgentMcp(configure: e => e.RequireCors("MentorAgentMcp"));
```

**Step 3 (optional)** — say **who** is calling, so role-gated actions can be published to callers
entitled to them.

By default nothing an author protected is published at all: an MCP request has no signed-in user to
check `RequiredRoles` against, so a gated action is simply withheld. That is the safe default and it
stays the default. If your host can genuinely identify the caller — an mTLS subject, a validated
bearer token, a gateway header you trust — give MentorAgent that principal:

```csharp
builder.Services.AddHttpContextAccessor();

builder.Services.AddMentorAgent(options =>
{
    options.McpServerEnabled = true;
});

// Resolves the caller behind an MCP request. A [MentorAction(RequiredRoles = ["Admin"])] is then
// published — and published ONLY — to a principal that is authenticated and holds it.
builder.Services.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
    (options, http) => options.McpCallerPrincipal = () => http.HttpContext?.User);
```

Roles are tested with `ClaimsPrincipal.IsInRole`, which reads the claim type the identity names as its
`RoleClaimType`, so a principal whose roles are in a `roles` claim works as well as one using
`ClaimTypes.Role` — once its identity says so. Microsoft.Identity.Web does; with JwtBearer and
`MapInboundClaims = false`, set `TokenValidationParameters.RoleClaimType = "roles"`, or `IsInRole` looks
for `ClaimTypes.Role` and every role-gated action stays withheld.
The resolver is called once per MCP request and its answer is never cached per caller: `tools/list`
and `tools/call` are decided for the caller of that request (BUG-103).

| Situation | Result |
|---|---|
| No `McpCallerPrincipal` (the default) | every gated action withheld |
| Principal holds one of the action's `RequiredRoles` (`IsInRole`) | published |
| Principal authenticated but lacks the role | withheld |
| Resolver returns `null`, an unauthenticated principal, or throws | withheld |
| An [`IMentorActionPolicy`](#deciding-before-an-action-runs--imentoractionpolicy) does not allow the caller the action | withheld from that caller |
| Action needs a **confirmation** (`RequiresConfirmation`, or `RequiresApproval` as it answers for that request) | **withheld, always** |

> The last row is the one to internalise. A role can be checked against a principal; a confirmation
> cannot be answered by one — there is no dialog on the far end of an MCP call and nobody watching
> it. This is an identity, not an override.

> ℹ️ The MCP server resolves each `[MentorAction]` class from a single DI scope that lives as long as the application. It does so once per process: the tools are built once, and which of them a caller sees is decided per request. The instance is then reused for every call from every MCP client. Whatever lifetime you register (Scoped, Transient or Singleton), the instance behaves as a long-lived shared object on this surface. Keep action classes stateless and thread-safe. Do not inject a `DbContext` directly: inject `IDbContextFactory<TContext>` (or `IServiceScopeFactory`) and create one per call.

---

## A2A — Agent-to-Agent

MentorAgent supports the **Agent-to-Agent (A2A) protocol** in both directions: as a **consumer** (calling remote A2A agents) and as a **server** (exposing MentorAgent as a federatable agent).

### A2A Consumer — calling remote A2A agents

Remote A2A agents participate in the **Handoff workflow** exactly like local `[MentorAgent]` classes. The coordinator can hand off to them; they return results to the coordinator when done. This lets you federate specialized agents deployed as separate services.

```csharp
builder.Services.AddMentorAgent(options =>
{
	options.RemoteAgents = [
		new MentorRemoteAgent
		{
			Name         = "InventoryAgent",
			Description  = "Manages warehouse stock and inventory levels",
			AgentCardUrl = "https://inventory.example.com",  // base URL only — SDK auto-appends /.well-known/agent-card.json
		},
		new MentorRemoteAgent
		{
			Name         = "BillingAgent",
			Description  = "Handles invoicing and payment processing",
			AgentCardUrl = "https://billing.example.com",    // base URL only
			Headers      = new Dictionary<string, string>
			{
				["Authorization"] = $"Bearer {builder.Configuration["BillingApiKey"]}"
			}
		}
	];
});
```

MentorAgent automatically resolves each agent's **Agent Card** (`/.well-known/agent-card.json`) when the first coordinator is built. That is the first message, or application start with `WarmUpAtStartup = true`. It then establishes an A2A connection and adds the agent to the Handoff graph. The card also decides how a request is sent (BUG-197): streamed when the card declares `streaming`, otherwise with `returnImmediately`, and the task is then read with `GetTask` until it ends — every quarter of a second at first, then up to every second (BUG-210). A peer that replies with a message, or with a task already finished, is not polled. Cards are cached once per process (15 minutes). A peer that cannot be reached is logged as a warning and is not retried for a minute; the backoff doubles on each failure, up to 5 minutes. A read that a Stop interrupts is not a failure: it is neither logged nor remembered, and the next message reads the card again and builds the coordinator with the agent (BUG-211). So it is dialled, and reported, at most once per window, not once per visitor. Until then it is simply absent from new sessions' workflows. Remote agents require Path A (a `ChatClient`). The coordinator's system prompt is automatically enriched with each remote agent's name and description, so the AI knows when to delegate. The coordinator can then delegate requests to remote agents using natural language — no extra configuration needed.

**`MentorRemoteAgent` properties:**

| Property | Description |
|---|---|
| `Name` | Display name used in the Handoff graph and in the coordinator's system prompt |
| `Description` | Human-readable description of what this agent does. Injected into the coordinator's system prompt so the AI knows when to route to it |
| `AgentCardUrl` | **Base URL** of the remote agent (e.g. `https://inventory.example.com`). The SDK automatically appends `/.well-known/agent-card.json` — do **NOT** include the path |
| `Headers` | Optional custom HTTP headers (auth tokens, API keys, etc.) injected into all A2A requests to this agent |

**Access control.** A remote agent has no per-agent role gate: any user of the host can reach it through `route_to_specialist`. To restrict it, authorise on the remote side — protect the peer's own `/a2a` endpoint and give its actions roles — or do not configure the agent on hosts whose users may not use it. (`RequiredRoles`, which was never enforced, was removed in 1.0.0-rc.13.)

**A Stop reaches the remote agent.** When the turn that delegated a request ends before the remote agent's answer — a Stop above all, but also a client that went away or a failure — the peer is told to cancel the task (`CancelTask`) instead of running it to the end (BUG-210). The task's id comes with the peer's first event, or with its reply to a `returnImmediately` request: a Stop that arrives before it keeps the request open up to 2 seconds more, for the id to arrive; if it does not, there is nothing to cancel the task by, and the peer finishes it. The cancel waits at most 2 seconds. A peer that does not take it costs a Debug line, not an error of the turn; one that does is logged as Information: `Task '<id>' of the remote agent '<Name>' cancelled: the turn that delegated it ended before its answer.` A MentorAgent peer stops a cancelled task as Stop would, so no action starts there once the cancel arrives (see *A task its caller cancels stops* under [A2A Server](#a2a-server--exposing-mentoragent-as-an-a2a-agent)).

**Whose answer is it?** On a host with `RemoteAgents`, every delegated answer reaches the coordinator with a note saying where it came from. The note is for the model, not for the user. Without it, a question addressed to a remote agent but answered by a local specialist would be presented as the remote system's data:

| What happened | What the coordinator is told |
|---|---|
| A remote agent answered | the answer is *that system's data, not this application's* |
| Both a remote agent and a local specialist answered | which part is the remote system's data and which is this application's, to be kept apart |
| A local specialist answered and no remote agent was named | *no remote agent was contacted*. If the user meant a remote agent, call `route_to_specialist` once more with `specialist` set to its name (the second call carries the name, so the retry is bounded) |
| `specialist` named a remote agent that never took part | `NOT <Name>'S ANSWER: …`, and a warning is logged: `route_to_specialist: <Name> was asked for and did not take part …` |

"Remote" covers every configured `MentorRemoteAgent`, including one whose card could not be fetched when the session was built. A host without remote agents, and a turn that arrived over A2A, see no note at all (BUG-082).

---

### A2A Server — exposing MentorAgent as an A2A agent

MentorAgent can expose itself as a fully compliant A2A agent, discoverable and callable by any A2A-compatible orchestrator.

The A2A agent is in the **`Novior.MentorAgent.Server`** package (`dotnet add package Novior.MentorAgent.Server`), also for a Blazor Server app, for the same reason as the MCP server above. Up to 1.0.0-rc.14 it came with `MentorAgent` and `AddMentorAgent` registered it. The A2A *client* — `RemoteAgents` — stays in `Novior.MentorAgent`.

**Step 1** — Enable the A2A server in options, and register it:

```csharp
builder.Services.AddMentorAgent(options =>
{
	options.A2AServerEnabled = true;
	options.AppName          = "MyApp";
	options.AppDescription   = "My Blazor AI assistant for order management";
	options.AgentCard        = new AgentCardInfo { Version = "2.0.0" };
});
builder.Services.AddMentorAgentA2A();   // after AddMentorAgent — AddMentorAgentServer() already includes it
```

**Step 2** — Map the A2A endpoints in `Program.cs`:

```csharp
app.MapMentorAgentA2A();          // maps at options.A2AServerPath (default: /a2a)
app.MapMentorAgentA2A("/agent");  // custom path — a path passed here wins over A2AServerPath
```

This registers two endpoints:
- `GET /.well-known/agent-card.json` — **Agent Card**, always at this path whatever the task path is: name, description, version, the provider/icon/documentation fields of `AgentCard`, one `JSONRPC` interface at `A2AServerUrl` (or the bare path when it is unset), the application's **ungated** `[MentorAction]`s of classes registered in DI as skills, one per tool name (actions with `RequiredRoles`, `RequiresConfirmation` or matched by `RequiresApproval` are left out, and so is a name that one of them shares: the card is the same for every caller, and nobody answers a confirmation), the input/output modes (`text/plain`, plus `image/png` in with `EnableImageInput`) and `streaming: true`
- `POST /a2a` (or `A2AServerPath`, or the path you pass) — **A2A task handler** (JSON-RPC binding) that receives messages, routes them through `MentorOrchestrator`, and returns the complete reply when the turn ends

Remote orchestrators can now discover this agent via `https://yourapp/.well-known/agent-card.json` and send A2A tasks to it.

**A task its caller cancels stops.** A caller that cancels a task (`CancelTask`) stops its turn as Stop would: no action starts after the cancel, a call in progress reaches the [observers](#auditing-the-gates-decisions--imentoractionobserver) as `Stopped` (`stop`), and the task ends `Canceled`. That holds however the task was asked for: streamed (`SendStreamingMessage`, as a MentorAgent caller asks), with `returnImmediately`, or waiting for the answer (`SendMessage`). The cancelled turn reports nothing more — no answer, no failure, no second attempt for an answer from tools. `MapMentorAgentA2A()` writes a Warning when it cannot wrap the A2A SDK's request handler, for example with a version of `A2A.AspNetCore` that registers it another way: a cancelled task still ends `Canceled`, but its call in progress reads `session-ended` instead of `stop`.

> **Protect the endpoints.** By default nothing is required: `/a2a` runs a full, paid turn for whoever can reach it. `MapMentorAgentA2A` takes a `configure` callback that is applied to **both** endpoints it maps, the task handler and the agent card:
>
> ```csharp
> app.MapMentorAgentA2A(configure: e => e.RequireAuthorization());
> ```
>
> A MentorAgent caller then authenticates through `MentorRemoteAgent.Headers` (e.g. `["Authorization"] = "Bearer …"`). The headers are sent with the card request as well as with every task.
>
> A task runs as the request's user: signed in when the endpoint requires authentication, anonymous otherwise. An anonymous task is keyed by `AnonymousIdentity`. With the default, `PerSession`, each task gets its own key, so `RateLimitPerUser` does not bound a caller across tasks; with `Shared`, every anonymous caller spends one allowance.

> **A request that arrives over A2A is answered here — it is never passed on to another peer.** When a host is both a server and a consumer (`A2AServerEnabled` **and** `RemoteAgents`), a turn served through `/a2a` is *not* offered the host's remote agents: they are left out of its handoff workflow, its prompt and the `route_to_specialist` description, and a Debug line says so. The host's own specialists, declarative agents, teams and tools still serve it. This is what lets two deployments be each other's remote agent without passing one question back and forth forever (BUG-080, found live between two samples that do exactly that). Chaining A → B → C through a MentorAgent host is therefore not supported in 1.0 — point A at C directly.

> **A request served over A2A answers from tools, or says it cannot.** The caller is a program: it cannot tell a looked-up figure from an invented one. So a turn served through `/a2a` is told to call the tool *first* and to state only what a tool returned in that turn. If it ends without calling any tool that returns data — a navigation, a UI action, a memory write and a skill do not count — the reply is discarded and the same request is put once more with a tool call **required**; if that attempt too returns no data and the classifier model judges that the reply states something about the application's records (a value, or that an entry does not exist or has no such value), the caller receives `NOT GROUNDED: …` instead. A reply that states no data — "this application cannot provide it", a policy read from a skill or your documents, even one with numbers in it — is returned as it is. Two attempts at most; the extra turn is paid only by requests answered without data (BUG-081, BUG-106). A MentorAgent caller also sends the name *it* has for this application as message metadata (`mentoragent.addressedAs`), so "how many products does InventoryAgent have?" is understood as a question about *this* application; only a single-token name is accepted from the wire (BUG-081).

> ⚠️ **Set `A2AServerUrl`** when other A2A clients will call this agent. Set it to the full public URL of your A2A endpoint **including the path** (e.g. `http://localhost:5001/a2a`, or `…/agent` if you mapped it there): the card publishes it verbatim, so a base URL without the path sends callers to the wrong endpoint. Without it, the Agent Card's `SupportedInterfaces` contains only the mapped relative path (`/a2a`). A MentorAgent consumer resolves that against the card's base URL and still works. A client that builds the endpoint with `new Uri(url)`, as the A2A SDK's client factory does, fails with `UriFormatException`.
>
> ```csharp
> options.A2AServerUrl = "https://myapp.example.com/a2a";
> ```

**`AgentCardInfo` properties:**

| Property | Default | Description |
|---|---|---|
| `Version` | `"1.0.0"` | Agent version string exposed in the Agent Card |
| `IconUrl` | `null` | URL to the agent's icon image |
| `Provider` | `null` | Provider or organization name |
| `DocumentationUrl` | `null` | URL to the agent's documentation |
| `Tags` | `null` | Reserved. The A2A card has no top-level tags field — the spec puts tags on individual skills — so this is **not** published today |

```csharp
options.A2AServerEnabled = true;
options.A2AServerUrl     = "https://shopflow.example.com/a2a";   // absolute — see the trap below

options.AgentCard = new AgentCardInfo
{
    Version          = "2.1.0",
    Provider         = "ShopFlow S.p.A.",
    IconUrl          = "https://shopflow.example.com/assets/agent-icon.png",
    DocumentationUrl = "https://docs.shopflow.example.com/agent",

    // Set it if you like, but do not expect to read it back: it is not on the published card.
    Tags             = ["ecommerce", "orders"],
};
```

Everything except `Tags` appears in `/.well-known/agent-card.json`. The card's audience is another
machine, so a wrong field is discovered by nobody until an integration fails — which is also why
`A2AServerUrl` matters: leave it unset and the card advertises a **bare path**, still valid, still
served, and useless to any client that does not already share your host.


> **How a task is served:** `MentorAgentA2AHandler` submits the task and marks it *Working* ("Processing..."), then runs the request through `MentorOrchestrator.SendMessageAsync` in a DI scope of its own: a fresh coordinator and session, so nothing carries over from earlier tasks. The caller is read from the request when the task starts, before it is submitted: a caller answered at once (`returnImmediately`) keeps its identity, and its roles, for the whole turn, although its request has ended (BUG-226). It collects the streamed chunks and returns the **complete** reply in one final message (`TaskUpdater.CompleteAsync`); the caller does not receive a token stream. A turn that throws is closed **failed** with `Error: <message>`. A turn that produced no words is also closed **failed** ("this agent could not produce a reply to the request") with a warning in the log, never as an empty completed task, which a MentorAgent caller used to read as "not delegated". The turn has no browser circuit, no current page and no UI actions. Since rc.11 a Blazor Server host serves `/a2a` as well as a headless one does: in rc.10 every task it received completed empty (BUG-083).

---

## Persistent conversation history

By default, conversation history lives in memory and is lost on app restart.

```csharp
// CosmosDB
options.ChatHistoryProvider = new CosmosChatHistoryProvider(cosmosClient, "my-db", "conversations");

// Custom (implement ChatHistoryProvider from Microsoft Agent Framework)
options.ChatHistoryProvider = new MyRedisChatHistoryProvider(redisConnection);
```

> ### ⚠️ One provider instance is shared by every user
>
> `MentorOptions` is a **singleton**, so an object assigned to `ChatHistoryProvider` serves every
> circuit, every SignalR connection and every user in the process. The default is the opposite:
> leave both options null and MentorAgent builds a fresh `InMemoryChatHistoryProvider` **inside each
> coordinator**, isolated by construction. Setting the instance property therefore *inverts* the
> isolation model — silently.
>
> The natural implementation is the dangerous one:
>
> ```csharp
> // ❌ one list, no notion of a user — every user is served every other user's transcript
> public sealed class MyHistoryProvider : ChatHistoryProvider
> {
>     private readonly List<ChatMessage> _history = [];
>     // …
> }
> options.ChatHistoryProvider = new MyHistoryProvider();
> ```
>
> Use **`ChatHistoryProviderFactory`** instead. It is invoked once per coordinator, which is the
> lifetime the default already has, and it receives that coordinator's `IServiceProvider` so the
> provider can key its storage by whatever identifies the caller in your application:
>
> ```csharp
> // ✅ in-memory, isolated because each scope gets its own instance
> options.ChatHistoryProviderFactory = _ => new InMemoryChatHistoryProvider();
>
> // ✅ shared backing store, partitioned by the caller
> options.ChatHistoryProviderFactory = sp =>
>     new CosmosChatHistoryProvider(cosmosClient, "my-db", "conversations",
>         partitionKey: sp.GetRequiredService<IUserContext>().UserId);
> ```
>
> Keep `ChatHistoryProvider` only when the host has a single user (a desktop app), or when the
> provider partitions its own storage internally. Setting both stops the host when it starts (in a MAUI
> app, the first time MentorAgent is used) rather than picking one for you.
>
> **Keep the transcript in the session, not in a field.** The provider is built once per coordinator
> and kept for the whole circuit or connection — across **new conversations** (`ResetSessionAsync`,
> the widget's reset button, the hub's `ResetSession`) and across restored sessions. A provider that
> stores messages in a field carries the previous conversation into the one the user has just started
> (BUG-102). Store them in the session's `StateBag`, as `InMemoryChatHistoryProvider` does;
> the Agent Framework's `ProviderSessionState<T>` does the bookkeeping:
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
> A provider backed by an external store does the same with the key of its rows: keep it in the
> session (a conversation id in the `StateBag`), not in the provider.

> **What your provider does not get.** `MaxSessionMessages` and `ChatReducer` trim only the
> built-in in-memory history. A provider returned by `ChatHistoryProviderFactory` (or assigned to
> `ChatHistoryProvider`) is used as it is, so give it a reducer yourself if the session must stay
> bounded — the same one MentorAgent uses by default (a `ChatReducer` set next to your provider is
> not used, and a warning says so):
>
> ```csharp
> using Novior.MentorAgent.Core;   // MentorChatReducer
>
> options.ChatHistoryProviderFactory = _ => new InMemoryChatHistoryProvider(
>     new InMemoryChatHistoryProviderOptions { ChatReducer = new MentorChatReducer(50) });
> ```
>
> With `UseServiceManagedHistory = true` no local provider is installed at all: the service owns
> the conversation, and a configured `ChatHistoryProvider` is ignored with a warning.

### Recipe: the history in PostgreSQL

A provider that keeps every conversation in a PostgreSQL table, so it outlives a reload of the page and a restart of the application. It follows the rules above:

- **Registered with `ChatHistoryProviderFactory`**, one provider per coordinator. A scope with nobody behind it — an A2A task, a call to the MCP server, the start-up warm-up — keeps the built-in in-memory history.
- **Each row is keyed by its owner and its conversation.** The owner is whoever the application knows the caller as: the signed-in user, or a browser cookie in an application without sign-in. The conversation is a `Guid` kept in the session's `StateBag`, not in a field, so that **New conversation opens a new one**: the first session of a circuit takes up the owner's latest conversation (that is the reload and the restart), and every later session without a key gets a new id.
- **The message is stored as `text`, not `jsonb`.** It is the JSON of `AIJsonUtilities.DefaultOptions`, and `jsonb` reorders the keys of every object: the `$type` of a polymorphic `AIContent` is no longer the first property, and the message cannot be read back.
- **The reducer is the provider's own**: every message is stored, and the model is sent what `MentorChatReducer` keeps, as with the built-in history.

```sql
CREATE TABLE IF NOT EXISTS public.mentor_chat_history (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    owner_key       text        NOT NULL,
    conversation_id uuid        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    message         text        NOT NULL   -- not jsonb
);
CREATE INDEX IF NOT EXISTS mentor_chat_history_conversation ON public.mentor_chat_history (conversation_id, id);
CREATE INDEX IF NOT EXISTS mentor_chat_history_owner ON public.mentor_chat_history (owner_key, id DESC);
```

```csharp
options.ChatHistoryProviderFactory = sp =>
{
    var reducer = new MentorChatReducer(50);
    return sp.GetService<HistoryOwner>()?.Key is { Length: > 0 } owner
        ? new PostgresChatHistoryProvider(sp.GetRequiredService<PostgresChatHistoryStore>(), owner, reducer)
        : new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions { ChatReducer = reducer });
};

public sealed class PostgresChatHistoryProvider(PostgresChatHistoryStore store, string owner, IChatReducer reducer)
    : ChatHistoryProvider
{
    private readonly ProviderSessionState<Conversation> _conversation =
        new(_ => new Conversation(), nameof(PostgresChatHistoryProvider), AIJsonUtilities.DefaultOptions);
    private int _resumed;   // the circuit's first session takes up the owner's latest conversation

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken ct = default)
        => await reducer.ReduceAsync(await store.LoadAsync(owner, await IdAsync(context.Session!, ct), ct), ct);

    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken ct = default)
    {
        var id = await IdAsync(context.Session!, ct);
        // save context.RequestMessages and context.ResponseMessages in your table, under (owner, id)
    }

    private async Task<Guid> IdAsync(AgentSession session, CancellationToken ct)
    {
        var state = _conversation.GetOrInitializeState(session);
        if (state.Id is { } id) return id;
        Guid? latest = null;
        if (Interlocked.Exchange(ref _resumed, 1) == 0)
        {
            // read the owner's latest conversation id from your table into latest
        }
        state.Id = latest ?? Guid.NewGuid();   // New conversation: a session with no id yet gets a new one
        _conversation.SaveState(session, state);
        return state.Id.Value;
    }

    public sealed class Conversation { public Guid? Id { get; set; } }
}
```

`PostgresChatHistoryStore` is your data access (Npgsql): it reads a conversation's rows in order and deserializes each `message`, inserts the turn's messages, each serialized with `AIJsonUtilities.DefaultOptions`, and finds the conversation of the owner's newest row; the sample's `PostgresChatHistoryStore` has the whole code. The source sample of the samples repository runs this recipe on PostgreSQL, with the knowledge base on pgvector, when it is given a connection string.

### How the built-in history is trimmed

The in-memory history keeps at most `MaxSessionMessages` messages (default 50) and trims them by
**whole turns**, oldest first. A turn is a question and everything that answered it: tool calls,
tool results, approvals and the reply. Tool calls and results count like any other message and stay
in the history, so on the next turn the model still sees the data a tool read — "and the oldest
one?" is answered from that result, not by guessing or by calling the tool again. A tool result
never reaches the model without the call that produced it. When the newest turn alone is over the
limit, its question and its most recent messages are kept.

Up to 1.0.0-rc.14 the history was trimmed by the framework's `MessageCountingChatReducer`, which
drops every tool call and result. Keeping them costs tokens: in a lab run of 84 turns the turns
answered correctly went from 85% to 95%, and tokens grew by about 9%. With the same
`MaxSessionMessages` a session also holds fewer turns, because tool messages now count.

To go back, or to use another reducer (a summarizing one, for example), set `ChatReducer`;
`MaxSessionMessages` is then not read:

```csharp
#pragma warning disable MEAI001 // MessageCountingChatReducer is experimental
options.ChatReducer = new MessageCountingChatReducer(50);   // the behaviour up to rc.14
#pragma warning restore MEAI001
```

One reducer instance serves every session, so it must not keep per-conversation state.

See the [Agent Framework documentation](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/storage) for available providers.

---

## Built-in AI tools

MentorAgent automatically registers internal tools on the Coordinator. The developer does **not** declare or register them — they activate based on configuration.

### Always-active tools

| Tool | When it is used | Notes |
|---|---|---|
| `navigate_to` | User asks to go to a page, or an agent needs to navigate before executing UI actions | Discovers pages from `[MentorPage]` at startup; waits for `SignalReady()` if `HasUIActions = true` and the page runs in the turn's process — a Blazor Server circuit or a MAUI Blazor Hybrid app (hub, SSE and A2A turns do not wait) |
| `route_to_specialist` | Coordinator delegates a request to a Level-2 agent, an agent from an `IMentorAgentSource` (e.g. a declarative YAML agent) or a remote A2A agent | Auto-registered on **Path A** (a `ChatClient` is configured) when at least one `[MentorAgent]`, agent-source agent or `MentorRemoteAgent` is available. Signature `route_to_specialist(request, specialist?)`: `specialist` carries the target agent's name when the user named one, and the Handoff Workflow's router picks otherwise. It returns what the specialists said, as text, or `NOT DELEGATED` / `DELEGATION FAILED` (see [Level 2](#level-2--specialized-agent-with-handoff)). On a host with remote agents every result opens with a note saying whose data it is, or `NOT <Name>'S ANSWER` (BUG-082). A turn that arrived over A2A is never offered the remote agents (BUG-080) |
| `{action_name}` | AI invokes a UI action registered via `PageContext.RegisterUIAction*()` | **One tool per action** — each has its own name, description, and parameter schema. Injected dynamically per-call by `UIActionsMiddleware`. Only visible when the page is mounted. *(Path A only)* |
| `invoke_ui_action` | Generic UI action dispatcher (legacy) | Used only on **Path B** (pre-built `AIAgent`). On Path A (`ChatClient`), each action is its own named tool — this dispatcher is not added. |

### Conditional tools (activated by options)

| Tool | Activated by | Notes |
|---|---|---|
| `remember` | `UseMemoryContext = true` **and** auto-capture is not running (`MemoryAutoCapture = false`, or Path B with neither `ChatClient` nor `ClassifierChatClient`) | AI saves a user preference or fact silently. While auto-capture runs — by default on Path A, and on Path B with a `ClassifierChatClient` — the tool is not registered: facts are extracted by a separate call after each message |
| `forget_all` | `UseMemoryContext = true` | User explicitly asks to reset their memory |
| `load_skill` | `EnableSkills = true` | Returns full markdown instructions for the requested skill |
| `read_skill_resource` | `EnableSkills = true` + at least one skill has resource files | Returns a specific resource file attached to a skill |

---

## Streaming responses

All AI responses are **streamed token by token**, with no waiting for the full response. The Orchestrator handles this automatically via `RunStreamingAsync`, and the developer does not need to configure anything. The one exception is output moderation. With `EnableOutputSafetyCheck` or a custom `OutputGuardrail`, the reply is buffered and shown only after it has been checked, so no unmoderated text is ever streamed.

Response chunks flow through `IMentorStateService.OnStreamingChunk` → `ChatWidget` → UI thread-safe update via `InvokeAsync(StateHasChanged)`. When [voice output](#voice-input-and-output) is enabled, the same chunks feed the speech queue, so the assistant speaks along with the text instead of reading it back at the end.

### Stop button

While the AI is processing, a **■ Stop** button appears above the chat input. Clicking it cancels the current request immediately — the `CancellationToken` propagated through the entire agent pipeline (RAG, LLM call, tool execution) is cancelled, and any partial response already streamed is kept in the chat.

This works for all request types: standard messages, team deliberations (L3), and post-confirmation responses. A Stop that comes after a confirmation, before the action has started, stops the action too (BUG-204). A request delegated to a remote agent is cancelled on the peer as well — see *A Stop reaches the remote agent* under [A2A Consumer](#a2a-consumer--calling-remote-a2a-agents) (BUG-210).

---

## Blazor Server vs Blazor WASM

MentorAgent registers its core services as **Scoped**, which behaves differently depending on the hosting model:

| Service | Blazor Web App (Server, Auto) | Blazor Hybrid (MAUI) |
|---|---|---|
| `MentorOrchestrator` | One per circuit | One per app instance |
| `IMentorSessionManager` | One per circuit | One per app instance |
| `IMentorPageContext` | One per circuit | One per app instance |
| `MentorMemoryService` | One per circuit | One per app instance |
| `IMentorMemoryStore` | Singleton (shared) | Singleton (shared) |
| `MentorDiscoveryService` | Singleton (shared) | Singleton (shared) |
| `MentorRateLimiter` | Singleton (shared) | Singleton (shared) |

### Supported render modes

| Template / Render mode | Support | Notes |
|---|---|---|
| **Blazor Web App — Server** | ✅ | Default. `AddMentorAgent()` only, no extra packages. |
| **Blazor Web App — Auto** | ✅ | Option A: `@rendermode="InteractiveServer"` on `<ChatWidget />` (simplest), with the template's *Interactivity location: Per page/component*. Option B: add `Novior.MentorAgent.Server` + `Novior.MentorAgent.Blazor` for full WASM support — the one for *Global*. |
| **Blazor Web App — WebAssembly** | ✅ | Via `Novior.MentorAgent.Server` (server project) + `Novior.MentorAgent.Blazor` (client project). Option A of *Auto* works too once the server project adds Server interactivity (see below). |
| **Blazor WASM Standalone** | ✅ | Via `Novior.MentorAgent.Server` (API backend) + `Novior.MentorAgent.Blazor` (WASM project). |
| **Blazor Hybrid (MAUI)** | ✅ | `Novior.MentorAgent` alone, on Android and Windows (iOS and Mac Catalyst not checked). The template needs the widget's static assets in `index.html`; setup in four steps in [Blazor Hybrid (MAUI)](#-blazor-hybrid-maui). |
| **React / Vue / Angular / mobile** | ✅ | Via `Novior.MentorAgent.Server` — connect using SignalR or SSE. |

---

### ✅ Blazor Web App — Blazor Server (recommended)

The simplest setup. `ChatWidget` and `MentorOrchestrator` run in the same server process, communicating via in-memory C# events. No extra packages needed.

```csharp
// Program.cs
builder.Services.AddMentorAgent(options => { ... });
```
```razor
@* MainLayout.razor *@
@using Novior.MentorAgent.Abstractions.Components
<ChatWidget />
```

---

### ✅ Blazor Web App — Auto render mode (simplest WASM option)

In Blazor Auto, the easiest approach is to pin `<ChatWidget />` to `InteractiveServer` — it stays server-side while the rest of the app can be WASM. No new packages needed.

```razor
@* MainLayout.razor — server project *@
@using Novior.MentorAgent.Abstractions.Components

<ChatWidget @rendermode="InteractiveServer" />
```

```csharp
// Program.cs — server project only
builder.Services.AddMentorAgent(options => { ... });
```

> ⚠️ Never call `AddMentorAgent()` in the **client** (WASM) project — only in the server project.

This option needs a layout rendered by the server, so it works with the template's **Interactivity location: Per page/component**:

- **Global** (`<Routes @rendermode="InteractiveAuto" />`): the layout itself runs under Auto, and once it runs in WebAssembly it cannot host a server component — `NotSupportedException: … render mode 'InteractiveServerRenderMode' is not supported by WebAssembly rendering`. Use the full WASM setup below.
- **WebAssembly template** (`dotnet new blazor -int WebAssembly`): the server project registers no Server interactivity, and every page fails with HTTP 500 (*the required endpoints are not mapped on the server*). Add both calls:

```csharp
// Program.cs — server project of the WebAssembly template
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()        // add
    .AddInteractiveWebAssemblyComponents();

// after builder.Build()
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()        // add
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(MyApp.Client._Imports).Assembly);
```

**The CSS and JS.** Prerendered — the default — the widget writes its `<link>` and `<script>` into the page itself. With `@rendermode="new InteractiveServerRenderMode(prerender: false)"` it cannot: add the two tags of step 1 below to the `<head>` of `App.razor`, or the widget renders unstyled.

---

### ✅ Blazor WASM / Blazor Auto (full WASM) — via Novior.MentorAgent.Server + Novior.MentorAgent.Blazor

For fully client-side WASM rendering or non-Blazor frontends, install the companion packages:

```bash
# Server project — MentorAgent is included automatically as a transitive dependency
dotnet add package Novior.MentorAgent.Server

# Client project (WASM)
dotnet add package Novior.MentorAgent.Blazor
```

```csharp
// Server/Program.cs
builder.Services.AddMentorAgent(options => { ... });
builder.Services.AddMentorAgentServer();
app.MapMentorAgentServer();   // /mentor-hub + /mentor/chat
```

```csharp
// Client/Program.cs
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });   // a Blazor Web App's client has none
builder.Services.AddMentorAgentBlazor(options =>
{
    options.HubUrl   = "/mentor-hub";
    options.BotName  = "My Assistant";
    options.Language = MentorLanguage.English;
});
```

```razor
@* Client Razor page — identical to Blazor Server *@
@using Novior.MentorAgent.Abstractions.Components
<ChatWidget />
```

> ⚠️ **Three WASM-only setup steps** not required in Blazor Server:
> 1. **CSS/JS links** — in a standalone WASM app the static `index.html` is served before the .NET runtime starts, so the widget cannot inject its own assets. A Blazor Web App has no `index.html`: prerendered, the widget writes the tags itself; without prerendering they go in `App.razor`. Where you add them, add `<link href="_content/Novior.MentorAgent.Abstractions/css/MentorAgent.css?v=1.3.0" rel="stylesheet" />` and `<script src="_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js?v=1.3.0"></script>` manually — **set the `?v=` to the version of the package you install and change it on every upgrade**, because nothing fingerprints these two and a cached older `MentorAgent.js` silently costs you the onboarding tour and streaming voice.
> 2. **CORS + absolute `HubUrl`** when the WASM app and the server are on different origins (different ports). Confirmations and Stop (`POST /mentor/approve`, `/mentor/cancel`) then go to the hub's origin with the `AccessTokenProvider` token, through the `HttpClient` the client registers (step 3). See the [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) and [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) READMEs.
> 3. **An `HttpClient` in the client project** — the widget sends Stop and confirmations through it, and `AddMentorAgentBlazor()` does not register one. The standalone WebAssembly template does; the client project of a Blazor Web App does not, and without it the WebAssembly runtime does not start (`CannotResolveService … HttpClient`). Add `builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });` to its `Program.cs`, as in the snippet above.

How it works: `MentorOrchestrator` runs on the server; the WASM widget connects via SignalR. Credentials never reach the browser. All features (agents, RAG, MCP, A2A, memory, HITL) are fully supported.

See [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) and [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) for complete documentation.

---

### ✅ Blazor Hybrid (MAUI)

A `BlazorWebView` is interactive by construction, so `<ChatWidget />` takes **no** `@rendermode`
here — and the whole of `Novior.MentorAgent` runs in-process, exactly as it does on Blazor Server. There is
no circuit, no HTTP request and no ASP.NET Core principal; what that costs you is at the bottom of
this section.

**Targets.** Android and Windows build: on 1.0.0-rc.15 the MAUI Blazor Hybrid template, unchanged,
builds `net10.0-android` with `MentorAgent` and signs its APK, and the MAUI sample runs on
`net10.0-windows10.0.19041.0`. iOS and Mac Catalyst build only on a Mac and have not been checked;
nothing in the package is specific to a platform. Up to 1.0.0-rc.14 every target but Windows failed
with `NETSDK1082` ("no runtime pack for Microsoft.AspNetCore.App … 'android-arm64'"): the package
brought ASP.NET Core in through its MCP and A2A server dependencies, which now live in
`Novior.MentorAgent.Server`.

**1 · Add the package.** The template builds as generated. (Up to 1.0.0-rc.14 its
`Microsoft.Extensions.Logging.Debug 10.0.0` pin had to be raised to `10.0.1` first, or restore failed
with `NU1605`: `Microsoft.Extensions.Hosting` came in with the same server dependencies.)

```xml
<!-- in the <ItemGroup> the MAUI template generated -->
<PackageReference Include="Novior.MentorAgent" Version="1.3.0" />
```

**2 · Reference the widget's static assets.** A MAUI app serves `wwwroot/index.html` itself, and the
template does not know about the package's assets:

```html
<!-- wwwroot/index.html -->
<link rel="stylesheet" href="_content/Novior.MentorAgent.Abstractions/css/MentorAgent.css" />
...
<script src="_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js"></script>
```

**3 · Configuration has no content root.** There is no `appsettings.json` on disk at runtime, so
embed it and read it back. `AddJsonStream` comes with the template; `AddUserSecrets` needs a package
that neither the template nor `Novior.MentorAgent` brings:

```bash
dotnet add package Microsoft.Extensions.Configuration.UserSecrets
```

```xml
<EmbeddedResource Include="appsettings.json" LogicalName="MyApp.appsettings.json" />
```

```csharp
// MauiProgram.cs — at the top of the file
using System.Reflection;                  // Assembly
using Microsoft.Extensions.Configuration; // AddJsonStream, AddUserSecrets

// in CreateMauiApp, after MauiApp.CreateBuilder()
using var stream = Assembly.GetExecutingAssembly()
    .GetManifestResourceStream("MyApp.appsettings.json");
if (stream is not null) builder.Configuration.AddJsonStream(stream);

#if DEBUG
// keeps the API key out of the app package; App: any class of the app that is not static
builder.Configuration.AddUserSecrets<App>();
#endif
```

`AddUserSecrets` reads the secrets of the project's `UserSecretsId` (`dotnet user-secrets init` adds one);
without one it adds nothing and throws nothing. The secrets file is on the development machine, so the app
finds it when it runs there, as with the Windows target, and not on a phone or an emulator.

**4 · Register as usual, and add the widget to the layout:**

```csharp
// MauiProgram.cs — after builder.Services.AddMauiBlazorWebView();
builder.Services.AddMentorAgent(options =>
{
    options.AppName = "My App";
    options.ChatClient = azure.GetChatClient("gpt-4.1").AsIChatClient();
    options.ScanAssemblies = [typeof(MauiProgram).Assembly];

    // One installation, one user. Without this, every app run gets a new anonymous key
    // and nothing remembered is recalled after a restart.
    options.AnonymousIdentity = MentorAnonymousIdentity.Shared;
});
```

```razor
@* Components/Layout/MainLayout.razor — no @rendermode: a BlazorWebView is already interactive *@
@using Novior.MentorAgent.Abstractions.Components
<ChatWidget />
```

> **What is different on Hybrid, and is not a bug.**
> * **`RequiredRoles` fails closed for everyone.** The gate reads an ASP.NET Core `ClaimsPrincipal`
>   and a desktop app has none, so a role-gated action is not offered to the model, and a call to it is
>   refused rather than run unchecked. If your
>   app signs users in itself, bridge your own principal before relying on the gate.
> * **Memory is per app run unless you say otherwise.** There is no principal, so the memory key
>   comes from `AnonymousIdentity`. The default, `PerSession`, gives every app run a new key, so facts
>   saved today are not recalled tomorrow. Set `options.AnonymousIdentity = MentorAnonymousIdentity.Shared`
>   (one installation, one user) **and** register a persistent `IMentorMemoryStore`: the built-in RAM
>   store loses everything on restart, whatever the key.
> * **Scoped services live for the whole app**, not for a circuit — see the lifetime table above.
>   One `MentorOrchestrator`, one conversation, until the app restarts.
> * **The dashboard and the MCP/A2A *server* endpoints need a web host** and are not available —
>   they are in `Novior.MentorAgent.Server`, which a MAUI app does not reference. MCP and A2A *clients*
>   (`McpServers`, `RemoteAgents`) work normally.

---

### ✅ Non-Blazor frontends (React, Vue, Angular, MAUI, mobile)

Install `Novior.MentorAgent.Server` on your ASP.NET Core backend and connect any client via SignalR (`@microsoft/signalr`) or plain HTTP SSE (`/mentor/chat`).

See [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) for complete documentation and examples.

---

## Session serialize and restore

`IMentorOrchestrator` exposes two methods for saving and resuming the conversation state (e.g. across page reloads or server restarts):

```csharp
@inject IMentorOrchestrator Mentor

// Save the current conversation (on the server, or in ProtectedLocalStorage — see below)
JsonElement? snapshot = await Mentor.SerializeSessionAsync();

// Resume it later — on a reconnect, or after a server restart
if (snapshot.HasValue)
    await Mentor.RestoreSessionAsync(snapshot.Value);
```

`SerializeSessionAsync` returns `null` when there is no conversation yet, which is **not** the same as an empty snapshot: store the `null` and you overwrite a good saved conversation with nothing. It also does not build the coordinator, so calling it on every page unload costs nothing on pages where the widget was never opened.

`RestoreSessionAsync` throws `ArgumentException` if the snapshot is not one of ours, was written by an incompatible version, or was saved by a host that keeps its history another way — on the service (Responses API with `UseServiceManagedHistory`) versus locally, or under another history provider — and **leaves the current conversation untouched** when it does. Snapshots carry a version marker and, since rc.14, how the history is kept: a stale entry from an older deployment, or a snapshot that would restore an empty history or fail every turn after it, is refused loudly instead of silently replacing what the user is in the middle of (BUG-110). If the first turn after restoring a service-managed snapshot fails because the service no longer knows the conversation — it answers that the previous response or the conversation was not found — a new conversation starts and the user is told. Any other failure on that turn (a content filter, an image the provider cannot download, a context grown too long, a missing deployment, a 429 or a 5xx) leaves the restored conversation in place.

The snapshot this method returns is the **conversation in clear**. It is the host's to keep: store it on the server, or in `ProtectedLocalStorage`, never as plain text a user can read or edit — a restored snapshot is history the model trusts. The HTTP endpoint in `Novior.MentorAgent.Server` hands clients a protected, user-bound form instead.

> The lower-level `IMentorSessionManager` is registered too, but every method on it takes the coordinator `AIAgent`, which is deliberately not handed out to application code — running it directly would bypass the guardrails, the rate limiter, the role gate and the confirmation gate. Use the orchestrator.
>
> On Blazor **WebAssembly** both methods throw `NotSupportedException`: the `AgentSession` lives in the server's DI scope. Save and restore it over HTTP, with `GET`/`POST /mentor/session?connectionId=` (see the Novior.MentorAgent.Blazor README).

The widget's **reset button** (inside the chat header) calls `ResetSessionAsync()` and clears the message list — starting a brand new conversation.

---

## Security

### Input safety check

```csharp
options.EnableSafetyCheck  = true;                            // one classifier call before each turn (0.7–2 s measured on gpt-4.1)
options.SafetyCheckTimeout = TimeSpan.FromSeconds(15);        // default; raise it for a local model, TimeSpan.Zero = no limit
options.SafetyCheckFailure = MentorSafetyCheckFailure.Allow;  // no verdict (timeout / error): Allow = fail open (default), Block = refuse
```

Before processing each message, the AI evaluates whether it is a prompt injection, jailbreak attempt, or an attempt to extract the system prompt or credentials. Works in any language automatically.

> **The classifier is told what your application is.** It receives the same scope the hosted-tool
> domain gate derives — `AppName`, `AppDescription` and your discovered page names — and is
> instructed that reading *your application's own business data* is safe **however broadly the
> request is phrased**. Without that context a classifier cannot tell exfiltration from ordinary use:
> "tell me everything about customer Mario Rossi" is the most common question a CRM ever receives,
> and it has exactly the shape of a data-extraction attempt. Your own role gates and
> `RequiresConfirmation` decide what a user may actually see — that is not the classifier's job, and
> it is told so.
>
> Give `AppDescription` a real sentence. It is what the classifier reasons against, and a blank one
> leaves it guessing.
>
> **It is also told what your application is equipped with.** The classifier prompt names the
> configured capabilities: MCP servers by name, image input, provider-hosted tools, skills, and memory
> when `UseMemoryContext` is on. Asking to *use* one of them ("list the files in Documents" on a host
> with a filesystem MCP server, "remember my code") is judged as ordinary use. With memory on, it is
> also told that what users say about themselves is theirs to give and ask back, not a system secret.
>
> **A refused message is not written to the log.** A refusal is often a false positive, and what a user
> typed does not belong in an operator log. The warning carries a 10-character fingerprint and the
> length instead (`Safety check FAILED (message fingerprint 3FA2…, 57 chars).`), so repeats of the
> same attempt can still be correlated.

### Rate limiting

```csharp
options.RateLimitPerUser    = 20;  // max 20 messages...
options.RateLimitWindowSecs = 60;  // ...per minute, per user
```

> ⚠️ **User identification:** With ASP.NET Core authentication configured, each user is identified by their `ClaimTypes.NameIdentifier` claim, else `sub`, `oid` or their name, and gets an independent counter. Without authentication the counter follows `AnonymousIdentity`. With the default, `PerSession`, each circuit or SignalR connection has its own counter, so one visitor cannot spend everybody's allowance. A reload or a new tab also starts a fresh one, though, so the limit is **no protection against a determined anonymous caller**. With `MentorAnonymousIdentity.Shared`, all unauthenticated sessions share the key `"anonymous"` and the limit applies globally across all browsers. For a real per-user limit, configure ASP.NET Core authentication.

### Role-based actions

```csharp
[MentorAction(Description = "Approves a budget request", RequiredRoles = ["Finance", "Admin"])]
public async Task<bool> ApproveBudgetAsync(int requestId) { ... }
```

Roles are verified with `IsInRole` on the current user: the caller of the hub, `/mentor/chat` or A2A (Novior.MentorAgent.Server), or in a Blazor circuit the user the host's `AuthenticationStateProvider` reports. For an action that also asks for confirmation, the roles are read before the banner and again after the user confirms, in either `HitlMode`: a role lost while the banner waits stops the action.

**What the model is offered.** The coordinator's model is offered an action with `RequiredRoles` only in a turn whose user is signed in and holds one of its roles — the rule of the gate and of the MCP server — and the prompt counts only the actions the user may run. It is decided at the start of every turn, reading the user once and only when some action requires a role, so a user who signs in during the session, or another user of the same session, gets what their own roles allow. Without an `AuthenticationStateProvider`, or with one that does not answer, the action is left out. On Path A it is left out of every request of the turn; on Path B, of the tools handed to your agent. A turn that leaves an action out appends a notice to the instructions of each of its requests, because without the tool a model can still claim the action was done (see [Taking a tool away is only half the job](#taking-a-tool-away-is-only-half-the-job)). The notice names no action and no role. It tells the model that every tool it was given in the request is available to this user, to be called when the user asks for what it does, and that other actions of the application are not available on this turn: they are not among its tools and will not become so. Only for a request none of its tools can do is the model to answer, in one sentence, that it cannot do it here, and it is never to write that an action was done unless a tool it called returned that it was. It is also told not to offer such an action or ask whether to do it, that a reply to an earlier message — a yes, a choice, a value — does not make it available, and never to call another of its tools in its place. The notice is written in the language of the turn, under an English heading (BUG-224). Told only that some actions were missing, a model refused one it had. The notice adds that signing in may make it available only when nobody is signed in and an action was left out for its roles (not only by a policy), the application registers an `AuthenticationStateProvider`, and the turn has a person who can sign in: the widget, the hub or `/mentor/chat`, not a task from another agent. Even then the model is told to say it only for a request none of its tools can do, never after a tool it called refused or stopped it. It travels in the instructions, never as a message — under `UseServiceManagedHistory` a message is stored and would follow the user into later turns — and on Path B it comes after your agent's own instructions and the context providers'. A turn that leaves nothing out sends the instructions unchanged, so the cached prefix of the prompt stays the same. So a user without the role, or not signed in, is not told which actions are left out or which role one needs: the model does not know them, and answers that it cannot do it here. A call it makes anyway — to an action from an earlier turn, or one it made up — is still refused by the gate. An [`IMentorActionPolicy`](#deciding-before-an-action-runs--imentoractionpolicy) leaves an action out the same way, whether or not it requires a role: one its `IsAllowedAsync` does not allow at `Offer` is not offered nor counted, with the same notice, and a call to it is refused (`Denied`, `not-authorized`). The agents under the coordinator — specialists, team members and the agents of an `IMentorAgentSource` — follow the same rule for roles: each is offered only the tools the turn's user may run, from the same reading of the user, in `Blocking` and in `Native` mode; a team member's instructions list only those tools, and a request that leaves one out ends with the same notice. An agent that holds no tool with `RequiredRoles` sends its requests as before. A policy does not leave their tools out: the gate asks it at every call.

**Roles from Windows and Active Directory groups.** `RequiredRoles` takes whatever `IsInRole` accepts for your sign-in, so a group works once the authentication makes it a role:

```csharp
// Windows authentication (Microsoft.AspNetCore.Authentication.Negotiate) on a Windows server:
// the user is a WindowsPrincipal, and IsInRole takes a group as DOMAIN\Group.
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();

[MentorAction(Description = "Approves a budget request", RequiredRoles = [@"CONTOSO\Finance"])]
public async Task<bool> ApproveBudgetAsync(int requestId) { ... }
```

- **Windows authentication on Windows:** name the group as `DOMAIN\Group`. On Linux, Negotiate knows the groups only through LDAP (`EnableLdap` on its options), which adds them as role claims: check the names it gives them before you write them in `RequiredRoles`.
- **Microsoft Entra ID** (Microsoft.Identity.Web): the `roles` claim holds the app roles. Assign the group to an app role in the app registration and name the app role in `RequiredRoles`. The `groups` claim holds the groups' object ids, not their names; to test it directly, set `TokenValidationParameters.RoleClaimType = "groups"` and write the ids in `RequiredRoles`.

### Confirmation dialogs

```csharp
[MentorAction(Description = "Deletes all orders for a customer", RequiresConfirmation = true)]
public async Task<bool> DeleteAllOrdersAsync(int customerId) { ... }
```

The widget shows a confirmation banner before executing the action. `options.RequireConfirmation = false` turns the banner off for your actions, MCP-client servers and `RequiresApproval`. It does not reach a [hosted MCP server](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp) that asks for approval (`RequireApproval`, `AlwaysRequireApprovalTools`): that request comes from the provider, and the banner still appears.

**Writes ask by default.** From 1.0.0-rc.15 a `[MentorAction]` that declares neither `ReadOnly = true` nor `RequiresConfirmation` is treated as an action that changes data and asks too (`RequireConfirmationForWrites`, on by default):

```csharp
[MentorAction(Description = "Marks an order as reviewed")]                                // asks
[MentorAction(Description = "Counts open orders", ReadOnly = true)]                      // only reads: never asks
[MentorAction(Description = "Adds a note to an order", RequiresConfirmation = false)]   // a write you chose to run unasked
```

`RequiresConfirmation` reads `false` on all three. To tell the first from the third outside the library, for example in a page that lists the application's actions for an administrator, read `RequiresConfirmationDeclared`: it is `true` only on the third, which writes `RequiresConfirmation`. It reports what the attribute says, not the confirmation the action gets, which also depends on `ReadOnly`, `IsReadOnlyAction` and `RequiresApproval`.

Like every confirmation-gated action, an action that asks is withheld from the MCP server and from the A2A agent card: nobody is there to confirm. Declare `RequiresConfirmation = false` on the ones that must stay available there, or turn the default off for the rc.14 behaviour:

```csharp
options.RequireConfirmationForWrites = false;   // only RequiresConfirmation = true asks, as up to rc.14
```

**The default covers `[MentorAction]` methods only**: a method marked with `[Description]` alone still asks for nothing and is still published on `/mcp`. Mark it `[MentorAction]` to have the default. A warning lists both kinds of write that run without confirmation: the `[MentorAction]` methods you opted out, and the `[Description]`-only methods not declared read-only with `IsReadOnlyAction`. A `[Description]` method cannot say that it only reads, so with `[Description]` alone the second list names your reads too: declare them with `options.IsReadOnlyAction`, or mark them `[MentorAction(ReadOnly = true)]`. The warning comes once, the first time MentorAgent reads your actions: at startup with `WarmUpAtStartup`, or when `MapMentorAgentA2A()` builds the agent card or `MapMentorAgentMcp()` maps the endpoint, otherwise with the first conversation or the first tour request.

**What the banner says.** The banner shows the action's description, then the arguments the model is about to pass, in the order of the method's parameters (for an MCP tool, in the model's order), each value cleaned of control and bidirectional characters and cut at 200 characters. To write it yourself — in your own words or language — set `ConfirmationMessage`:

```csharp
options.ConfirmationMessage = c => c.ToolName == "cancel_order"
    ? $"Cancel order {c.Arguments.First(a => a.Name == "orderId").Value}? Reason: {c.Arguments.First(a => a.Name == "reason").Value}"
    : null;   // null: the default text for this one
```

It gets the tool name, the description, the arguments as the default text shows them, the configured `Language`, the default text itself (`DefaultMessage`) and the services of the conversation's scope (`Services`, for an `IStringLocalizer` or your own data). The banner's title stays the localized "Confirmation required". When the function throws or returns only white space the default text is shown, with a warning: a banner never fails to appear because of it.

**A confirmation nobody answers.** By default a confirmation waits until it is answered, until the user presses Stop, or until the session ends (the circuit, the hub connection or the request closes): there is no time limit. `ConfirmationTimeout` sets one, counted from when the banner is shown. When nobody has answered by then, the confirmation counts as rejected and the action does not run; the model is told that nobody confirmed it in time, so nothing changed — not that the user said no — and that the user can ask again. An answer that comes later is ignored, as one after Stop is: `POST /mentor/approve` answers `404`. The banner goes when the turn ends. The limit holds in `Blocking` and in `Native` mode, for a confirmation an [`IMentorActionPolicy`](#deciding-before-an-action-runs--imentoractionpolicy) asks for, and for the actions of specialists and team members.

For a web host, a few minutes is a sensible value:

```csharp
options.ConfirmationTimeout = TimeSpan.FromMinutes(5);
```

MentorAgent does not read `MentorOptions` from configuration. To keep the value in `appsettings.json`, read it yourself — `options.ConfirmationTimeout = builder.Configuration.GetValue("MentorAgent:ConfirmationTimeout", TimeSpan.Zero);`, with `"00:05:00"` in the file.

While the banner waits, the turn keeps its request open — the hub call or `/mentor/chat`, with the model and the conversation's services. A user who closes the tab frees all of it, since the session ends; one who leaves the tab open and walks away keeps the turn busy for hours. A few minutes is enough for someone who reads the banner and decides, and closes a wait nobody will finish. Zero or a negative value, the default, means no limit.

**Who confirmed, inside the action.** In the method of an action that asks for confirmation,
`MentorActionConfirmation.Current` says who confirmed this call and when — for a ledger of the writes, for one:

```csharp
[MentorAction(Description = "Refunds a payment", RequiresConfirmation = true)]
public async Task<string> RefundPaymentAsync(int paymentId)
{
    var confirmation = MentorActionConfirmation.Current;   // null when nobody confirmed
    await _ledger.WriteAsync(paymentId,
        confirmation?.ConfirmedBy?.Identity?.Name, confirmation?.ConfirmedAt);
    return $"Payment {paymentId} refunded.";
}
```

It carries the confirmation's id (`ConfirmationId`), the tool (`ToolName`), when it was confirmed, in UTC
(`ConfirmedAt`), and who confirmed (`ConfirmedBy`): the caller of `POST /mentor/approve`; in a circuit and in the
MAUI app, the session's signed-in user; `null` when nobody is signed in. `Current` is `null` when nobody
confirmed the call: the action asks for no confirmation, an auto-approval rule let it run (`Native` mode), or
the MCP server called it. It is set only while the action runs, in `Blocking` and in `Native` mode alike, and
for the tools of specialists and team members too. `ConfirmationId` is the `ActionId` the banner carried, and
the `ConfirmationId` of the [gate's decisions](#auditing-the-gates-decisions--imentoractionobserver) about the
same call.

Tools you can't annotate (MCP, skills) are gated with `MentorMcpServer.RequiresConfirmation` or `options.RequiresApproval`, and the whole flow can run on the Agent Framework's native protocol — see [Human-in-the-loop tool approval](#human-in-the-loop-tool-approval).

### Telling people they are talking to an AI — `ShowAiDisclosure`

Article 50 of the EU AI Act (Regulation (EU) 2024/1689) asks, among other things, that people be told when they
interact with an AI system, and that AI-generated text be marked in a machine-readable way. MentorAgent gives you the
two tools for it. Whether and how the obligations apply to your application, and which words to show, are for you
to decide, with your own legal advice: the library does not make an application compliant.

**What MentorAgent gives.**

- **A notice that stays visible.** By default the widget shows a short notice right under its header, outside the
  messages: it stays there for the whole conversation, with the welcome panel and during the tour, does not scroll
  away and cannot be closed. The default text is translated into the ten languages of `Language`:

  > You are talking to an AI assistant: its replies are generated automatically.

  Set your own words, or turn the notice off:

  ```csharp
  options.AiDisclosureMessage = "You are talking to Aria, an AI assistant. A person can take over: write «operator».";
  options.ShowAiDisclosure    = false;   // no notice; the marker below stays
  ```

  `AiDisclosureMessage` is shown as you write it — as text, never as markup — in every language: give it in the
  language of your users. `null` or white space, the default, shows the translated text. Both options are on
  `MentorOptions`, and on `MentorAgentBlazorOptions` for the WebAssembly widget, as `WelcomeMessage` is; the widget
  reads them from `MentorWidgetOptions`.
- **A marker on every reply.** Each reply of the model carries a machine-readable marker, whether the notice is
  shown or not:
  - in the widget, the reply's bubble (`.bm-bubble`) has the attribute `data-ai-generated="true"`;
  - on `/mentor/chat`, the `chunk` and `completed` frames carry `"aiGenerated": true`;
  - on the hub, the event `ReplyStarted` comes once before every reply, with `{ "aiGenerated": true }`
    (`MentorReplyStarted` in .NET);
  - a reply served to another agent over A2A carries `"aiGenerated": true` in the message's `metadata`.

  The user's messages and the library's own error messages (the ones with ⚠) are not marked: they are not the
  model's words. A client of yours that shows the replies elsewhere, or copies them, can keep the marker.

**What stays with you.**

- **The words.** The default text is a starting point: choose the sentence that fits your application and your
  users, and have it checked.
- **Your own user interface.** With `IMentorStateService` or a client of your own, MentorAgent does not draw the
  notice: show it yourself, and keep the marker. A .NET UI reads the two options and the translated text as the
  widget does:

  ```csharp
  var widget = services.GetRequiredService<IOptions<MentorWidgetOptions>>().Value;
  if (widget.ShowAiDisclosure)
  {
      var text = string.IsNullOrWhiteSpace(widget.AiDisclosureMessage)
          ? services.GetRequiredService<MentorLocalizer>().Get("ai.disclosure")
          : widget.AiDisclosureMessage;
      // show text, always visible
  }
  ```

  A React, Angular, Vue or mobile client has no endpoint for the text: write it in your users' language, or take the
  default above. A hub client registers `ReplyStarted`, even with an empty handler: one that does not still works,
  but SignalR logs a Warning for every reply (see the Server README's protocol reference).
- **What MentorAgent does not mark.** The results of the MCP server, the voice of the widget's spoken replies, the
  images or files a provider's hosted tool generates; over A2A, the working message and the error messages; on
  `/mentor/chat`, the `cards`, `navigation`, `confirmationRequired` and `error` frames.
- **Everything else in the article.** Deep fakes, emotion recognition, biometric categorisation and text published
  to inform the public are your application's decisions, not the widget's.

### Deciding before an action runs — `IMentorActionPolicy`

Roles say who may run an action, and a confirmation asks the user first. A decision that depends on your own data —
a permission per company or per record, a rule on the arguments, the time of day — goes in an
`IMentorActionPolicy`: it decides, in your code, whether a user may use an action at all, and whether one call may
run, on every surface.

```csharp
builder.Services.AddMentorActionPolicy<CompanyPolicy>();

public sealed class CompanyPolicy : IMentorActionPolicy
{
    public async ValueTask<bool> IsAllowedAsync(MentorActionRequest request, CancellationToken ct)
    {
        var permissions = request.Services.GetRequiredService<IPermissionService>();   // scoped
        if (request.Stage == MentorActionStage.Offer)
            return await permissions.MayUseAsync(request.User, request.ToolName, ct);    // coarse: the company

        // Call: the record the model asks about. The value comes from the model: parse it, never trust it.
        return request.Arguments?.TryGetValue("orderId", out var id) == true
               && int.TryParse(id?.ToString(), out var orderId)
               && await permissions.MayTouchOrderAsync(request.User, orderId, ct);
    }

    public ValueTask<MentorActionVerdict> BeforeActionAsync(MentorActionRequest request, CancellationToken ct)
        => ValueTask.FromResult(request.ToolName == "issue_refund" && DateTime.UtcNow.Hour >= 22
            ? MentorActionVerdict.Stop("refunds are closed after 22:00")
            : MentorActionVerdict.Proceed);
}
```

`AddMentorActionPolicy<T>()` registers one (the same type twice registers it once), and
`AddMentorActionPolicy(instance)` one already built. A policy is a singleton; every policy registered is asked, in
the order of registration. Both methods have a default body, so a policy writes only the one it needs.

**`IsAllowedAsync` — may this user use this action?** `true` by default. It is asked with `Stage` set to
`MentorActionStage.Offer` when MentorAgent decides what to offer: at every turn, for each of the application's
actions the coordinator holds, and at every `tools/list` of the MCP server, for each tool it publishes. There is no
call yet, so `Arguments` and `CallId` are `null`. An action it does not allow is left out as one the user has no role
for (see [Role-based actions](#role-based-actions)): not offered, not counted in the prompt, with the same notice
that names nothing. It is asked again with `MentorActionStage.Call` before every call, with the arguments, and once
more after the user confirmed: a permission taken away while the banner waits stops the action, as a role does. A
call it does not allow is refused (`Denied`, `not-authorized`): the model reads that the user may not use this
action, and the log has one Warning.

**`BeforeActionAsync` — may this call run?** Asked before every call, once the roles and `IsAllowedAsync` have let
it through, it answers a `MentorActionVerdict`:

- `MentorActionVerdict.Proceed`, the default: no objection. It never removes a confirmation the action asks for; to
  approve a call without the banner there is [`AutoApprovalRules`](#approving-without-asking--autoapprovalrules), in
  `Native` mode.
- `MentorActionVerdict.Confirm(reason)`: ask the user first, even for an action that asks for no confirmation. Where
  nobody can confirm — the MCP server, a task from another agent — the call is refused.
- `MentorActionVerdict.Stop(reason)`: the call does not run. The model reads "Stopped by a rule:", in the configured
  language, followed by your reason — never that the user declined. The reason reaches the model and the
  [observers](#auditing-the-gates-decisions--imentoractionobserver): never put a secret in it.

A call is decided in this order: the roles, `IsAllowedAsync`, `BeforeActionAsync`, the confirmation — the action's
own, or the one a policy asked for — then the action. A call refused or stopped before the confirmation shows no
banner. With several policies, `IsAllowedAsync` stops at the first `false`; `BeforeActionAsync` asks every one, and
the strictest answer wins — `Stop`, then `Confirm`, then `Proceed` — with the reason of the first policy that gave it.

**A policy that fails stops the call.** Each call to a policy may take `ActionPolicyTimeout`, 5 seconds by default;
zero or a negative value means no limit, and Stop still cancels the wait:

```csharp
options.ActionPolicyTimeout = TimeSpan.FromSeconds(10);
```

A policy that throws, returns `null` or does not answer in time stops the call — the model reads "Stopped: the check
before this action could not be completed." — and, when MentorAgent decides what to offer, leaves the action out.
The log names the exception's type, never its message.

**`MentorActionRequest`** — what a policy is asked about:

| Property | What it holds |
|---|---|
| `Stage` | Whether MentorAgent is deciding what to offer (`Offer`) or a call is about to run (`Call`) |
| `ToolName`, `Description` | The tool's technical name, as on `MentorActionDecision`, and the description the model reads; `null` when it has none. The description is the `IMentorActionGovernor`'s when it gives one, except on a call to the MCP server, which carries the code's; a question to the governor itself carries the code's |
| `Arguments` | At `Call`, the arguments by parameter name, as the model or the MCP client sent them — in general a `JsonElement` each. **Nothing has checked them**: read them as you would read a request's body. `null` at `Offer` |
| `User` | The signed-in user, or `null` when nobody is; read for every action, those that require no role too. On the MCP server, the caller `McpCallerPrincipal` resolves, else the request's user |
| `Surface`, `AgentKind`, `AgentName` | Where the call comes from and which agent asks for it, as on `MentorActionDecision` |
| `SessionId`, `TurnId`, `CallId` | As on `MentorActionDecision`. `CallId` is `null` at `Offer`, and `TurnId` on the MCP server |
| `PageName` | The page the user is on, as it named itself with `IMentorPageContext.SetPageName`; `null` when no page did, on the MCP server and in a task from another agent |
| `RequiredRoles` | The roles the action requires, any one of which is enough; empty when it requires none. They are the `IMentorActionGovernor`'s when it gives them; a question to the governor itself carries the code's |
| `RequiresConfirmation` | Whether the action asks for a confirmation of its own — the attribute, `RequiresApproval` or an MCP server's `RequiresConfirmation` — with `RequireConfirmation` on; the `IMentorActionGovernor`'s answer when it gives one. A question to the governor itself carries the code's |
| `Services` | The services of the conversation's scope: the circuit, the hub connection, the request, the A2A task; on the MCP server, the request's. Never the root services: resolve a scoped service of yours there, a `DbContext` or the current tenant |

**Where policies are asked:**

| Surface | `IsAllowedAsync` at `Offer` | Before a call | A `Confirm` |
|---|---|---|---|
| The widget, the hub and `/mentor/chat`, in `Blocking` and in `Native` mode | At every turn | `IsAllowedAsync`, then `BeforeActionAsync` | The usual banner |
| Specialists, team members, the agents of an `IMentorAgentSource` | Not asked: a policy leaves none of their tools out (their roles do — see [Role-based actions](#role-based-actions)) | `IsAllowedAsync`, then `BeforeActionAsync` | The usual banner |
| A task from another agent (A2A) | At every turn | The same | Refused: `nobody-to-confirm` |
| The MCP server | At every `tools/list` | At every `tools/call` | Refused: `nobody-to-confirm` |

A policy decides about the application's actions on every surface: the coordinator's, the tools of specialists, team
members and agent sources, the tools of the MCP servers the application consumes, and the actions the MCP server
publishes. Never about MentorAgent's own tools: `navigate_to`, `remember`, `forget_all`, `load_skill`,
`read_skill_resource`, `route_to_specialist`, a team's tool and `invoke_ui_action` — so not about a page's UI
actions either. The A2A agent card is not filtered: it is one card for every caller, built once, and lists only
actions that require no role and ask for no confirmation.

At `Offer`, `IsAllowedAsync` is asked for every action offered, by every policy, at every turn and at every
`tools/list`: keep it fast — a cache per user, for one. With a policy registered, the user is read at every turn and
at every call.

**The actions as MentorAgent sees them — `IMentorActionCatalog`.** Registered by `AddMentorAgent()`, its
`GetActions()` lists every `[MentorAction]` of the coordinator, of the specialists and of the team members, one
`MentorActionInfo` each — for an administration page, or for a test that nothing is published by mistake:

```csharp
var writesOnMcp = catalog.GetActions()
    .Where(a => a.McpExposure is MentorMcpExposure.Published or MentorMcpExposure.PublishedToRoles && !a.ReadOnly)
    .Select(a => a.ToolName);
```

Each one carries `ToolName`, `Description`, `AgentKind` (`Coordinator`, `Specialist` or `TeamMember`) and
`AgentName`, `RequiredRoles`, `RequiresConfirmation` (whether a call asks for a confirmation now, the
`RequiresApproval` predicate included), `ReadOnly` (declared as only reading, and gated by no confirmation),
`OnAgentCard` (whether the A2A card lists it; `false` when the host does not serve A2A), `EnabledByDefault` (`false`
for an action shipped off, which is then `Withheld` on MCP and never `OnAgentCard`: the catalogue says what the code
says, and does not ask the governor) and `McpExposure`:
`Published` to every caller, `PublishedToRoles` to the callers `McpCallerPrincipal` identifies with one of its roles,
`Withheld` (it asks for a confirmation, the predicate holds it back, or it requires a role and no
`McpCallerPrincipal` is set) or `NotServed` (the host does not map the MCP server, the action belongs to a specialist
or a team, or its class is not registered). It is decided again at every call, by the rules of the MCP server and of
the card; a policy may still leave an action out for one user. The tools of the MCP servers the application consumes,
and those of an agent source, are not in it.

### Governing actions from outside the code — `IMentorActionGovernor`

An action can ship off: `[MentorAction(Enabled = false)]` keeps it in `IMentorActionCatalog` (`EnabledByDefault` is
`false`) and nowhere else. No agent is offered it, no model reads about it, the MCP server does not list it, the A2A
card does not show it, and a call to it does not run (`Denied`, `disabled`). Without a governor it stays off.

An `IMentorActionGovernor` decides from outside the code whether each of the application's actions is on, the
roles it requires, whether the user confirms it, and the description the model reads, from an administration
console for example:

```csharp
// Shipped off: it runs only where the governor turns it on
[MentorAction(Description = "Archives an order", Enabled = false)]
public string ArchiveOrder(int orderId) => orders.Archive(orderId);

public sealed class ConsoleGovernor(IConsoleCatalog console) : IMentorActionGovernor
{
    // null: what the code says
    public ValueTask<bool?> IsEnabledAsync(MentorActionRequest request, CancellationToken ct)
        => console.IsEnabledAsync(request.SessionId, request.ToolName, ct);

    // null: the code's roles; []: nobody needs one
    public ValueTask<IReadOnlyList<string>?> GetRequiredRolesAsync(MentorActionRequest request, CancellationToken ct)
        => console.GetRolesAsync(request.SessionId, request.ToolName, ct);

    // Code: what the code says; Required: ask; None: do not ask, even if the code does
    public ValueTask<MentorConfirmationChoice> GetConfirmationAsync(MentorActionRequest request, CancellationToken ct)
        => console.GetConfirmationAsync(request.SessionId, request.ToolName, ct);

    // null: the code's description; the name and the argument schema never change
    public ValueTask<string?> GetDescriptionAsync(MentorActionRequest request, CancellationToken ct)
        => console.GetDescriptionAsync(request.SessionId, request.ToolName, ct);
}

builder.Services.AddMentorActionGovernor<ConsoleGovernor>();
```

- `null` leaves the code's word, `false` turns the action off, `true` turns it on, also one the code ships off.
  **Registering a governor gives whoever implements it the power to widen what the code allows**: an action the code
  ships off runs once it turns it on, one that requires a role runs for anybody once it requires none, and one the
  code confirms runs without asking once it takes the confirmation away. What may be widened, when, and with whose
  approval is the governor's to check.
- `GetRequiredRolesAsync` gives the roles an action requires in place of the code's, which the request carries in
  `RequiredRoles`. `null` leaves the code's; an empty list requires none, so anybody may run the action, signed in or
  not; a list requires any one of its roles, tested as the code's are. They decide whom the action is offered to on
  each turn, the check before a confirmation and again after it, and on the MCP server which callers see and call
  it: an action withheld there only for its roles is published once the governor requires none, and one that
  requires roles is published only with `McpCallerPrincipal`. A decision says where its roles came from:
  `RolesFromOutside`.
- `GetConfirmationAsync` decides whether the user confirms an action in place of the code, which the request
  carries in `RequiresConfirmation`. `MentorConfirmationChoice.Code` leaves the code's word; `Required` asks even if
  the code does not; `None` does not ask even if the code does, whether the confirmation is declared, by default for
  writes, or by `RequiresApproval`. Only the code's confirmation is the governor's: an `IMentorActionPolicy` that
  answers `Confirm` still asks, a hosted MCP server's approval still comes from its provider, and with
  `RequireConfirmation` off nobody is asked. In Native mode a confirmation the governor requires is asked even when
  an `AutoApprovalRules` rule would let the call through. An action whose confirmation is taken away still counts as
  one that changes data: tool filtering never sends it alone, and `RequireActionForChangeRequests` never makes the
  model call it. On the MCP server, where nobody can confirm, an action that asks is not published, and one whose
  confirmation the governor takes away is. A decision says when the governor changed whether a call is confirmed:
  `ConfirmationFromOutside`.
- `GetDescriptionAsync` gives the description the model reads for an action in place of the code's, which the
  request carries in `Description`. `null` or a blank text leaves the code's. It is what the coordinator, the
  specialists and the team members read, what tool filtering ranks the message against, and what the MCP server's
  `tools/list` shows; the action's name and argument schema never change, and the code's `ProactiveHint` still
  follows it. **What the user sees stays the programmer's**: the confirmation banner, the bar of the running action
  and the decisions' `DisplayName` keep the code's description, so a description written outside the code speaks to
  the model and never changes what a user is asked to confirm. It is asked as a session builds its tools and on
  every `tools/list`, never at a call; on a call to the MCP server the policies are asked with the code's
  description. Whoever writes it writes part of the model's instructions about the action.
- It is asked once as a session builds its tools (for the coordinator, the specialists and the team members), on
  every `tools/list` of the MCP server, and before every call, ahead of the roles, the policies and the confirmation.
  `MentorActionRequest.SessionId` names the session every time, so an answer can stay the same for a whole session; on
  the MCP server it is a fingerprint of the MCP session. A session keeps the tools it was built with: turning an
  action off takes effect at once for its calls, which are refused, while turning one on counts from the next
  session. The roles go the same way: what is offered follows the answer given as the session was built, a call
  the answer at that moment. For the confirmation, narrowing takes effect at once and widening from the next
  session: a confirmation added counts from the call, one taken away from the next session, and a call is confirmed
  when the session's answer or the call's asks for it. The description is asked only as a session builds its tools
  and on every `tools/list`. On `/mentor/chat` the session is the request.
- There is one per application: a second `AddMentorActionGovernor` throws. It is a singleton; what it needs from the
  conversation's scope it resolves from `MentorActionRequest.Services`.
- A governor that throws, or does not answer within `ActionPolicyTimeout`, stops the action: it is not offered, and
  the call does not run (`PolicyFailed`, `governor-failed`). Going back to the code would undo what the governor
  narrowed. Only the exception's type is logged.
- **The A2A card follows the code only.** It is built once, for every caller: an action shipped off is not on it, and
  one a governor turns on is not added to it, even though a task can then call it. The onboarding tour, built once
  too, suggests no action shipped off. The roles, the confirmation and the description on
  the card, and in `IMentorActionCatalog`, are the code's too.
- It decides the application's own actions, those `IMentorActionCatalog` lists: not the tools of the MCP servers the
  application consumes, not an agent source's own, never MentorAgent's.

### Auditing the gate's decisions — `IMentorActionObserver`

Every decision MentorAgent's tool gate takes — allowed, denied, blocked by a policy, confirmation asked, confirmed,
rejected, stopped, run, failed — reaches the observers you register: for every session and every surface of the process (a Blazor
Server circuit, a MAUI app, the hub, `/mentor/chat`, the MCP server and A2A) and for every agent that calls a
tool (the coordinator on both paths, specialists, team members and the agents of an `IMentorAgentSource`). It is
the place for an audit trail: who asked for which action, and why the gate decided as it did.

```csharp
builder.Services.AddMentorActionObserver<AuditObserver>();

public sealed class AuditObserver(ILogger<AuditObserver> logger) : IMentorActionObserver
{
    public ValueTask OnDecisionAsync(MentorActionDecision d, CancellationToken ct)
    {
        logger.LogInformation("{Outcome} ({Reason}) {Tool} by {User} on {Surface}, turn {Turn}",
            d.Outcome, d.ReasonCode, d.ToolName, d.User?.Identity?.Name ?? "nobody", d.Surface, d.TurnId);
        if (d.RolesFromOutside)
            logger.LogInformation("{Tool}: roles from the governor ({Roles})", d.ToolName, string.Join(", ", d.RequiredRoles));
        if (d.ConfirmationFromOutside)
            logger.LogInformation("{Tool}: confirmation decided by the governor", d.ToolName);
        return ValueTask.CompletedTask;
    }
}
```

An observer is a singleton, resolved once from the application's root services. Every observer registered is
called, in the order of registration; registering the same type twice registers it once, and
`AddMentorActionObserver(instance)` registers one already built. An observer that needs scoped services creates
a scope with `IServiceScopeFactory`. A host without observers pays nothing for them: the gate does not even
read the user for an action that requires no role.

**`MentorActionDecision`** — the call's arguments are never part of it:

| Property | What it holds |
|---|---|
| `Outcome` | What the gate decided: a `MentorActionOutcome`, below |
| `ReasonCode` | Why: one of the constants of `MentorActionReasons`, below |
| `ToolName` | The tool's technical name, as the model or the MCP client called it, as on `OnActionStarted` |
| `DisplayName` | The text shown to the user for the tool, the one `OnActionExecuting` carries |
| `Surface` | `InProcess` (a Blazor Server circuit or a MAUI app), `Hub`, `Sse`, `Mcp` or `A2A` |
| `AgentKind`, `AgentName` | The agent that asked: `Coordinator`, `Specialist`, `TeamMember` or `AgentSource`, with its name when it has one. `null` on the MCP server, where the caller names the tool itself |
| `SessionId` | An opaque id of the session — the circuit, the MAUI app's scope, the hub connection, the `/mentor/chat` request, the A2A task. On the MCP server, `mcp#` and a fingerprint of the MCP session id, never the id itself |
| `TurnId` | An opaque id of the user turn, new for every message. `null` on the MCP server, where there is no turn |
| `CallId` | The model's id for the call; on the MCP server, the id of the JSON-RPC request |
| `ConfirmationId` | The `ActionId` of the confirmation, on the decisions that belong to one |
| `User` | Who asked for the action, or `null` when nobody is signed in |
| `RequiredRoles` | The roles the action requires, any one of which is enough; empty when it requires none |
| `RolesFromOutside` | `true` when `RequiredRoles` are the `IMentorActionGovernor`'s, in place of the code's: it answered the roles of this call with a list, empty or not |
| `ConfirmationFromOutside` | `true` when the `IMentorActionGovernor` changed, from what the code says, whether this call is confirmed: it took away a confirmation the code asks for, or asked for one the code does not. `false` on the decisions made before the confirmation is decided |
| `ConfirmedBy` | On `Confirmed`, who confirmed: the caller of `POST /mentor/approve`; in a circuit and in the MAUI app, the session's signed-in user |
| `Timestamp` | When the gate decided, in UTC |
| `ExceptionType` | On `Failed` and `PolicyFailed`, the full name of the exception's type. Never its message |
| `Detail` | The application's own words: the reason an [`IMentorActionPolicy`](#deciding-before-an-action-runs--imentoractionpolicy) gave, on `Blocked`, on `ConfirmationRequested` and on `Denied` with `nobody-to-confirm`; `null` otherwise. Whoever reads the record reads it |

**Outcomes and reason codes.** Both lists are closed: a new outcome or code comes only in a minor version, and
the CHANGELOG says so.

| `Outcome` | When | `ReasonCode` |
|---|---|---|
| `Allowed` | The roles and every [`IMentorActionPolicy`](#deciding-before-an-action-runs--imentoractionpolicy) allow the call; the confirmation, when the action or a policy asks for one, comes next | `allowed`; `auto-approved` when, in `Native` mode, an `AutoApprovalRules` rule let a call that asks for confirmation run without asking |
| `Denied` | Refused | `disabled` (the action is off: shipped off and not turned on, or turned off by the governor), `role-missing`, `login-required` (a role is required and nobody is signed in), `auth-not-configured` (no `AuthenticationStateProvider`), `auth-check-failed` (reading the user threw), `not-authorized` (a policy does not allow this user the action), `nobody-to-confirm` (the action or a policy asks for a confirmation nobody can give: a task from another agent, a call to the MCP server), `not-offered` (the model called an action of the application its agent was not given in the turn — one that is off, or another agent's: no gate saw the call, and nothing ran); on the MCP server also `withheld` (an action it does not publish) and `rate-limited` |
| `ConfirmationRequested`, `Confirmed`, `Rejected` | The user is asked, says yes, says no | `operator`; `policy` on `ConfirmationRequested` when a policy asked for a confirmation the action does not ask for itself; `confirmation-timeout` on `Rejected` when nobody answered within `ConfirmationTimeout` |
| `Stopped` | Stopped before it finished | `stop` (Stop, `/mentor/cancel`, the MCP client cancelled the call, or the caller cancelled the A2A task — also after the confirmation, before the action started), `session-ended` (the circuit, the hub connection or the request closed), `turn-ended` (in `Native` mode, the turn ended before a confirmed call could run) |
| `Executed` | The action ran | `completed`; `result-withheld` when `OnToolResult` threw — on the MCP server the caller then gets an error result |
| `Failed` | The action threw | `exception` |
| `Blocked` | A policy stopped the call before it ran; its reason is in `Detail` | `policy` |
| `PolicyFailed` | A policy or the governor threw, or did not answer within `ActionPolicyTimeout`: the call did not run | `policy-exception`, with the exception's type in `ExceptionType`; `policy-timeout`; `governor-failed` (the governor; the exception's type in `ExceptionType` when it threw) |

A call goes through them in one of these orders, the same in `Blocking` and in `Native` mode:

- `Allowed`, then `Executed`, `Failed` or `Stopped`;
- `Allowed`, `ConfirmationRequested`, then `Rejected` or `Stopped` — or `Confirmed`, followed by the outcome of
  the run, or by `Denied` (`role-missing`, `not-authorized`) when a role or a policy's permission was taken away
  while the confirmation waited;
- `Denied`, `Blocked` or `PolicyFailed` alone: a call refused or stopped before the confirmation shows no banner.

`Allowed` comes once the roles, `IsAllowedAsync` and `BeforeActionAsync` have all let the call through. A policy's
`Confirm` on an action that asks for none reads `Allowed` (`allowed`), `ConfirmationRequested` (`policy`),
`Confirmed` (`operator`), `Executed` (`completed`). On the MCP server and in a task from another agent, a call
that asks for a confirmation — its own or a policy's — reads `Denied` (`nobody-to-confirm`) alone.

Every confirmation ends in exactly one outcome. One difference between the modes is real: when the model asks
for two actions at once, `Native` asks for both before it runs either, while `Blocking` runs each one as soon as
it is confirmed. In `Native` mode a confirmation still open when the turn ends — Stop, an error, the cap on
approval rounds — gets its last decision in that turn, with that turn's `TurnId`; the next turn closes it in
the conversation only and raises nothing, and a request the cap never showed has no decisions at all. Over A2A,
the second attempt at a task (see [A2A Server](#a2a-server--exposing-mentoragent-as-an-a2a-agent)) is a turn of
its own, with another `TurnId`.

**Delivery.** No action waits for an observer. Each observer has a queue of its own, of 10,000 decisions, and
receives them in the order they were taken. A call is given 5 seconds; then it is no longer awaited, and the
next decision may reach the observer while that call still runs, so write observers thread-safe: the order
holds except after a timeout. An observer that throws or runs out of time is logged at Warning, with the
exception's type only, then at Debug while it keeps failing. When an observer's queue is full, its decisions
are dropped, for that observer only: a Warning when it starts, an Information line with the count when it
catches up.

**What is not in the record.** The UI actions a page registers, on Path A: `UIActionsMiddleware` runs them
inside the chat client, before the gate. On Path B they reach the gate as calls to `invoke_ui_action`.
`navigate_to`, the memory tools, the skills and `route_to_specialist` are recorded like any other tool. The run
of a provider-hosted tool is not: for a [hosted MCP server](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp)
that asks for approval, the confirmation is the last decision. On the MCP server, a tool name nothing defines
raises no decision.

`IMentorStateService.OnActionStarted` is the per-session event a component shows (see
[Recording which tools ran](#recording-which-tools-ran)); `IMentorActionObserver` is the process-wide record,
with the gate's reasons.

---

## Widget customization

### Themes

The widget's baseline look is **dark glassmorphism** — that is what `MentorAgent.css` defines in its
`:root` block. A theme is a small stylesheet of variable overrides on top of that baseline, which the
widget links itself (`css/themes/dark.css`, `css/themes/minimal.css`).

| Value | Description |
|---|---|
| `MentorTheme.Default` | The baseline. Deep navy glass (`--bm-bg-base: #0a0e1a`) with a blue accent (`--bm-accent: #3b82f6`): a translucent panel, navy at 55% opacity over a blur. Nothing is overridden. On a light page it shows as dark grey, with the page behind it: there, use `Minimal`. |
| `MentorTheme.Dark` | Darker still — near-black base (`#050810`) and a near-black panel at 85% opacity instead of 55%, so less of the page shows through, with more contrast. For apps that are already dark and where the default glass looks washed out. |
| `MentorTheme.Minimal` | **The light theme.** White panel, thin borders, soft shadow. Also re-tints the hosted-tools and A2A chip text, which is pale by design and would be unreadable on white. |
| `MentorTheme.Custom` | Links no theme stylesheet, which today renders exactly like `Default`: the baseline `:root` values in `MentorAgent.css` still apply, and your own stylesheet changes what it overrides. `PrimaryColor`, if set, is still applied. |
| `MentorTheme.Auto` | Follows the visitor's system setting (`prefers-color-scheme`): `Minimal` when it asks for light — which is also what a browser reports when the system states no preference — and `Default` when it asks for dark. The widget links `minimal.css` with `media="(prefers-color-scheme: light)"`, so no script is involved. It follows the system, not your page: on a page that is always light, a visitor whose system is dark sees the dark widget. |

> The names are historical. If you want a light widget, the value you want is `Minimal`, not
> `Default` — or `Auto`, to follow the visitor's system.

`PrimaryColor` is applied on top of whichever theme you picked, and it is not a single variable:
from one hex value the widget derives the pressed shade, the glow, the accent border and the FAB
shadow, so you get a coherent accent from one setting. The widget sets them on `<html>` with
`style.setProperty` once its script runs; until then — on a prerendered page, before the circuit or the
WebAssembly runtime starts — it shows the theme's accent, with no error.

```csharp
options.Theme        = MentorTheme.Minimal;   // light
options.PrimaryColor = "#7c3aed";             // → --bm-accent, --bm-accent-dark, --bm-accent-glow,
                                              //   --bm-border-accent, --bm-shadow-fab
```

Only 6-digit hex is parsed for the derived shades. `rgb()`, `hsl()` and named colours still set
`--bm-accent`, but the glow and shadow keep the theme's values — set them yourself if you use those
formats.

### CSS variables — full reference

Every variable is declared on `:root` and can be overridden from your own stylesheet. This is the
complete set; with `MentorTheme.Custom` it is also the complete list of what you are responsible for.

**Surfaces**

| Variable | Default | Used for |
|---|---|---|
| `--bm-bg-base` | `#0a0e1a` | Page-level base behind the panel |
| `--bm-bg-panel` | `rgba(10,14,26,.55)` | The panel itself — translucent, this is what makes the glass effect |
| `--bm-bg-surface` | `#141929` | Message bubbles, input, cards |
| `--bm-bg-elevated` | `#1a2035` | Raised elements — header, chips, detail bars |
| `--bm-bg-hover` | `#1e2540` | Hover state of buttons and chips |

**Text**

| Variable | Default | Used for |
|---|---|---|
| `--bm-text-primary` | `#f0f4ff` | Message text, headings |
| `--bm-text-secondary` | `#8b9cc8` | Timestamps, secondary labels |
| `--bm-text-muted` | `#4a567a` | Placeholders, disabled state |
| `--bm-hosted-text` | `#fcd34d` | Hosted-tools chip text (amber tint) |
| `--bm-hosted-text-strong` | `#fde68a` | Its emphasised variant |
| `--bm-a2a-text` | `#a5b4fc` | A2A chip text (indigo tint) |
| `--bm-a2a-text-strong` | `#c7d2fe` | Its emphasised variant |

> The four chip colours sit on a translucent tint of their own hue, so they must move with the
> theme. `Minimal` overrides all four to amber-700/indigo-700 — if you build a light theme with
> `Custom`, override them too or the chips become invisible.

**Accent**

| Variable | Default | Used for |
|---|---|---|
| `--bm-accent` | `#3b82f6` | Primary accent — FAB, send button, active states |
| `--bm-accent-dark` | `#2563eb` | Pressed/active shade |
| `--bm-accent-bright` | `#60a5fa` | Highlights, links inside messages |
| `--bm-accent-cyan` | `#06b6d4` | Secondary accent in gradients |
| `--bm-accent-glow` | `rgba(59,130,246,.35)` | Glow behind the FAB and focus rings |

**Borders, radii and shadows**

| Variable | Default | Used for |
|---|---|---|
| `--bm-border` | `rgba(255,255,255,.07)` | Hairlines between regions |
| `--bm-border-accent` | `rgba(59,130,246,.3)` | Focused input, active chip |
| `--bm-radius-sm` | `8px` | Chips, small buttons |
| `--bm-radius-md` | `14px` | Message bubbles, cards |
| `--bm-radius-lg` | `20px` | Panel corners |
| `--bm-radius-xl` | `28px` | Input field |
| `--bm-radius-full` | `9999px` | FAB, avatars, pills |
| `--bm-shadow-panel` | *(3-layer)* | Panel elevation |
| `--bm-shadow-fab` | *(2-layer)* | Floating button elevation |

**Geometry, motion and type**

| Variable | Default | Used for |
|---|---|---|
| `--bm-panel-w` | `400px` | Panel width — the one to change to make the widget wider |
| `--bm-panel-h` | `600px` | Panel height. Capped against the viewport, so a large value degrades gracefully instead of overflowing |
| `--bm-ease-spring` | `cubic-bezier(.34,1.56,.64,1)` | Open/close overshoot |
| `--bm-ease-smooth` | `cubic-bezier(.4,0,.2,1)` | Everything else |
| `--bm-font` | `'Sora', system-ui, sans-serif` | All UI text |
| `--bm-font-mono` | `'JetBrains Mono', 'Fira Code', monospace` | Code blocks, inline code, numbers in the dashboard |

A wider, taller widget in your own brand font is therefore three lines:

```css
:root {
    --bm-panel-w: 480px;
    --bm-panel-h: 720px;
    --bm-font: 'Inter', system-ui, sans-serif;
}
```

### Fonts and Content-Security-Policy

**Sora** and **JetBrains Mono** ship in the `Novior.MentorAgent.Abstractions` package
(`_content/Novior.MentorAgent.Abstractions/fonts/`, SIL Open Font License 1.1): the widget makes no request to a
third party and works offline. They cover Latin and Latin Extended; Japanese, Chinese and any other script
fall back to the next font in `--bm-font` / `--bm-font-mono`.

To use your own fonts, override the two variables. The browser downloads a font file only when some text
uses its family, so the packaged files are then never fetched:

```css
/* Loaded AFTER the widget stylesheet */
:root {
    --bm-font: 'Inter', system-ui, sans-serif;
    --bm-font-mono: ui-monospace, 'Cascadia Code', monospace;
}
```

**Content-Security-Policy.** With any theme and any `PrimaryColor`, the widget and `<MentorDashboard />` work
under `default-src 'self'`, with no `'unsafe-inline'` (BUG-213). A theme is a stylesheet of the package, from
the page's own origin (see [Where to put your overrides](#where-to-put-your-overrides)); a `PrimaryColor` is set
through the CSSOM (`style.setProperty`), which such a policy allows; the dashboard draws with classes and SVG,
with no `style` attribute. For the widget's styles, `style-src 'self'` is enough.

A WebAssembly client has requirements of its own, which come from Blazor and not from MentorAgent:
`script-src 'wasm-unsafe-eval'` for the .NET runtime, and in a Blazor Web App the inline import map that
`<ImportMap />` renders. See the Content Security Policy guidance in the ASP.NET Core Blazor documentation.

### Where to put your overrides

`ChatWidget` injects its `<link>` through `<HeadContent>`, which lands in `<head>`. When `Theme` is `Dark`, `Minimal` or `Auto`, it also links the theme's stylesheet inside its own markup, in `<body>` after everything in `<head>`. No stylesheet in `<head>` comes after it, so for the variables a theme sets, raise the specificity. The five variables of a `PrimaryColor` are set on `<html>` itself, as an inline style that no stylesheet rule without `!important` beats, `html:root` included: to set them yourself, leave `PrimaryColor` unset. For every other variable, a `:root` rule
of yours with the same specificity wins only if the browser sees it **later**, so put your override
stylesheet after the framework's in `App.razor`, or raise the specificity:

```css
html:root { --bm-accent: #7c3aed; }   /* (0,1,1) — beats :root regardless of order */
```

### The admin dashboard is themed separately

`MentorDashboard` is meant to live on an operator page, not inside the widget, so it does not
inherit the widget palette. Its variables are scoped to `.bm-dash`, default to **light**, and flip
automatically under `@media (prefers-color-scheme: dark)`.

| Variable | Light | Dark |
|---|---|---|
| `--bm-dash-bg` | `#ffffff` | `#16181d` |
| `--bm-dash-panel` | `#f6f7f9` | `#1e2127` |
| `--bm-dash-border` | `#e3e6ea` | `#2a2e36` |
| `--bm-dash-text` | `#1a1d21` | `#e6e8eb` |
| `--bm-dash-muted` | `#6b7280` | `#9aa1ac` |
| `--bm-dash-accent` | `#2563eb` | `#60a5fa` |

The SVG charts have their own series colours, so a series keeps its identity across both schemes:

| Variable | Light | Dark | Series |
|---|---|---|---|
| `--bm-chart-in` | `#2563eb` | `#60a5fa` | Input tokens |
| `--bm-chart-out` | `#0d9488` | `#2dd4bf` | Output tokens |
| `--bm-chart-total` | `#7c3aed` | `#a78bfa` | Total tokens |
| `--bm-chart-req` | `#db2777` | `#f472b6` | Requests |
| `--bm-chart-lat` | `#9333ea` | `#c084fc` | Latency |
| `--bm-chart-grid` | `#e3e6ea` | `#2a2e36` | Gridlines |
| `--bm-chart-text` | `#6b7280` | `#9aa1ac` | Axis labels |

To pin the dashboard to one scheme regardless of the OS setting, set the variables on `.bm-dash`
yourself, in a stylesheet loaded after the widget's or with a more specific selector such as `html .bm-dash`. A media query adds no specificity, so an equal `.bm-dash` rule that comes first loses to it.

### Mentorship level

Controls the AI's verbosity and proactivity:

| Value | Behaviour |
|---|---|
| `MentorshipLevel.Minimal` | Executes silently. No unsolicited explanations or suggestions. Best for expert users. |
| `MentorshipLevel.Standard` | Balanced. Full responses with contextual suggestions when relevant. *(default)* |
| `MentorshipLevel.Proactive` | Guides the user: explains what it did and why, warns of risks, proposes alternatives. Best for onboarding or less-experienced users. It ends with the answer, with no closing offers, unless `ProactiveNextSteps` is on (off by default since 1.2.0): then it also closes with 1-3 suggested next steps, limited to what a tool of the current request, a page, a skill, a specialist or a team can carry out — or none. Even so it offered steps the application does not have, 3 in 15 measured turns (BUG-074): it is told how many actions exist but, with tool filtering, is given only the relevant ones as tools. |

### Full example

```csharp
options.Theme              = MentorTheme.Dark;
options.Position           = ChatPosition.BottomRight; // BottomLeft, TopRight, TopLeft, SideRight, SideLeft
options.PrimaryColor       = "#2563EB";                // overrides theme accent color
options.BotName            = "Aria";
options.AvatarUrl          = "/images/aria-avatar.png";
options.WelcomeMessage     = "Hi! I'm Aria, your assistant. How can I help?";
options.InputPlaceholder   = "Ask me anything...";
options.MentorshipLevel    = MentorshipLevel.Proactive;
// options.ProactiveNextSteps = true;  // Proactive's closing next steps — off by default (BUG-074)
options.EnableSuggestions   = true;   // proactive suggestion chips below the input
options.EnableActionFeedback = true;  // "Executing..." visual feedback during tool calls
options.EnableVoiceInput    = true;   // microphone — Chrome/Edge only, requires HTTPS in production
options.EnableVoiceOutput   = true;   // text-to-speech — all modern browsers
options.VoiceHandsFree      = true;   // optional: keep talking without touching the microphone again
options.EnableOnboardingTour = true;  // first-run guided tour, generated from your own pages
```

### Built-in widget features (always active, no configuration needed)

| Feature | Description |
|---|---|
| **Unread badge** | When the widget is closed and the AI responds, a numeric badge appears on the FAB button (capped at `9+`) |
| **Typing indicator** | Animated dots shown while the AI is processing and no streaming text has arrived yet |
| **Action bar** | While a tool is executing, a pulsing bar shows the tool display name |
| **Stop button** | A ■ Stop button appears above the input while the AI is processing. Clicking it cancels the current request immediately — the partial response (if any) is kept in the chat |
| **Reset button** | Button in the widget header that clears the message list and starts a new `AgentSession` |
| **Voice toggle** | When `EnableVoiceOutput = true` and the browser supports Speech Synthesis, a speaker toggle button appears in the header. Turning it off also silences whatever is currently playing |
| **Replay tour** | When `EnableOnboardingTour = true`, a **?** button in the header reopens the [tour](#onboarding-tour) at any time |
| **MCP status badge** | When `ShowMcpStatus = true` and MCP servers are configured, a pill in the header shows the server's name (one server) or `online/total` (several), coloured by connection state. Click to see details |
| **A2A status badge** | When `ShowA2AStatus = true`, a badge in the header shows configured remote A2A agents (name + URL). Click to expand the detail bar |
| **RAG citations** | When `ShowRagSources = true`, source chips appear below AI messages linking to the retrieved documents |

---

## Token & cost optimization

MentorAgent minimizes the tokens sent on every request. Some optimizations are always on; the others are opt-in.

**Always on (no configuration):**
- **Slim, cache-friendly system prompt** — the static instructions are a stable prefix (so the provider can cache them), and volatile data (date/time, URL) is emitted last. A model that caches the whole request by its prefix, a local engine above all, still reads the conversation again when the minute or the page changes: see [`SessionContextPlacement`](#the-requests-context-in-the-users-message--sessioncontextplacement).
- **Token measurement** — every model round-trip logs its usage, so you can verify the effect:
  ```
  [MentorAgent] Tokens — model: gpt-4.1, in: 740, out: 95, call total: 835, 1180ms | session: 740+95=835 over 1 call(s)
  ```

### Semantic tool filtering

Every tool you register is serialized as a JSON schema into each request — the biggest per-call cost when you have many tools. With tool filtering, only the tools **semantically relevant** to the user's latest message are sent; the AI still freely chooses among them.

Relevance is computed with an **embedding model**, which ranks tools by meaning across languages (an Italian message matches English tool names); the absolute score is not calibrated. It **requires** `EmbeddingGenerator` — if enabled without one, filtering is skipped and all tools are sent (with a warning); there is no keyword fallback.

```csharp
options.EmbeddingGenerator = new AzureOpenAIClient(endpoint, credential)
    .GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();

options.EnableToolFiltering = true;
options.ToolFilterMaxTools  = 12;    // max matched business tools (core tools always kept)
options.ToolFilterMinScore  = 0.35f; // cosine-similarity threshold (higher = stricter)
options.ToolFilterMinTools  = 0;     // default: the threshold alone (see below before raising it)
```

Core tools (navigation, memory, routing, teams, skills, UI actions) are **always kept**. Tool embeddings are computed once and cached. Only the actions the user may run are ranked: one with `RequiredRoles` they hold none of never travels, not as a fallback and not carried over (see [Role-based actions](#role-based-actions)). Which application actions travel with them follows three rules:

- **Nothing clears the threshold** — the **three best-ranked actions that need no confirmation and no role** travel anyway (within `ToolFilterMaxTools`); an MCP tool only when its server marks it `readOnlyHint`. The choice is by tool name, so a name that two actions or two MCP servers share travels only when every copy qualifies. Cosine ranks well and is not calibrated: asked *"Quanto costa il Dell XPS 15?"*, the read that could answer scored 0.21, below *"Ciao"* at 0.28, so no line separates a question that needs data from a greeting. With core tools only the model invented a price range or said the catalogue did not have it (BUG-106). It costs about 80 input tokens per model call on such turns, greetings included. Any action that needs no confirmation may therefore be offered on any message: **give every action that changes data `RequiresConfirmation`** (or the `RequiresApproval` predicate).
- **Only actions that need confirmation cleared it** — the three best-ranked actions that do not travel with them (BUG-099): sent alone, a stock-update action was called with a quantity of 0 for a question about stock.
- **An action that needs no confirmation cleared it** — the actions above the threshold travel alone, unless `ToolFilterMinTools` asks for more.

**The answer to the assistant's own question carries the previous selection.** "Sì, procedi" or the reason the assistant asked for match no action ("Sì, procedi" scores 0.29 against `cancel_order`), so that message used to arrive with none of the actions the question was about: the model, with no tool, could only say it could not — or claim it had (OSS-01). On a message that matches nothing, the actions the previous message selected travel again: one message back only, never what was itself carried, and a confirmation-gated action still never alone. A new or restored conversation carries nothing.

**Answering questions about your records from a read (opt-in).** Even with the right read action on the turn, the model can answer *"I can't see the price — shall I look it up?"*, or write "price not available" without calling the action that would have told it. Stronger wording in the prompt only traded that for invented figures (BUG-106, measured). `RequireLookupForRecordQuestions` makes it structural: when a classifier model says a message asks for a value of your records, the first model call **must** call one of the actions you declared read-only, or hand the request to a specialist or a team. With semantic tool filtering it is one of those the filter selected for that message — the one that cleared the threshold, the companions of a gated match, or the zero-match fallback. The option does not need the filter (BUG-222): without it (`EnableToolFiltering` off, or no `EmbeddingGenerator`) it is any declared read the user may run — one that needs no role and no confirmation — since no ranking chooses among them, and the turn's later calls have every tool again. Nothing that writes, navigates or loads a skill can satisfy it, and the reply is written from what the read returned.

```csharp
options.RequireLookupForRecordQuestions = true;   // with or without EnableToolFiltering
// Declare the reads: by attribute…
[MentorAction(Description = "Counts open orders", ReadOnly = true)]
// …or by name, for [Description] methods
options.IsReadOnlyAction = name => name.StartsWith("get_") || name.StartsWith("search_");
```

It costs one small classifier call (`ClassifierChatClient` when set, bounded by `SafetyCheckTimeout`) on every turn whose selection holds a declared read — with the zero-match fallback, and without the filter, nearly every turn, greetings included — and on those turns the reply starts after the result, so a Proactive "I'm looking it up…" sentence no longer comes first. A classifier that fails or answers anything unreadable leaves the turn as it would be without the option. Without the filter, a startup line at Information says that the read is chosen among every declared read. The classifier is told the names of your specialists, teams and remote agents, so a message addressed to one ("ask ShopFlowRemote…") is classified OTHER and not forced — measured on the classifier alone before it was wired in, Azure gpt-4.1: 32 messages × 3, 96/96, and 15 messages written afterwards × 3, 45/45. On a forced call `route_to_specialist` and the team tools stay available, for the message the classifier misreads.

**Starting a change or a page request with the action itself (opt-in).** Asked *"Imposta lo stock dell'iPad Air a 30"*, the model can answer *"Stock aggiornato"* without calling anything — no banner, nothing changed — and asked for a page, *"Ecco la pagina Prodotti"* without navigating (BUG-118: on the ApiServer sample, 11 stock requests in 25 and 5 page requests in 10; with the option, the banner 25 times in 25 and the page 10 times in 10). A rule in the prompt cannot stop it: worded generally, it would also forbid reporting what a specialist did. `RequireActionForChangeRequests` makes it structural: when the classifier says a message asks to carry out one of your actions that ask for confirmation — and gives everything that action needs — the first model call **must** call one of those actions that the filter selected for that message, or hand the request to a specialist or a team, so the confirmation banner is the first thing the user sees. When it says the message asks for one of your pages (`[MentorPage]`), the first call must be `navigate_to`. Only the actions the user may run count: one they have no role for is never required, and the classifier is not shown it.

```csharp
options.EnableToolFiltering = true;
options.RequireActionForChangeRequests = true;
// Only actions that ask the user first are ever required:
[MentorAction(Description = "Aggiorna la quantità in stock di un prodotto", RequiresConfirmation = true)]
```

**A request that leaves something out is never forced.** The classifier is shown the inputs each selected action requires, and calls a request complete only when the message itself gives a value for each one. *"Annulla l'ordine 1004"* is not complete when `cancel_order` also needs a reason; neither is *"annulla l'ultimo ordine"* (the order is only described), *"cambia il piano di Mario Rossi"* when the action takes a customer ID, or *"aggiungi 5 unità"* (the new value has to be worked out from the current one). The model is then free to ask, as without the option. Forced, it would have had to invent the missing value, and the banner would have shown that value as if the user had given it. "Needs" means required in the action's schema: an input the action declares optional is not asked for first, whatever its `ProactiveHint` says, so make an input required if it must be asked. An action whose inputs cannot be shown faithfully — a schema with `$ref` or a combination of schemas, an MCP tool that marks none of its parameters required — is never required.

Only actions that will ask a person first are ever required: declared confirmation (`RequiresConfirmation`, the `RequiresApproval` predicate, an MCP server with `RequiresConfirmation`) with `RequireConfirmation` on — and, from 1.0.0-rc.15, every `[MentorAction]` that `RequireConfirmationForWrites` makes ask, so with the default more of your actions can be required than in rc.14. With `HitlMode.Native`, no `AutoApprovalRules` rule is consulted for a required call: it is always shown to a person, while the calls the model makes on its own are the rules' to decide, as before. A page request is matched against your `[MentorPage]` pages, which the classifier is shown: a page the application does not have, or something within the page already open, is not forced. A request from another agent is never required to navigate, and the turn that answers an approval a Stop left open is never forced. It costs one small classifier call per turn while the application has a page or the message selected such an action; with `RequireLookupForRecordQuestions` on as well, one call answers both. The classifier is asked only about what this message's selection could satisfy, so with reads alone it is asked the question measured above, unchanged. Both run it at temperature 0, the temperature it was measured at.

**A provider that ignores `tool_choice`.** Both options require the first call with `ChatToolMode.RequireAny`, which the provider receives as `tool_choice: "required"`. Ollama ignores it and answers with text, so with Ollama the two options have no effect. The first time a request that required a tool call comes back without one, a Warning says so, once per provider client (`MentorOptions.ChatClient`) in the process; a streamed reply is not held back for it. vLLM supports `"required"`; the recipes for llama-server and vLLM are in [AI providers](#ai-providers).

The threshold decides which actions count as a match. `ToolFilterMinTools` (default 0) can also send the next best-ranked actions with a lone match, whatever their score. It is a trade-off, measured both ways. Asked *"Quante unità di iPad Air ci sono in magazzino?"*, a shop's stock-update action (*"Aggiorna la quantità in stock di un prodotto"*) scored 0.351 and was the only action above 0.35, while the actions that read stock ranked just below. Sent alone, the write action was called with a quantity of 0 and the stock was set to 0; with a floor of 3 the read actions travelled with it and nothing was written. But asked *"Quanto ha speso Anna Ferrari?"*, the lone match was the right search, and with a floor of 3 the model picked a by-email lookup instead and answered that no such customer exists, four times of four. Neither setting tells a wrong lone match from a right one. **Let every action that changes data ask for confirmation** — a `[MentorAction]` that declares nothing does, from 1.0.0-rc.15 — that is what stops a model that picked the wrong tool. On a task from another agent, where nobody can confirm, such an action is refused at once.

> Applies only to the pipeline MentorAgent builds around `ChatClient` (Path A). With a pre-built `Agent` (Path B) the option has no effect and a startup warning says so. Filter the tools on your own agent instead.

**Checking a reply that announced an action and called none (on by default).** With `MentorHitlMode.Blocking` and a Chat Completions client, the model can end its reply with *"Procedo a registrare la nota…"* and call nothing: the note is not registered, and the user is left waiting (BUG-220). A rule in the prompt against it only moved the miss: the model then wrote that the note was registered, still without calling anything. `CheckAnnouncedActions` checks the reply instead. After a turn in which the coordinator called no tool, a model is asked whether the reply announces an action (*"I'm registering the note…"*) or reports one as done; when it does, the coordinator gets one more request in the same turn, with the same tools, told to call the tool now or to say, in one sentence, that it was not done. The question names the tools the model was sent in the turn, and the answer names the one that does the action: the second request then tells the model that this tool is there to call now, and not to say that it cannot be done or that signing in is needed.

```csharp
options.CheckAnnouncedActions = true;                  // the default
options.ClassifierChatClient  = smallDeploymentClient; // where the question goes; ChatClient without it
```

- **Never a required call.** The second request keeps the tool mode it had: an action that asks no one first must not run on a judgement that may be wrong. The call it makes passes the gate as any other: one that asks for confirmation shows the banner.
- **Once per turn.** The reply of the second request is not checked again, and the request that reads the tool's result is not told again to call it.
- **Nothing is held back.** The first reply streams as before; the second follows in the same turn, as a message of its own, as the approval rounds of `MentorHitlMode.Native` do. The instruction is that request's last message, read after the reply it is about — a message of the user, since many chat templates (Mistral and Ministral, Gemma, others served by llama-server or vLLM) refuse a system message that is not the first (BUG-231) — and it is never added to the conversation, so it is not stored or carried into later turns.
- **What it costs.** One small call after every reply that called no tool, while the coordinator has an action of the application to call (your `[MentorAction]` methods, specialists, teams, remote agents or MCP servers): the user's message and the reply, each cut to one line, the names of the tools the model was sent, and an answer of one or two words. A turn that called a tool, or an application with no actions, is not checked. The question goes to `ClassifierChatClient`, or to `ChatClient` without one; unlike the other checks on that client, this one is on by default, so a small, fast deployment there is worth configuring. It is bounded by `SafetyCheckTimeout` and asked at temperature 0.
- **What it cannot see.** It reads the reply, not the application: a reply that says *"done"* after a tool that did something else is not its case. A question that fails, runs out of time or answers anything unreadable leaves the turn as it is. A line at Information in the log gives the verdict of every reply it checks — `ANNOUNCED`, `CLAIMED`, `NONE` or `unreadable`, with the time and the tokens it took — and another says when the second request was made and whether it called a tool. A call to an action of the application the agent was not given in the turn — one that is off, or another agent's — runs nothing, and the turn is checked as one in which nothing ran. With this option on or off, an `IMentorActionObserver` receives that call's one decision: `Denied`, `not-offered`.

> Applies to the pipeline MentorAgent builds around `ChatClient` (Path A), with the history MentorAgent keeps. With `UseServiceManagedHistory`, or with a pre-built `Agent` (Path B), the option has no effect.

### History compaction

As a conversation grows, its history is re-sent on every call. Compaction shrinks it intelligently instead of a hard cut. It uses the (experimental) Agent Framework compaction pipeline: collapse old tool results → keep the last N turns → hard token-budget backstop.

```csharp
options.EnableCompaction         = true;
options.CompactionTokenThreshold = 4000;  // token budget that triggers compaction
options.CompactionMaxTurns       = 8;     // recent turns kept intact
```

> Applies only to agents with a local history (Path A / `ChatClient`: the built-in provider, or your `ChatHistoryProviderFactory` / `ChatHistoryProvider`). With `UseServiceManagedHistory` (Foundry, Responses API with `store`) it is not installed.
>
> Compaction changes what is **sent** to the model, never the **stored** conversation — the history provider, a session snapshot, a durable store. Until rc.13 it also dropped the question of every turn that called a tool or ran a UI action from the stored history (BUG-104); questions lost that way cannot be recovered from a snapshot taken on those versions.

### The request's context in the user's message — `SessionContextPlacement`

By default the lines that change with every request — the page the user is on, its data from `IMentorPageContext`, the
address, the date and time to the minute — close the instructions. The instructions come before the whole
conversation, so a model that caches the prompt by its prefix, a local engine above all, reads the whole conversation
again whenever the minute or the page changes; on CPU, that can be many seconds before the first word.

```csharp
options.SessionContextPlacement = MentorSessionContextPlacement.LastUserMessage;
```

With `LastUserMessage` those lines move to the start of the user's last message, in a block
`⟦app-context <code>⟧ … ⟦/app-context⟧` whose code is drawn once per session. The instructions keep the user, the
roles, the memory and the notice of a turn served over A2A, and end with a line that says where the block is and that
only that block comes from the application. They stay the same for the whole session, and the model reads again only
the last exchange.

**What it guarantees.**
- The block is only in the request sent to the model: the stored conversation keeps the message as the user wrote it.
- In the user's messages sent to the model, `⟦` becomes `[`, so a user's text cannot imitate the block.
- Every request of a turn, the tool loop included, carries the same block on the same message.
- Only the coordinator's model sees it, on both models with model routing. The classifiers (safety, scope, the forced
  read), memory extraction, the router's classifier, specialists and team members never see the block or its code;
  tool filtering, the router and the classifiers read the user's words without it.

**What it does not do.**
- RAG documents stay in the instructions: a turn that brings documents still changes them, so the saving holds for the
  turns without documents.
- It needs Path A with local history. With `UseServiceManagedHistory` (the service would keep the block in every
  message) and on Path B it has no effect, and a startup warning says so: `SessionContextPlacement is LastUserMessage,
  but …`.
- OpenTelemetry records the request without the block, which is added closer to the model.

With the option on, a startup line at Information says where the context goes. With `Instructions`, the default,
nothing changes: the instructions are the ones they were, line for line.

### Semantic RAG and relevance-ranked memory

Both inject context only when relevant, saving tokens — and both are fully semantic (embedding-based, no keyword heuristics):
- **RAG**: the vector search + `RagMinScore` already ensure nothing irrelevant is injected on pure commands. See [RAG configuration options](#rag-configuration-options).
- **Memory**: with `MemoryRelevanceFiltering` (requires `EmbeddingGenerator`), only the memories most similar to the message are injected (identity/preference facts always kept). See [Contextual memory](#contextual-memory).

---

## Middleware & extensibility

Make the request pipeline robust and pluggable — all additive (defaults preserve current behaviour). Prefer the **built-in toggles**; drop to the **custom hooks** for full control.

```csharp
// ── Built-in safety (no code) ─────────────────────────────────────────────────
options.EnableSafetyCheck       = true;   // LLM check on the USER MESSAGE (prompt-injection / jailbreak)
options.RefuseOutOfScope        = true;   // and refuse what plainly has nothing to do with this
                                          // application: the same classifier call returns both
                                          // verdicts, so it is refused without a model call
options.EnableOutputSafetyCheck = true;   // LLM check on the ASSISTANT REPLY (leaks system prompt / secrets / PII / harmful)
options.SafetyCheckTimeout      = TimeSpan.FromSeconds(15);         // both checks, built-in or custom (Zero = no limit)
options.SafetyCheckFailure      = MentorSafetyCheckFailure.Allow;  // no verdict (timeout/error): Allow = fail open, Block = refuse

// ── Custom hooks (full control) ───────────────────────────────────────────────
// Custom input guardrail — REPLACES EnableSafetyCheck. Return true = safe.
options.InputGuardrail  = (message, ct) => Task.FromResult(IsSafe(message));
// Custom output guardrail — REPLACES EnableOutputSafetyCheck. Buffers the reply (no live streaming that turn).
options.OutputGuardrail = (reply, ct)   => Task.FromResult(IsSafeReply(reply));
// Transform a tool result before it goes back to the model (e.g. cap very long results to save tokens).
options.OnToolResult    = (toolName, result) => Truncate(result, maxChars: 2000);
// Map an exception to a friendly message (null → built-in mapping).
options.OnException     = ex => ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase)
    ? "The service is busy, please retry shortly." : null;
// Public extension point — insert your own DelegatingChatClient / AF middleware, outermost.
// Built once per coordinator (per circuit on Blazor Server); only the coordinator's own calls go through it.
options.ConfigureChatClientPipeline = b => b.UseLogging();
```

Two layers: **built-in toggles** — `EnableSafetyCheck` / `EnableOutputSafetyCheck`, LLM classifiers (one small model call each, on `ClassifierChatClient`, or on `ChatClient` when none is set; on Path B without either they are skipped, with a warning when the coordinator is built) — and **custom hooks** that replace or extend them. A custom `InputGuardrail` / `OutputGuardrail` takes precedence over the matching built-in. Output moderation (built-in **or** custom) buffers the reply for that turn, so no unmoderated text is ever streamed; leave both off to keep live token-by-token streaming.

**`OnToolResult` and cards.** The hook runs *before* [generative cards](#generative-ui--cards-level-2) are extracted, and what it returns is what reaches the screen as well as the model. That ordering is deliberate, so a redaction reaches the card too. For a method declared as returning `MentorCard`, the hook receives a `JsonElement`. A hook that turns every result into a string therefore stops cards from rendering, with no error, so let card tools through untouched. Card-shaped JSON the hook returns is drawn as a card only for a method declared to return cards; a `MentorCard` object it returns always is:

```csharp
var cardTools = new HashSet<string> { "get_order", "list_products" };
options.OnToolResult = (toolName, result) =>
    cardTools.Contains(toolName) ? result : Truncate(result, maxChars: 2000);
```

**When a check has no verdict.** Every check — input or output, built-in or custom — runs under `SafetyCheckTimeout` (15 s by default) and is cancelled by Stop like the rest of the turn. A check that times out, meets a provider error or throws has no verdict, and `SafetyCheckFailure` decides: `Allow` (default, fail open) processes the message and shows the reply with a warning in the log; `Block` (fail closed) refuses the message with a localized "can't check it right now, try again", or withholds the reply. An UNSAFE verdict blocks under both. On a hosted model the input check takes 0.7–2 s; on a local model running on a CPU one call can take close to a minute — raise the timeout, or give the checks a fast `ClassifierChatClient`.

## Observability (OpenTelemetry)

Opt in with `EnableObservability`. MentorAgent then emits **GenAI-convention** traces/metrics for the coordinator's model calls (its `ChatClient` pipeline on Path A), plus its own per-turn/tool spans and counters on both paths — all under the source/meter named by `ObservabilitySourceName` (default `"MentorAgent"`). Nothing else is wrapped: to trace the embedding generator, the `ClassifierChatClient` or a pre-built `Agent` too, wrap it with `UseOpenTelemetry()` before you hand it over. Your app wires the exporter:

```csharp
options.EnableObservability = true;
options.ObservabilityIncludeSensitiveData = builder.Environment.IsDevelopment(); // prompt/response text: dev only

// Host wiring (App Insights / Aspire / OTLP / console):
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("MentorAgent").AddConsoleExporter())
    .WithMetrics(m => m.AddMeter("MentorAgent").AddConsoleExporter());
```

On top of the GenAI spans, MentorAgent emits these under `ObservabilitySourceName`:

| Instrument | Kind | Emitted |
|---|---|---|
| `mentor.turn` | span | Once per user turn that got past the input checks |
| `mentor.tool.{toolName}` | span | Once per tool call through MentorAgent's gate — in `Native` mode also for a call refused, rejected or stopped before it runs. Tags: `mentoragent.tool`, `mentoragent.tool.outcome` (the call's last outcome: `executed`, `failed`, `denied`, `rejected`, `stopped`, `blocked`, `policy-failed`…), `mentoragent.tool.reason` (a [reason code](#auditing-the-gates-decisions--imentoractionobserver)), `mentoragent.tool.confirmation` (`none`, `requested`, `confirmed`, `rejected`, `auto-approved`, `unavailable`, `timed-out` when nobody answered within `ConfirmationTimeout`), `mentoragent.surface` (`in-process`, `hub`, `sse`, `a2a`) and, when the agent has a name, `mentoragent.agent`. A failed call, and one whose policy failed, sets the status to error |
| `mentor.mcp.tool.{toolName}` | span | Once per `tools/call` to a tool the MCP server publishes, with the same tags: `mentoragent.surface` is `mcp` and `mentoragent.tool.confirmation` is `none`, or `unavailable` when a policy asked for a confirmation |
| `mentor.rag.search` | span | Once per knowledge-base search (`UseRag`). Tag `mentoragent.rag.source`, the source's type's full name; when the search failed, the status is Error and `mentoragent.error.type` names the exception's type, never its message |
| `mentoragent.turns` | counter | User turns processed |
| `mentoragent.tool_calls` | counter, tag `mentoragent.tool` | Tool invocations |
| `mentoragent.errors` | counter | Turns that ended in an error |
| `mentoragent.guardrail_blocks` | counter | Messages refused by the input check (`RefuseOutOfScope` refusals included), replies withheld by the output check |
| `mentoragent.rag_failures` | counter, tags `mentoragent.rag.source`, `mentoragent.error.type` | Knowledge-base searches that failed — the source threw, or answered with no list — so the turn went on without documents. A Stop during the search is not counted |

With an `IMentorTenantResolver` registered, every counter and the `mentor.turn` span carry one more tag,
`mentoragent.tenant`: the tenant of the turn or of the MCP call. Without a resolver the tag is absent and every
series is as before. Read the whole instance, tenant by tenant, from here: the dashboard below shows one tenant
at a time.

> The GenAI chat spans come from the pipeline MentorAgent builds around `ChatClient` (Path A). With a pre-built `Agent` (Path B) you get the `mentor.*` spans and `mentoragent.*` counters only. Instrument your own agent's chat client for the rest.

### What the log shows, and when

- **The configuration summary is written once per process.** The first coordinator logs what it was built with at Information: skills, external and remote agents, hosted tools, the hosted MCP server and declarative handoff. Every later session (another circuit, another hub connection) writes the same lines at **Debug**. To see what one session was built with, turn Debug on for `Novior.MentorAgent`. Warnings are never demoted: a degradation is still reported for the session it affects. An unreachable remote agent is reported once per retry window (1 to 5 minutes), not once per visitor.
- **One failure, one stack trace.** When a provider call fails, the first component to meet the failure in a turn logs the full exception. Every later one logs a single line, *same cause as the first failure in this turn*.
- **No user names and no exception text in a tool call's log lines.** A call refused for a missing role names the user by a fingerprint (`caller#` and 10 hex characters, the same one the MCP server's `tools/call` line uses), never by name, which is often an e-mail address. A tool that throws, or a role check that fails, is logged with the exception's type only; the whole exception is logged at Debug. A refused call writes one Warning, the same in `Blocking` and in `Native` mode.

## Token & cost dashboard

An **admin-only** view — never shown in the end-user widget — modelled on Azure OpenAI's monitoring page: a **model selector**, **temporal charts** (tokens / requests / latency over time), a **per-model cost table**, plus deflection and top actions. Usage is attributed **per model** — the cheap chat model, the strong routing model, the embedding model and `ClassifierChatClient` (its own row when it is a different deployment) are tracked separately (tokens, requests, latency), so you see exactly where cost goes. Every model call inside a delegated turn is counted: the handoff router, Level-2 specialists, Level-3 team members and agents from an `IMentorAgentSource`. Supply prices (MentorAgent ships none), then drop the component on a protected page:

```csharp
options.ModelPricing = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
{
    ["gpt-4.1"]                = new ModelPrice(InputPer1M: 2.00m, OutputPer1M: 8.00m),  // cheap chat
    ["o3"]                     = new ModelPrice(InputPer1M: 2.00m, OutputPer1M: 8.00m),  // strong routing
    ["text-embedding-3-small"] = new ModelPrice(InputPer1M: 0.02m, OutputPer1M: 0.00m), // embedding
};
```

```razor
@* Admin page — you own the authorization; the component reads IMentorMetrics in-process. *@
@attribute [Authorize(Roles = "Admin")]
@using Novior.MentorAgent.Abstractions.Components
<MentorDashboard Currency="$" />
```

Cost is shown only for models present in `ModelPricing`; otherwise tokens are shown with cost “n/d”. **Key `ModelPricing` by the model id the dashboard reports** — for Azure OpenAI that is your *deployment* name, not the base model name. Charts are inline SVG (no JS, no external libraries), so they render on Blazor Server and WASM without scripts, and are theme-aware; the dashboard renders no `style` attribute, so it works under `default-src 'self'` (see [Fonts and Content-Security-Policy](#fonts-and-content-security-policy)). Time-series buckets are hourly and kept for `MetricsRetention` (default 7 days). Headless clients read the same data from `IMentorMetrics.GetSnapshot()` (or, in [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server), the `GET /mentor/admin/metrics` endpoint) — the snapshot carries the per-model breakdown and series, so the WASM dashboard is identical.

The dashboard is **localized** like the rest of the UI (10 languages, English fallback). On Blazor Server it resolves `MentorLocalizer` from DI and follows `options.Language` automatically — nothing to pass. In WASM there is no MentorAgent DI, so set the language explicitly:

```razor
<MentorDashboard Snapshot="_snapshot" OnRefresh="LoadAsync" Currency="€" Language="MentorLanguage.Italian" />
```

### Persistence (optional)

By default the metrics live only in RAM and reset when the process restarts. Register an `IMentorMetricsStore` **before** `AddMentorAgent()` to change that — same pluggable pattern as `IMentorMemoryStore`. Each implementation fills only the half of the contract it needs:

```csharp
// (A) Local durability — persist a JSON/DB snapshot. The dashboard keeps reading live RAM,
//     re-seeded from the last save on startup. The library flushes on a timer + on shutdown.
builder.Services.AddSingleton<IMentorMetricsStore, FileMetricsStore>();   // your impl: LoadForSeed + Save

// (B) External source — the dashboard reads the aggregate the OpenTelemetry export already
//     pushed to Prometheus / Azure Monitor (historical, multi-instance). SaveAsync stays a no-op
//     because OpenTelemetry is the writer; QueryAsync queries the backend on each read.
builder.Services.AddSingleton<IMentorMetricsStore, PrometheusMetricsStore>();   // your impl: QueryAsync

builder.Services.AddMentorAgent(options => { ... });
```

`LoadForSeedAsync` restores totals at startup, `SaveAsync` persists them (timer via `MetricsPersistenceInterval`, default 30s, + graceful shutdown), and `QueryAsync` lets an external source serve the authoritative aggregate — the admin endpoint returns `await store.QueryAsync(tenant) ?? metrics.GetSnapshot(tenant)`, with `tenant` `null`
without a resolver. The default `NullMetricsStore` keeps the RAM-only behaviour with zero overhead. Working `FileMetricsStore`, `PrometheusMetricsStore` and `AzureMonitorMetricsStore` implementations ship in the `MentorAgentServer` sample.

### With more than one tenant

With an `IMentorTenantResolver` registered (see [Serving more than one tenant](#serving-more-than-one-tenant)),
`<MentorDashboard/>` and `GET /mentor/admin/metrics` show the figures of the reader's tenant only — tokens, cost,
turns, errors, guardrail blocks and top actions, the MCP server's calls included — never another tenant's. A
reader whose tenant the resolver does not name sees no figures, and the endpoint answers 403.

- **Reading a tenant in code.** `IMentorMetrics.GetSnapshot(tenant)` gives a tenant's snapshot; `GetSnapshot(null)`
  is the instance's, the same as `GetSnapshot()`.
- **Kept in RAM.** A tenant's figures are kept in RAM since the process started. A registered
  `IMentorMetricsStore` saves and seeds the instance's totals only.
- **An external store.** A store that reads an external backend can serve a tenant too, by overriding
  `QueryAsync(string? tenant, CancellationToken)` and filtering on the `mentoragent.tenant` tag. Without that
  override the store is asked for the instance only, and a tenant's reader gets the tenant's in-RAM figures.
- **A custom `IMentorMetrics`** keeps working unchanged: the new `GetSnapshot(string? tenant)` has a default, which
  answers the instance's snapshot for `null` and nothing for a tenant. With a resolver, that dashboard therefore
  shows a tenant no figures rather than the whole instance's, and the endpoint answers 204.

### Reading the numbers yourself

`MentorDashboard` is one consumer of `IMentorMetrics`. Inject it to feed your own admin page, an
export job, or a health probe — the same snapshot the component and `GET /mentor/admin/metrics`
both render:

```csharp
public class UsageReport(IMentorMetrics metrics)
{
    public string Summary()
    {
        MentorMetricsSnapshot s = metrics.GetSnapshot();

        var lines = new List<string>
        {
            // Deflection is the business number: turns that completed without an error, over turns
            // that got past the input checks (a refused message never becomes a turn; a withheld reply
            // still counts as served).
            $"{s.TurnCount} turns · {s.DeflectionRate:P0} deflected · {s.ErrorCount} errors",
            $"{s.TotalTokens} tokens · {s.TotalCost?.ToString("C") ?? "n/a"}"
        };

        foreach (MentorModelUsage m in s.Models)
        {
            // TotalCost is null — not zero — when the model has no ModelPricing entry.
            lines.Add($"  {m.ModelId} ({m.Kind}): {m.RequestCount} req, " +
                      $"{m.TotalTokens} tok, {m.TotalCost?.ToString("C") ?? "n/a"}");

            foreach (MentorUsagePoint p in m.Series)          // hourly buckets
                lines.Add($"    {p.Timestamp:t}  {p.TotalTokens} tok  {p.AvgLatencyMs:F0} ms");
        }

        foreach (MentorActionCount a in s.TopActions)
            lines.Add($"  tool {a.Action}: {a.Count} calls");

        return string.Join('\n', lines);
    }
}
```

**`MentorMetricsSnapshot`** — the aggregate:

| Member | Type | Meaning |
|---|---|---|
| `TotalInputTokens` / `TotalOutputTokens` / `TotalTokens` | `long` | Token totals across every model |
| `RequestCount` | `long` | Model calls served |
| `TurnCount` / `ServedCount` / `ErrorCount` | `long` | User turns started, answered, failed |
| `GuardrailBlockCount` | `long` | Messages refused by the input check (`EnableSafetyCheck` / `InputGuardrail`, including a `SafetyCheckFailure = Block` refusal) plus replies withheld by the output check, and `RefuseOutOfScope` refusals. Rate-limited messages are not counted |
| `DeflectionRate` | `double` | `ServedCount / TurnCount`, `0` when nothing ran |
| `InputCost` / `OutputCost` / `TotalCost` | `decimal?` | **`null` when the model is unpriced** — never a fabricated `0` |
| `Models` | `IReadOnlyList<MentorModelUsage>` | Per-model breakdown, in a stable display order |
| `TopActions` | `IReadOnlyList<MentorActionCount>` | `(Action, Count)` — most-called tools first |
| `SeriesBucketMinutes` | `int` | Width of one `Series` bucket, `60` |
| `GeneratedAt` | `DateTimeOffset` | When the snapshot was taken |

```csharp
var s = metrics.GetSnapshot();

// GeneratedAt is what makes a cached or persisted snapshot honest: a dashboard that shows
// yesterday's numbers as today's is worse than one that shows nothing.
Console.WriteLine($"as of {s.GeneratedAt:HH:mm:ss} · buckets of {s.SeriesBucketMinutes} min");

// Messages the input check refused (nothing spent on the answer) plus replies the output check
// withheld (already generated and billed). Rate-limited messages are not counted. A rising
// number here with a flat TotalCost is a guardrail doing its job, not an outage.
Console.WriteLine($"{s.GuardrailBlockCount} turn(s) refused · {s.ErrorCount} failed");

foreach (var m in s.Models)
{
    // RequestsWithoutUsage: calls the provider served and billed but reported no token counts for
    // — a turn the user stopped mid-stream is the usual cause. They are counted but contribute
    // nothing to the token totals, so per-request averages stay truthful.
    var note = m.RequestsWithoutUsage > 0 ? $" ({m.RequestsWithoutUsage} without usage)" : "";
    Console.WriteLine($"  {m.ModelId,-24} {m.RequestCount,4} req{note}  {m.TotalCost?.ToString("C") ?? "n/a"}");
}
```


**`MentorModelUsage`** — one row per model, and the reason the dashboard has a model selector:

| Member | Type | Meaning |
|---|---|---|
| `ModelId` | `string` | The id the provider reported, folded onto your configured model |
| `Kind` | `MentorModelKind` | `Cheap`, `Strong`, `Embedding` or `Other` |
| `InputTokens` / `OutputTokens` / `TotalTokens` | `long` | This model's tokens |
| `RequestCount` | `long` | Calls served by this model |
| `RequestsWithoutUsage` | `long` | Calls the provider billed but reported no token counts for — a stopped turn, typically. **Counted here rather than as zero tokens**, so averages stay honest |
| `AvgLatencyMs` / `AvgTimeToFirstByteMs` | `double?` | Weighted means; `null` when nothing was timed |
| `InputCost` / `OutputCost` / `TotalCost` | `decimal?` | `null` when unpriced |
| `Series` | `IReadOnlyList<MentorUsagePoint>` | Hourly points: `Timestamp`, token counts, `RequestCount`, `AvgLatencyMs` |

> Cost is `null`, not `0`, for a model with no `ModelPricing` entry, and the aggregate sums only the
> priced models. A partially-priced deployment — the normal state when a new model is added before
> its price reaches configuration — therefore neither hides what it knows nor quietly reports the
> new model as free.

---

## Model routing

Route each turn to a **cheap** model for simple messages and a **strong** model for complex ones — a real cost lever. `ChatClient` is the cheap default; set a strong client and pick a **routing strategy**. Routing is active only when `StrongChatClient` is set.

```csharp
// options.ChatClient stays the cheap default (e.g. gpt-4o-mini)
options.StrongChatClient = new AzureOpenAIClient(endpoint, credential)
    .GetChatClient("gpt-4o").AsIChatClient();                     // strong (escalation)

options.RoutingStrategy = MentorRoutingStrategy.Semantic;         // pick a strategy (default: Custom)
```

Four strategies — all avoid naive keyword matching on user text:

| Strategy | How it decides | Extra cost per turn | Notes |
|---|---|---|---|
| `Semantic` | Embeds the user message; escalates when cosine similarity to a "complex" exemplar ≥ `RoutingThreshold` (0.35) | 1 embedding (~free) | Multilingual; **requires** `EmbeddingGenerator`. Override `RoutingComplexExemplars` for your domain |
| `Classifier` | A tiny LLM call labels the turn `SIMPLE`/`COMPLEX` | 1 small LLM call | Most precise. Uses the cheap client, or `RoutingClassifierClient` |
| `Cascade` | Serves on cheap, judges completeness, re-runs on strong only if it fell short | cheap + judge (+ strong on hard turns) | Non-streaming calls only. Chat turns always stream (widget, hub, SSE), and there `Cascade` pre-decides exactly like `Classifier`: one label call, no cheap-first attempt |
| `Custom` | Your own predicate — `UseStrongModelAsync` (async, whole conversation) or legacy `UseStrongModel` | none | The escape hatch |

```csharp
// Custom async router — full control, sees the whole conversation:
options.RoutingStrategy = MentorRoutingStrategy.Custom;
options.UseStrongModelAsync = async (messages, ct) => await MyClassifier.IsComplexAsync(messages, ct);

// Legacy synchronous predicate — sees only the latest user message. Prefer the async form above.
options.UseStrongModel = message => message.Length > 500;
```

**Tuning `Semantic`.** The two knobs below are the ones worth touching, and the defaults are chosen
deliberately:

```csharp
options.RoutingStrategy = MentorRoutingStrategy.Semantic;

// Cosine floor to escalate. Higher = stricter = cheaper, and more complex turns served badly.
options.RoutingThreshold = 0.35f;                     // default

// Replaces the built-in multilingual set — it does not merge with it. Write exemplars in the
// language(s) your users actually type, and describe the KIND of work, not the topic.
options.RoutingComplexExemplars =
[
    "analizza questo contratto e riassumi le clausole di recesso",
    "confronta le tre offerte e spiega quale conviene e perché",
    "write and explain a SQL query that joins orders, customers and refunds",
    "debug this stack trace and propose a fix"
];
```

> Leaving `RoutingComplexExemplars` unset — or binding it yourself from a configuration where it arrives as an
> **empty array** — falls back to the built-in multilingual set (silently, nothing is logged) rather than matching
> nothing. That distinction matters: a set that matches nothing does not throw, it silently keeps
> every turn on the cheap model, and the only symptom is an assistant that answers a little worse.
> Setting your own genuinely **replaces** the built-in one, so a set narrowed to exclude coding
> stops paying for coding escalations.

```csharp
// Classifier: by default the label is produced by the cheap client. Point it elsewhere to keep
// the routing decision off your main deployment's rate limit.
options.RoutingStrategy       = MentorRoutingStrategy.Classifier;
options.RoutingClassifierClient = new AzureOpenAIClient(endpoint, credential)
    .GetChatClient("gpt-4o-mini-router").AsIChatClient();

// The same argument, for the decisions MentorAgent makes on its own behalf: the input safety
// check, the output safety check, the hosted-tool scope check and the post-turn fact extraction. They send a few hundred
// tokens and read back one word, and the safety check runs *before* the reply the user is waiting
// for — measured on gpt-4.1: 707–2091 ms for two tokens. A small deployment answers the same
// question in a fraction of the time and the price.
options.ClassifierChatClient = new AzureOpenAIClient(endpoint, credential)
    .GetChatClient("gpt-4.1-mini").AsIChatClient();

// And pay the shared start-up cost before anyone is waiting for it: the tool catalogue's
// embeddings, the shared MCP connections and the remote agent cards are the same for every
// visitor, and without this the first one to arrive pays for all three.
options.WarmUpAtStartup = true;

// Optional, with WarmUpAtStartup: one minimal request to ChatClient (and StrongChatClient, when set) once the
// coordinator is built — its instructions, tools and CoordinatorSampling, ChatToolMode.None, MaxOutputTokens 16.
// The first turn then finds the connection open, the provider's prompt cache warm and a local model loaded.
// Every start costs one request per client; a failure is a warning and the application goes on.
options.WarmUpChatModel = true;
```

The decision uses the latest user message and applies to the whole turn; the chosen model is logged at `Debug` level (`… routing → strong/cheap model`; set the `Novior.MentorAgent.Middleware.RoutingChatClient` category to `Debug` to see it). Any routing failure falls back to the cheap model. Semantic without an `EmbeddingGenerator`, or Custom without a predicate, disables routing (cheap only) with a warning. The [dashboard](#token--cost-dashboard) attributes tokens and cost **per model**, so cheap vs strong spend is broken out separately. Note: the routing decision's own auxiliary calls (Classifier's label, Cascade's cheap probe + completeness judge) are made below the token meter and are not counted.

## Structured outputs

Ask the model for a **strongly-typed** result (its JSON schema is derived from your type) — ideal for auto-filling forms, extracting entities, or typed generative UI, outside the chat flow. Inject `IMentorStructured`:

```csharp
public record ExtractedOrder(string Customer, string[] Products, decimal Total);

var order = await structured.GenerateAsync<ExtractedOrder>(
    prompt: userText,
    instructions: "Extract the order details from the text.");
```

Returns `default` when the output can't be parsed, when no `ChatClient` is configured, or when the call itself fails. A provider error is logged as a warning, not thrown, so check the log before concluding the model found nothing. The call goes straight to `ChatClient`: it is not routed and not counted on the [dashboard](#token--cost-dashboard). Requires a provider with structured-output support (OpenAI / Azure OpenAI).

## Rich responses (tables & lists)

The chat-facing companion to [structured outputs](#structured-outputs): the widget renders the assistant's **Markdown** as real UI — **tables** for tabular/comparative data, bullet/numbered **lists** (a numbered list that resumes after a paragraph keeps counting, BUG-216), fenced code, **bold**/*italic*, and **links** to `http`/`https` addresses, which open in a new tab (`rel="noopener noreferrer nofollow"`); any other scheme and a relative address stay text (BUG-215). With `EnableRichResponses` on (the default), the coordinator is nudged to format data this way:

```csharp
options.EnableRichResponses = true;   // default — set false for terse plain-text replies
```

Nothing to wire on the client — ask the assistant something tabular (e.g. *"list the 5 cheapest products with price and stock"*) and the reply renders as a table that scrolls horizontally on narrow widgets. The renderer is **XSS-safe by construction**: every piece of model text is HTML-encoded before any tag is emitted, so the model supplies *data*, never markup. A link's address is encoded like the rest, and only an absolute `http` or `https` one becomes a link, so `javascript:` and `data:` stay text.

This is Level 1 — Markdown the **model** writes. [Cards](#generative-ui--cards-level-2) are the level above, where the structure comes from **your code**.

---

## Generative UI — cards (Level 2)

Level 1 asks the model to *describe* data well. Level 2 stops asking: a tool returns the structure and the widget draws it.

```csharp
[MentorAction(Description = "Shows an order")]   // tool name from the method: GetOrder → get_order
public MentorCard GetOrder(int id)
{
    var o = _orders.GetById(id)!;
    return new MentorCard("order")
    {
        Title    = $"Order #{o.Id}",
        Subtitle = o.CustomerName,
        Accent   = o.Status == "Shipped" ? MentorCardAccent.Success : MentorCardAccent.Warning,
        Fields   = [ new("Total", o.Total.ToString("C")), new("Status", o.Status) ],

        // Optional thumbnail. It has to be reachable by the BROWSER, not by the server — a path
        // behind your API's auth renders as a broken image in the card.
        ImageUrl = o.ThumbnailUrl,

        Actions  =
        [
            new("Open",    MentorCardActionKind.Navigate,    $"/orders/{o.Id}"),
            new("Details", MentorCardActionKind.SendMessage, $"Give me the full details of order {o.Id}"),
        ],
    };
}
```

Return `MentorCard` or `IEnumerable<MentorCard>` — that is the entire integration. No component to register, no event to subscribe to, nothing to change on the client.

**Only from a method that returns cards.** A card comes only from a tool whose method is declared to return `MentorCard` or a sequence of them — `IEnumerable<MentorCard>`, `IReadOnlyList<MentorCard>`, `List<MentorCard>`, `MentorCard[]`, `IAsyncEnumerable<MentorCard>` — directly or through `Task<…>` or `ValueTask<…>` (BUG-212). That result reaches MentorAgent as JSON, so inside it a card is recognised by its shape: an object with a non-empty string `kind`, or a non-empty array whose every element has one. Any other result stays text and reaches the model whole, even with that shape — a record of yours with a `kind` property of its own, an anonymous object, a `JsonElement` built by hand — and a Debug line names the tool whose result looked like a card. A tool of an MCP server has no method behind it and makes no cards. Up to 1.1.0 the shape alone was enough: a method that returned cards as `object`, as a `JsonElement` built by hand or as a type of its own now shows them as text, so declare it `MentorCard` or `IEnumerable<MentorCard>`.

### The card shape

| Member | Type | Notes |
|---|---|---|
| `Kind` | `string` | Constructor argument, required. Your own name for the type — `"order"`, `"product"`. The built-in renderer ignores it; a `CardTemplate` uses it to pick a component |
| `Title` | `string?` | Heading |
| `Subtitle` | `string?` | Secondary line under the title |
| `Fields` | `IReadOnlyList<MentorCardField>?` | Label/value rows, in display order |
| `Actions` | `IReadOnlyList<MentorCardAction>?` | Buttons. Two or three — a card is a summary, not a form |
| `ImageUrl` | `string?` | Absolute URL or a `data:` URI |
| `Accent` | `MentorCardAccent` | `Default` · `Success` · `Warning` · `Danger` · `Info` — state at a glance |

`MentorCardField(string Label, string? Value)` and
`MentorCardAction(string Label, MentorCardActionKind Kind, string Value)` are both positional
records, which is why the example can write `new("Total", …)` and `new("Open", …, …)`.

Every value is rendered as **text**, never as markup — see [Why cards can have buttons](#why-cards-can-have-buttons).

### Why cards can have buttons

**A card is built by your tool, never by the model.** The model only chooses *when* to call the tool; every label, URL and action name in the result was written by your code.

That is what makes buttons safe here. A card cannot gain a button, lose one, or have one repointed by something said in the conversation — so a prompt injection cannot turn the assistant's reply into a link to an attacker's page. It is also why a card cannot hallucinate a field: there is no model output in it. Stored data cannot become a card on its own: only a method declared to return `MentorCard` makes one, and its buttons are the ones its code builds (see *Only from a method that returns cards* above).

The trade-off is deliberate: cards are not "the model designs UI". If you want the model to fill a layout you choose, use [structured outputs](#structured-outputs) and render the typed result yourself.

### What the model is told

The cards do not go back to the model as JSON. It receives a compact summary plus one line saying they are already on screen:

```
5 card(s) are ALREADY DISPLAYED to the user. Comment or summarise briefly; do NOT repeat
every field, and do not describe the buttons.
- Order #1001 — Mario Rossi (Status: Delivered; Total: € 2.599,98)
…
```

Without that, the model pays for every label twice — once to render, once to read — and then dutifully reproduces the whole card as a Markdown table underneath it, which is exactly the duplication cards exist to remove. Measured on the sample: the assistant answers *"I have shown the order cards on screen"* and moves on to suggestions.

### Button kinds

| `MentorCardActionKind` | What pressing it does |
|---|---|
| `SendMessage` | Sends `Value` as if the user had typed it — good for drill-down |
| `Navigate` | Goes to `Value`, an application route your tool chose |
| `UIAction` | Runs the UI action named `Value`, registered by the **current** page. Resolved at click time: if the user has navigated away, it is logged and skipped rather than firing a handler from a disposed component |

### Custom rendering

The built-in renderer covers title, subtitle, fields, an image, an accent and buttons. When you want your own component for one kind, supply a template — and fall back for the rest:

```razor
<ChatWidget>
    <CardTemplate Context="card">
        @if (card.Kind == "order") { <OrderCard Card="card" /> }
        else                       { <MentorCardView Card="card" /> }
    </CardTemplate>
</ChatWidget>
```

`Kind` is your own name for the card type — keep it stable, it is the card's contract with the UI.

**Buttons inside a template.** The widget cascades its own card-action dispatcher around the cards, so a `<MentorCardView Card="card" />` rendered inside a `CardTemplate` without `OnAction` uses it: its `SendMessage`, `Navigate` and `UIAction` buttons work exactly as on the built-in renderer. An explicit `OnAction` on that `MentorCardView` still wins. Buttons your own component draws (`OrderCard` above) do **not** get the dispatcher — render `MentorCardView` for the cards whose buttons should work, or handle the press yourself.

### Turning it off

```csharp
options.EnableGenerativeCards = false;   // default true
```

The cards still reach the model, so the assistant keeps answering; it describes the data in text instead of showing it.

> **One implementation note that matters if you write middleware.** `AIFunctionFactory` marshals tool
> results through JSON, so a method declared as returning `MentorCard` arrives at the middleware as a
> `JsonElement`. Cards are therefore recognised by shape, not by CLR type — but only in the result
> of a method declared to return `MentorCard` or a sequence of them (the function's
> `UnderlyingMethod`), and strictly, because every ordinary tool result arrives the same way and a
> false positive would replace a real answer with an empty card.

---

## Declarative agents (YAML)

Level-2 specialists are normally C# classes with `[MentorAgent]`. The optional **[Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative)** package lets you declare one in a file instead:

```yaml
kind: Prompt
name: ShippingAgent
description: Answers questions about deliveries and shipping costs
instructions: |
  You handle shipping questions only. Use the tools to look up real orders;
  never invent a tracking number.
model:
  options:
    temperature: 0.2
tools:
  - kind: function
    name: get_order_status
```

```csharp
builder.Services.AddMentorAgentDeclarative(o => o.Directory = "Agents");
```

Both kinds land in the same handoff graph, so `route_to_specialist` reaches them identically.

Save the definition as `Agents/shipping.agent.yaml`. By default only `*.agent.yaml` files are picked up (`SearchPattern`). `Directory` is resolved against the application's base directory, so copy the files to the output:

```xml
<ItemGroup>
  <Content Include="Agents\**\*.agent.yaml" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Declarative agents run on `ChatClient` (Path A). With a pre-built `Agent` they are skipped and a warning is logged.

The `tools` section **names** tools; it does not define them. Only `kind: function` entries are kept: their names are resolved against the Level-1 tools your application already registers, as the session has them, and they arrive **already wrapped in the gate** — so `RequiredRoles`, human approval, action feedback and per-tool metrics keep working inside the agent's own function-calling loop. Entries of kind `webSearch`, `codeInterpreter`, `fileSearch` or `mcp` are **removed** from the agent with a warning (`removed web_search …`); web search, code interpreter, file search and MCP are configured by the host (`HostedTools`, `McpServers`), never by a definition file. A `kind: function` entry that names no action of the session — unknown to the application, or off for this session — is removed the same way, with a warning (`removed get_order_statu …`), so an action that is off reaches no model through a definition that names it. Both are reported to the observers below (`hosted-tool-removed`, `tool-not-found`). A definition file gets no capability the application did not already have.

> **A definition file is code.** It chooses the model, writes the system instructions and names the
> callable tools. Load it only from deploy-time locations and review it like a `.cs` file.

To supply agents from somewhere else entirely — a database, a configuration service — implement `IMentorAgentSource` and register it. `AddMentorAgentDeclarative` is one implementation of that interface, not a privileged path.

**When a source is asked.** A source is asked whenever a coordinator is built: at least once per session — once per Blazor circuit, MAUI app scope or hub connection, and at every request on `/mentor/chat` and every task over A2A, where a request is a session of its own. A turn never asks again. `MentorAgentSourceContext.SessionId` names the session, with the same opaque id as `MentorActionRequest.SessionId` and `MentorActionDecision.SessionId`, so a source can give each session the agents of one version. The coordinator that `WarmUpAtStartup` builds has a session of its own.

**What it left out.** MentorAgent calls `IMentorAgentSource.LoadAsync`, which returns a `MentorAgentSourceResult`: the agents, and a `MentorAgentRejection` for each definition left out or tool taken away. Its default body returns the agents of `GetAgentsAsync` and leaves nothing out, so a source that implements only `GetAgentsAsync` works as before. One bad definition is a rejection, not an exception. A source that throws is skipped and reported as `source-failed`, with the exception's type and never its message; the other sources still load. A source that reports what it left out:

```csharp
public async Task<MentorAgentSourceResult> LoadAsync(MentorAgentSourceContext context, CancellationToken ct)
{
    var agents = new List<AIAgent>();
    var rejected = new List<MentorAgentRejection>();
    foreach (var definition in await store.ForSessionAsync(context.SessionId, ct))
    {
        try { agents.Add(await factory.CreateFromYamlAsync(definition.Yaml, ct)); }
        catch (Exception ex) { rejected.Add(new(definition.Name, MentorAgentRejectionReasons.InvalidDefinition, ex.Message)); }
    }
    return new(agents, rejected);
}
```

**Who hears it.** An `IMentorAgentSourceObserver`, registered with `AddMentorAgentSourceObserver<T>()` (a singleton; every one registered is called), receives one `MentorAgentSourceReport` per source every time a coordinator is built, also when the source loaded nothing. It is called on the path that builds the session, before the first answer: return quickly, and hand anything slow — a call to another service — to a queue of your own. An observer that throws is logged with the exception's type and stops nothing. Each source that left something out also writes one Warning, `[MentorAgent] Agent source <type> left out: <name> (<code>), <tool> of <name> (<code>)…`.

```csharp
builder.Services.AddMentorAgentSourceObserver<AgentLoadLog>();

public sealed class AgentLoadLog(ILogger<AgentLoadLog> log) : IMentorAgentSourceObserver
{
    public void OnAgentsLoaded(MentorAgentSourceReport report)
    {
        log.LogInformation("{Source} at {Time}: {Agents}", report.Source, report.Timestamp, string.Join(", ", report.Loaded));
        foreach (var r in report.Rejected)   // r.Tool is set when only one tool of a loaded agent was taken away
            log.LogWarning("{Source} left out {Name} {Tool}: {Code} {Detail} (session {Session})",
                report.Source, r.Name, r.Tool, r.ReasonCode, r.Detail, report.SessionId);
    }
}
```

`MentorAgentSourceReport`:

| Property | Type | Meaning |
|---|---|---|
| `Source` | `string` | The full type name of the source |
| `SessionId` | `string?` | The session the coordinator was built for |
| `Loaded` | `IReadOnlyList<string>` | The names of the agents that joined the coordinator, in the order the source gave them |
| `Rejected` | `IReadOnlyList<MentorAgentRejection>` | What was left out; empty when everything loaded as written |
| `Timestamp` | `DateTimeOffset` | When the source answered |

`MentorAgentRejection`:

| Property | Type | Meaning |
|---|---|---|
| `Name` | `string` | What was left out: for the Declarative package the file name, or `inline[n]` for the n-th of `Definitions`; for `source-failed`, the source's full type name |
| `ReasonCode` | `string` | One of `MentorAgentRejectionReasons`, or a code of the source's own |
| `Detail` | `string?` | What to correct: for `invalid-definition` the agent factory's message, which describes the text; for `source-failed` the exception's type, never its message |
| `Tool` | `string?` | The tool taken away, when the agent loaded without it; `null` when the whole definition was left out |

The codes of `MentorAgentRejectionReasons` stay the same from one version to the next; a new one comes only in a minor version, and the CHANGELOG says so:

| Code | When |
|---|---|
| `invalid-definition` | The agent factory refused the text: it does not parse, does not describe a valid agent, or describes something else — a workflow in a file meant for an agent (`Detail`: `Unsupported root element: …`) |
| `not-an-agent` | The definition gave no agent and no error. The Agent Framework's factory refuses another kind instead (`invalid-definition`): this is for a factory or a source that answers with nothing |
| `foreign-tools` | The definition adds tools it may not add, or names a function the session does not have, and they could not be taken away: the agent is not loaded |
| `invalid-configuration` | The configuration exposed to the definitions is not valid for Power Fx: every definition of the source is left out |
| `source-failed` | The whole source threw (added by MentorAgent) |
| `tool-not-found` | With `Tool`: a `kind: function` entry names no action of this session — unknown, or off for it. The agent loads without it |
| `hosted-tool-removed` | With `Tool`: a `webSearch`, `codeInterpreter`, `fileSearch` or `mcp` entry was taken away. The agent loads with the application's own tools |

**Nothing is added at runtime.** There is no API to hand MentorAgent agents or YAML texts while the application runs: a source supplies them when it is asked.

See the [package README](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) for the YAML reference, including two format details the Agent Framework guide does not document: every `tools` entry needs `kind`, and folded scalars (`>`) are not supported.

## Voice input and output

Two independent halves, both built on the browser's native Web Speech API — no provider to configure, and your server receives only the transcript, as an ordinary message. The browser is another matter: in some browsers, Chrome among them, speech recognition runs on a service of the browser's maker, so the user's audio leaves the device ([MDN, *Using the Web Speech API*](https://developer.mozilla.org/en-US/docs/Web/API/Web_Speech_API/Using_the_Web_Speech_API)). Weigh that before turning on `EnableVoiceInput` where what people say is personal data.

```csharp
options.EnableVoiceInput  = true;   // 🎤 microphone in the composer  (Chrome/Edge; HTTPS in production)
options.EnableVoiceOutput = true;   // 🔊 the assistant reads answers aloud (all modern browsers)

options.VoiceStreaming = true;      // default — speak sentence by sentence while the answer streams
options.VoiceBargeIn   = true;      // default — taking the floor stops playback
options.VoiceHandsFree = false;     // opt-in — keep the conversation going by voice alone
options.VoiceRate      = 1.0;       // 0.5–2.0
```

### Speaking while the answer streams

`VoiceStreaming` is the difference between a demo and something usable. Voice output originally waited for the whole response and then recited it, which is fine for one line and unbearable for a paragraph — the assistant sits silent for several seconds, then starts talking about something the user has already read.

The unit that can be spoken is a **sentence**: a `SpeechSynthesisUtterance` cannot be extended once queued, so a half-sentence cannot grow when the next chunk lands. Text is buffered until a sentence boundary and queued as its own utterance; the browser's own FIFO queue plays them back to back, so the seams are inaudible. Three details that are not obvious:

- **A decimal point is not a full stop.** `8.459` must not become two utterances.
- **A fenced code block is dropped whole, never half-open.** The newlines inside one all look like sentence ends, and cutting on them would leave a fragment the Markdown stripper cannot recognise — so the code gets read out loud, character by character.
- **Chunks are batched before crossing into JavaScript.** One interop call per token would put a SignalR round-trip on every chunk of every answer on Blazor Server; the widget accumulates and hands over a sentence at a time.

### Barge-in

`VoiceBargeIn` stops playback the moment the user takes the floor — presses the microphone, sends a message, presses **■ Stop**, or starts a new conversation.

This is **press-to-interrupt, not acoustic** barge-in, and the distinction is worth stating plainly: a browser cannot reliably listen through its own playback without echo cancellation, and a microphone left open during synthesis transcribes the assistant. Interrupting by *speaking* over the assistant is not something the Web Speech API supports, and MentorAgent does not pretend otherwise.

### Hands-free

`VoiceHandsFree` turns the microphone button into a session: the transcript is sent when the user stops speaking, and the microphone reopens once the spoken answer has finished. It is off by default because it holds the microphone across turns, which no application should do unasked. It requires both `EnableVoiceInput` and `EnableVoiceOutput` — with nothing to listen *to*, there is nothing to wait for before reopening, and the loop talks over itself. If you set only one, the option is ignored.

Recognition stays single-utterance even in this mode, and the loop is built by restarting it rather than leaving it open, precisely so the microphone can be held shut while the assistant speaks.

> **What this is not.** True speech-to-speech (`gpt-4o-realtime`, WebRTC audio, server-side voice activity detection) is a different feature with a provider dependency and its own transport. The Agent Framework has no voice surface at all, so nothing here is an Agent Framework integration — it is the Web Speech API used properly.

---

## Onboarding tour

The hardest part of shipping an in-app assistant is not discovery — users find the floating button — it is that they do not know what to *ask* it. The tour answers that with examples drawn from the application's own capabilities.

```csharp
options.EnableOnboardingTour = true;   // shown once, on the user's first open of the widget
```

The steps are **generated from what the application actually has**: the pages registered with `[MentorPage]` and the descriptions of the assistant's own tools — those that need no role only: the tour is one for every visitor, generated once per process without reading the user, and an action some of them may not use is not one to suggest. That is the whole point — a hardcoded tour is correct on the day it is written and wrong three releases later, whereas this one cannot describe a screen that no longer exists.

Each step can carry a ready-made question the user sends with one tap:

```jsonc
{
  "title": "Orders",
  "body":  "Track what customers ordered and update their status.",
  "url":   "/data/orders",
  "tryAsking": "Quanti ordini sono in attesa?"
}
```

Three properties worth knowing:

- **Generated once per process, shared by everyone.** A tour is identical for every visitor, so paying per user would be waste. Concurrent first-time visitors collapse onto a single generation.
- **Invented URLs are dropped.** A model asked to describe screens will happily produce a plausible one that does not exist. Every generated URL is checked against the registered pages; an unknown one loses its link and keeps its text, because a first-run tour whose button leads to a 404 is worse than no tour.
- **It always has something to show.** With no `ChatClient`, or if generation fails or returns nothing usable, it falls back to a deterministic tour built from the same discovery data — no tokens, no model.

A **?** button in the widget header replays it on demand: a tour that can only ever be seen once, by accident, on the very first visit, is not worth generating.

#### Acting on a step suspends the tour — it does not end it

Tapping a step's question or opening its screen closes the tour and **remembers the position**: next
time the widget opens it resumes at the following step, on its own. Only *Skip* and reaching the end
mark it finished, and those also clear the saved position so the replay button starts over.

The distinction matters because the two look the same from the outside and behave very differently.
Treating "the user took the tour's suggestion" as "the user is done" means engaging with step 2 of 6
silently forfeits steps 3 to 6 — a penalty for using the tour exactly as intended.

Two keys in `localStorage`, both per browser profile:

| Key | Meaning |
|---|---|
| `mentoragent.tour.seen` | `"1"` once skipped or completed — suppresses the automatic first run |
| `mentoragent.tour.step` | Zero-based step to resume at while the tour is unfinished; removed when it is |

> **"Per user" means per browser, not per account.** `localStorage` is scoped to the browser profile
> and the origin, so the same person on a second device sees the tour again, and two people sharing
> one profile share one tour — the second never sees it. If your app is authenticated and you need
> the tour to follow the identity instead, register your own `IMentorTour` and key the state on your
> user id server-side.

### Writing the tour by hand

Register your own `IMentorTour` **before** `AddMentorAgent()` and the generated one steps aside:

```csharp
public sealed class ScriptedTour : IMentorTour
{
    public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MentorTourStep>>(
        [
            new("Welcome", "This is the ops console."),
            new("Orders", "Everything customers bought.", "/orders", "Show me today's orders"),
        ]);
}

builder.Services.AddSingleton<IMentorTour, ScriptedTour>();   // BEFORE AddMentorAgent()
builder.Services.AddMentorAgent(o => { o.EnableOnboardingTour = true; /* … */ });
```

---

## Multimodal image input

Let users **send images to the assistant** — a screenshot of an error, a photo of a receipt, a product picture — and have the model reason about them. Built on the Agent Framework's native multimodal API: the user turn becomes a `ChatMessage` carrying a `TextContent` plus one `DataContent` (inline upload) or `UriContent` (remote URL) per image.

Off by default. Enable it and configure the safety limits:

```csharp
builder.Services.AddMentorAgent(options =>
{
    options.EnableImageInput     = true;                    // shows the 📎 and 🔗 buttons in the widget
    options.MaxImageBytes        = 4 * 1024 * 1024;         // per image (default 4 MB)
    options.MaxImagesPerMessage  = 4;                       // per turn (default 4)
    options.AllowedImageTypes    = ["image/png", "image/jpeg", "image/webp"];   // MIME allow-list
});
```

> **Your chat model must be vision-capable** — e.g. `gpt-4.1`, `gpt-4o`, `o3`. A text-only deployment will reject the request.

### What the user gets

Once enabled, the widget's composer accepts images in **four ways**, with no extra code:

| Method | How |
|---|---|
| **Upload** | 📎 button → native file picker (multi-select) |
| **Paste** | `Ctrl+V` an image straight from the clipboard (screenshots) |
| **Drag & drop** | Drop a file onto the composer — or drag an `<img>` from another page (its URL is attached) |
| **URL** | 🔗 button → paste a public image link |

Attached images appear as removable thumbnails above the input, then inside the sent message bubble. Client-side checks mirror the server limits so the user gets an instant message; **the server re-validates every attachment** (allow-list, size, count) before anything reaches the model — per the AF *Agent Safety* guidance, this is an allow-list, never a deny-list.

### Sending images from your own code

`IMentorOrchestrator` takes an optional attachment list. Text-only calls are unchanged:

```csharp
await orchestrator.SendMessageAsync("What's wrong in this screenshot?", [
    new MentorAttachment
    {
        MimeType   = "image/png",
        DataBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync("error.png")),
        FileName   = "error.png",
    },
    new MentorAttachment { MimeType = "image/jpeg", Url = "https://cdn.example.com/product.jpg" },
]);
```

`MentorAttachment` carries exactly one source — `DataBase64` (inline) **or** `Url` (remote) — and converts itself to the right AF content type. An **image-only turn** (empty text) is valid.

Attachments are honoured only when `EnableImageInput` is on. With it off they are ignored and the turn goes out as text. An attachment that fails validation is dropped with no error to the caller, and the rest of the turn is still sent. That covers a type not in `AllowedImageTypes` and inline data over `MaxImageBytes` (both logged as a warning), and anything past `MaxImagesPerMessage` (dropped without a log line). An image-only turn whose images are all rejected therefore reaches the model as an empty message. Only inline data can be size-checked: a `Url` attachment is checked against its declared `MimeType` alone.

### Transport

Attachments flow over every transport (see the [.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) README for JS/React/Vue/Angular/MAUI examples):

| Transport | Call |
|---|---|
| Blazor Server (in-process) | `SendMessageAsync(text, attachments)` |
| SignalR hub | `SendMessage(text, attachments)` — the trailing argument is optional, so 1-argument clients keep working |
| SSE | `POST /mentor/chat` with `{ "message": "...", "attachments": [ … ] }` (base64 does not fit in a query string; `GET` remains for text-only) |

> **Blazor Server note:** the widget never marshals image bytes through a single interop call — it streams them via `IJSStreamReference`, so you do **not** need to raise `HubOptions.MaximumReceiveMessageSize`.

## Hosted tools (web search, code interpreter, file search, images, remote MCP)

Your `[MentorAction]` methods let the assistant act **on your application**. Hosted tools let it do things **no application code can do**: look up today's news, compute an exact number, read an uploaded PDF, draw an image, call someone else's MCP server.

They are the Agent Framework's **provider-hosted tools** — they run on the **model provider's** infrastructure while it generates the answer. You write no implementation and nothing executes on your servers; you only declare which ones the model may use.

| Tool | What the model gains | Typical question it unlocks |
|---|---|---|
| **Web search** | Information past the training cut-off | *"What changed in .NET 10?"* |
| **Code interpreter** | Exact computation in a provider-side sandbox | *"Sum the first 500 primes"* — no more invented digits |
| **File search** | Retrieval over documents in the provider's vector store | *"What does the contract say about penalties?"* |
| **Image generation** | Images returned inline in the message bubble | *"Draw a banner for the summer sale"* |
| **Hosted MCP** | Tools from a remote MCP server, called by the provider | *"Search the Microsoft Learn docs"* |

```csharp
builder.Services.AddMentorAgent(options =>
{
    options.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter;

    // File search needs at least one vector store — without it the tool is skipped (fail-closed)
    // options.HostedTools |= MentorHostedTools.FileSearch;
    // options.FileSearchVectorStoreIds = ["vs_abc123"];
    // options.FileSearchMaxResults     = 5;

    // Image generation — see the Azure note below, this option alone is not enough there.
    // options.HostedTools |= MentorHostedTools.ImageGeneration;
    // options.HostedImageModel = "gpt-image-1-mini";
    // options.HostedImageSize  = "1024x1024";   // the cost knob

    // Hosted MCP: the PROVIDER connects to the server, so it must be reachable from the
    // provider's network (no localhost). Approval is enforced by the provider.
    // options.HostedTools |= MentorHostedTools.HostedMcp;
    // options.HostedMcpServers = [
    //     new MentorHostedMcpServer
    //     {
    //         Name            = "microsoft_learn",
    //         Url             = "https://learn.microsoft.com/api/mcp",
    //         AllowedTools    = ["microsoft_docs_search"],
    //         RequireApproval = true,     // default — the provider pauses and MentorAgent shows the banner
    //     }
    // ];

    options.ShowHostedToolsStatus  = true;   // amber badge in the widget header (dev)
    options.ShowHostedToolActivity = true;   // default — live "Searching the web…" + citations
});
```

> **Image generation on Azure OpenAI needs a request header.** Azure resolves which image deployment
> the hosted tool should use from `x-ms-oai-image-generation-deployment`, **not** from the tool payload —
> `HostedImageModel` alone leaves every image turn failing with
> *"imagegen deployment must be provided through header"*. MentorAgent is handed an already-built
> `IChatClient`, so it cannot add the header; attach it where you construct the Azure client:
>
> ```csharp
> var azureOptions = new AzureOpenAIClientOptions();
> azureOptions.AddPolicy(new ImageDeploymentHeaderPolicy("gpt-image-1-mini"), PipelinePosition.PerCall);
> var azureClient = new AzureOpenAIClient(endpoint, credential, azureOptions);
> ```
>
> `ImageDeploymentHeaderPolicy` is a ~15-line `PipelinePolicy` that sets the header on every request —
> see `MentorAgentServer/Infrastructure/ImageDeploymentHeaderPolicy.cs` in the samples. MentorAgent
> repeats the requirement in a startup log line whenever image generation is enabled.

> **Hosted MCP vs `options.McpServers`.** Same protocol, opposite direction. With `McpServers` *your process* is the MCP client: tools are fetched at startup, invoked locally, and pass through MentorAgent's gate (roles, confirmation, metrics). With `HostedMcpServers` the *provider* is the client: your server never contacts the MCP server, so approval is the provider's job via `RequireApproval`.

### What the user sees while a hosted tool runs

A hosted tool runs remotely and can take several seconds with nothing streamed — without feedback the widget looks frozen. With `ShowHostedToolActivity` (on by default) MentorAgent reports it through the channels the widget already has:

- **the action-feedback line** — *"Searching the web… · .NET 10 release notes"*, *"Running code…"*, *"Searching your documents…"*, *"Generating the image…"*
- **citation chips** — the pages a web search used **and the documents a file search matched**, through the same panel as RAG sources (so they also need `ShowRagSources`)
- **inline images** — generated images are attached to the finished message, exactly like an uploaded image
- **a debug log line per call** — the only trace a provider-side tool ran at all, since these never reach the function-calling middleware or the per-tool metrics

Providers differ in how much they report. An unrecognised part is skipped rather than guessed at, and the answer itself never depends on any of it.

Two details worth knowing, because they explain what you will see:

- **File search has no content type of its own** in Microsoft.Extensions.AI 10.6.0 — its call and result arrive as the plain base types, and the documents it matched come back as *annotations on the answer text* rather than as a result part. MentorAgent reads both, which is why file search now shows activity and citations like the others. It attributes an unnamed hosted call to file search only when `FileSearch` is actually enabled: otherwise it logs the call and shows nothing, because a wrong label is worse than none.
- **Hosted MCP is the only hosted tool that can pause for approval.** The request arrives as a normal `ToolApprovalRequestContent` and reaches the same confirmation banner as a local tool, showing the tool name and the remote server (`Microsoft Docs Search · microsoft_learn`) plus the arguments about to be sent.

### Keeping them from firing when they are not needed

Hosted tools are the expensive ones — a web search is billed per call, and a file-search turn injects
thousands of extra input tokens — yet they used to be declared on **every** message, including
*"ciao"*. They were also the one group the semantic tool filter never touched: they are not
`AIFunction`s and carry no description to embed, so while 40 cheap application tools were being
trimmed each turn, the four costly ones went through untouched.

They are now scored like everything else, from an internal catalogue of descriptions:

```csharp
options.EnableToolFiltering = true;          // the existing filter
options.EmbeddingGenerator  = embeddings;    // required — this is semantic, never keywords
options.FilterHostedTools   = true;          // default

// Optional. Default 0.15 — keep it LOW: this is a coarse pre-cut, not the decision
options.HostedToolFilterMinScore = 0.15f;

// Optional: a ceiling that does not depend on a score being right
options.MaxHostedToolCallsPerSession = 10;
```

**But relevance to a tool is not relevance to your application.** *"Draw me a dog"* passes the filter
above with a high score — it genuinely is an image request — and is complete nonsense for a shop
assistant, which pays for the picture anyway. That is a different question, so it gets a different
gate:

```csharp
options.HostedToolDomainCheck = true;        // default false
// options.HostedToolDomainScope = "...";    // null = derived from AppName + AppDescription + page names
```

Before a hosted tool runs, a small model is asked whether the message is something this assistant
should handle at all. It runs **once per turn, and only when a hosted tool has already passed relevance
filtering** — so it costs nothing on ordinary conversation, and a couple of hundred tokens exactly on
the turns where a per-call web search or a generated image was about to be paid for.

> **Why not embeddings here.** This was first built as a cosine threshold against the application's own
> metadata, and it did not work. Raw cosine has no stable zero point: measured on a real turn,
> *"genera immagine di un cane"* scored **0.398** against a shop's page names — high not because it was
> related but because both were short phrases in the same language — and the same request crossed the
> tool threshold on a one-word rewording. The question is comparative (*more like my domain than like
> everything else?*) and a single absolute threshold cannot express it. Embeddings stay where they do
> discriminate: per-tool relevance, where the same turn scored `web_search` 0.04 and `image_generation`
> 0.35.

`HostedToolDomainClassifier` replaces the model call with your own predicate — an existing intent
service, per-user policy, or simply to make the check free.

Three layers, three different jobs. **Domain gating** answers *should this application be spending
anything on this message at all*. **Relevance scoring** answers *which tool, if any*; it is
probabilistic, so it reduces how often a paid tool fires without promising a maximum. **The session
cap** provides the maximum, and it is what survives a message the other two get wrong.

Tuning is done from data, not guesswork — each turn logs every hosted tool's score at Debug:

```
[MentorAgent] Tool filtering: no action cleared minScore=0.35 (best: get_product 0.256); the 3 best-ranked
              that need no confirmation were sent: get_product 0.256, search_products 0.242, search_customers 0.234.
[MentorAgent] Hosted tool relevance: hosted:web_search scored 0.040 (min 0.15) — skipped.
[MentorAgent] Hosted tool relevance: hosted:code_interpreter scored 0.080 (min 0.15) — skipped.
[MentorAgent] Hosted tool relevance: hosted:image_generation scored 0.352 (min 0.15) — declared.
[MentorAgent] Hosted tool relevance: hosted:mcp:microsoft_learn scored 0.038 (min 0.15) — skipped.
[MentorAgent] Hosted-tool domain check → OUT of scope (from the turn's classifier call).
[MentorAgent] Hosted tools withheld: the request is outside this application's scope.
              hosted:image_generation would have run.
[MentorAgent] Tool filtering: 10/63 tools sent (7 core + 3/51 matched + 0/5 hosted, minScore=0.35).
```

(*"genera immagine di un cane"* on the ApiServer sample, rc.14. The first line is the zero-match fallback
of [semantic tool filtering](#semantic-tool-filtering): no action cleared the line, so the three best-ranked read actions
travel anyway.)

### Taking a tool away is only half the job

Withhold a capability and the model still believes it has one: the system prompt is built **once**,
the tool list is decided **per turn**. Asked *"genera immagine di un cane"*, an assistant whose image
tool had just been withheld answered anyway — with an invented URL introduced as *"the image I created
for you"*. A lie to the user, and, whenever such a URL happens to resolve, someone else's licensed
content presented as generated output.

So whenever a hosted tool is taken away — by the domain gate or by the session cap — MentorAgent tells
the model so, in that same request:

```
## Capability status for THIS request — MANDATORY, overrides the capability list above
The built-in capabilities … are NOT available on this turn: they are not for this request. … If the
user asked for one, reply with ONE short sentence saying plainly that you cannot do it — and if the
request is about this application, answer it with this application's own actions. … NEVER substitute
a result you did not receive from a tool in this conversation: no invented or remembered URL, no
Markdown image, no stock photo, no made-up search result …
```

The notice says the capabilities are not *for this request* — never that the request is outside the
application. The gate's verdict is about the paid tools, and it is strict on purpose: until rc.13 the
notice called the request itself out of scope, and a catalogue price question the gate had judged OUT
for web search was then answered as if the application could not help (BUG-106).

Same request, after the fix (rc.14, three runs, all alike):

> Al momento non posso generare immagini, ma posso aiutarti con qualsiasi altra operazione relativa ai
> prodotti, ordini o clienti nel sistema ShopFlow. Vuoi consultare il catalogo prodotti o gestire uno
> specifico ordine?

Two details that are not incidental:

- It travels in **`ChatOptions.Instructions`** (per-request), not as an extra system message. Under
  `UseServiceManagedHistory` a message is *stored* in the conversation, so *"image generation is
  unavailable"* would silently follow the user into every later turn.
- It is **appended** to the agent's instructions, never substituted for them — overwriting would drop
  the persona, the security block and the page list for that turn.

Nothing is sent on turns where nothing was withheld, so ordinary conversation pays nothing and the
cached prompt prefix stays intact. The always-on floor lives in the system prompt itself: the
hosted-tools block states that the list describes what is *configured*, not what is available now, and
that refusing always beats fabricating.

> **Fail-open by design.** All three controls — `FilterHostedTools`, `HostedToolDomainCheck` and
> `MaxHostedToolCallsPerSession` — run **inside** the semantic tool filter, which is only installed
> with `EnableToolFiltering` **and** an `EmbeddingGenerator`. Without those, nothing is filtered and
> hosted tools keep firing on every turn — with a startup warning that names the options it is
> ignoring, because a control that is switched on but never runs is worse than one that is off. An
> assistant losing a capability because a model was not configured is worse than one that costs more
> than it should, and there is no keyword fallback: this project treats guessing relevance from
> substrings as not implementing the feature at all.

> **Duplicate tool names are reported**, once, the first time MentorAgent reads your actions, with tool
> filtering on or off. A model calls a tool by name, so an agent offered both methods reaches only one of
> them, and tool filtering and the `RequiresApproval` and `IsReadOnlyAction` rules decide by name — a call's
> roles, confirmation and page are its own method's. MentorAgent logs
> `N tool name(s) are carried by more than one method: …` with each name and its methods. With filtering
> on they are also quietly expensive: both are declared on any turn the name matches, both consume the
> `ToolFilterMaxTools` budget, and they share one relevance-score cache entry — so the second tool's
> description decides the score for the first. The filter adds
> `N duplicate tool name(s) among the filterable tools: …`, MCP tools included.

### Provider support — read this before enabling

Hosted tools are a **provider capability**, not a MentorAgent one. Availability differs per client:

| Client | Function tools | Web search | Code interpreter | File search | Image gen | Hosted MCP |
|---|:--:|:--:|:--:|:--:|:--:|:--:|
| Azure OpenAI / OpenAI — **Responses** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Azure OpenAI / OpenAI — **Chat Completions** | ✅ | ✅¹ | ❌ | ❌ | ❌ | ❌ |
| Foundry (`AIProjectClient`) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

¹ Depends on the deployment; an unsupported one answers `400 unknown_parameter: web_search_options`.

Availability also depends on the individual **deployment**, not just the client type — a model without image generation enabled rejects the call even on the Responses client.

Enabling a tool the client does not support makes the **provider** reject the call, so MentorAgent logs the enabled set at startup to make the configuration obvious when that happens.

Hosted tools also need a **`ChatClient` (Path A)**. They travel in `ChatOptions.Tools`, and a pre-built `options.Agent` (Path B) carries its own tool list. With `options.Agent`, `HostedTools` is ignored with the startup warning *"HostedTools is set but no ChatClient is configured (Path B)"*. Declare the hosted tools on the agent itself, where you build it.

Switch to the Responses client to get the full set:

```csharp
var azure = new AzureOpenAIClient(endpoint, credential);
options.ChatClient = azure.GetResponsesClient().AsIChatClient(deploymentName);

// ⚠️ Required with Responses: the service owns the conversation and returns a conversation id.
// The Agent Framework refuses to combine that with a local ChatHistoryProvider, so every turn
// would fail with "Only ConversationId or ChatHistoryProvider may be used, but not both".
options.UseServiceManagedHistory = true;
```

With `UseServiceManagedHistory` on, `MaxSessionMessages`, `ChatReducer` and `EnableCompaction` no longer apply (there is no local history to trim — a warning is logged if compaction or a reducer is set). Everything else — tools, memory, RAG, guardrails, metrics — is unaffected.

> **With [model routing](#model-routing):** the strong model receives the *same* tool list, so build `StrongChatClient` on a client that supports hosted tools too. Otherwise turns work until one gets escalated, and then fail — MentorAgent warns at startup when both are configured.

**Limits by design.** Hosted tools never reach MentorAgent's function-calling middleware — there is no local invocation to intercept — so `RequiredRoles`, `RequiresConfirmation`, `OnToolResult` and the per-tool metrics do not apply to them. What you *can* control is what the provider is allowed to do: which tools you declare at all, which vector stores file search may read, which `AllowedTools` a hosted MCP server may expose, and `RequireApproval` on that server (the one hosted tool that can pause for a human, because the provider itself supports it — the approval then arrives as a normal `ToolApprovalRequestContent` and surfaces through the usual banner, in either `HitlMode` and whatever `RequireConfirmation` says: that option switches off MentorAgent's own gate, not the provider's).

**Tell the model they exist.** MentorAgent adds the enabled hosted tools to the coordinator's system prompt automatically. Without that, a coordinator holding a long list of application actions — and instructed never to invent capabilities — answers from memory instead of searching or running code. The block costs a handful of tokens and only appears when hosted tools are on.

## Human-in-the-loop tool approval

Any action marked `[MentorAction(RequiresConfirmation = true)]` pauses and shows the confirmation banner before it runs (see [Confirmation dialogs](#confirmation-dialogs)). Two more ways to gate a tool — useful for tools you don't own and can't annotate:

```csharp
builder.Services.AddMentorAgent(options =>
{
    // 1. Every tool from an MCP server
    options.McpServers = [
        new MentorMcpServer {
            Name = "filesystem", Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/data"],
            RequiresConfirmation = true,          // gate the whole server
        }
    ];

    // 2. By tool name — applies to L1 actions, MCP tools and skills alike
    options.RequiresApproval = tool =>
        tool.StartsWith("delete_") || tool is "send_email" or "write_file";
});
```

**The predicate is asked whenever the answer is needed, not once.** On every call to a tool — in `Native` mode too, where a tool it gates after the coordinator was built asks for confirmation at the call, with the same banner and the same `POST /mentor/approve`, and is refused in a task from another agent with `nobody-to-confirm` (BUG-203); on every `tools/list` and `tools/call` of the MCP server, where an action it holds back is not listed and, called, answers "Unknown tool" (`Denied`, `withheld`) (BUG-202); on every turn the tool filter matches a tool; and for every action an [`IMentorActionPolicy`](#deciding-before-an-action-runs--imentoractionpolicy) is asked about. The startup warning that lists the actions withheld from the MCP server tells their state at startup. A tool whose confirmation is already declared is never asked about. Keep it a cheap, pure function of the tool name: read a file or a database once, outside it.

Following the AF *Agent Safety* guidance, gate anything with **side effects**, that touches **sensitive data**, or that is **irreversible**.

### Two modes: `Blocking` (default) and `Native`

```csharp
options.HitlMode = MentorHitlMode.Native;   // default: MentorHitlMode.Blocking
```

|  | `Blocking` (default) | `Native` |
|---|---|---|
| Mechanism | MentorAgent's middleware parks the AI thread on a `TaskCompletionSource` | AF standard: `ApprovalRequiredAIFunction` → `ToolApprovalRequestContent` → `ToolApprovalResponseContent` |
| Model calls | The same in both modes | The same in both modes. Each round of confirmations adds one agent run, not a model call |
| Streaming | Stays alive across the banner | Reply arrives in two segments (before/after the banner). With output moderation on (`EnableOutputSafetyCheck` or an `OutputGuardrail`), each segment is checked on its own |
| Why choose it | Fewer moving parts, one streamed reply | **Interop** — AF workflows, DevUI and AF-native hosts see the standard protocol |

**The banner and the client protocol are identical in both modes** — same `ConfirmationRequired` event, same reply (`ConfirmAsync` / `Cancel` or `RespondToApprovalAsync` in-process, `POST /mentor/approve` over the wire, never the `RespondToApproval` hub method) — so you can flip the mode without touching a single client. On rejection, both modes send the model the same structured result (see below).

**A declined action reads the same to the model in both modes** (BUG-115): `{"success":false,"declined_by_user":true,…}` with the instruction that nothing was checked or changed and that no other reason, and no state of any record, is to be given — until rc.14 the Native flow sent only a localized sentence, and the sentence both modes carried — "Operazione annullata dall'utente" — read, for a request to cancel an order, as the cancellation being cancelled: on MAUI (Blocking) a reply invented why the order "could no longer be cancelled". **Stop while a banner waits no longer breaks a Native conversation** (BUG-116): the open approval request is answered as not run at the start of the next turn; before, every later turn failed with `ToolApprovalRequestContent found … that have no matching ToolApprovalResponseContent` until a reset.

**The decisions and the log are the same in both modes**: an [`IMentorActionObserver`](#auditing-the-gates-decisions--imentoractionobserver) receives the same decisions, in the same order, and a refused call writes the same single Warning.

### Approving without asking — `AutoApprovalRules`

In `Native` mode approvals travel as Agent Framework content, which lets MentorAgent add the
framework's own `ToolApprovalAgent` middleware. Two things come with it: **"don't ask again"** (a
user approval becomes a standing rule for the rest of the session) and rules that approve a call
before any banner appears.

```csharp
options.HitlMode = MentorHitlMode.Native;

options.AutoApprovalRules =
[
    // A scheduled job or a headless caller has nobody to answer a dialog.
    // Approve reads; let everything else still prompt.
    ctx => ValueTask.FromResult(ctx.FunctionCallContent.Name.StartsWith("get_")),
];

// Safety cap on how many times one turn may re-run because every approval was auto-approved.
options.MaxAutoApprovalIterations = 5;
```

| Option | Type | Default | Description |
|---|---|---|---|
| `AutoApprovalRules` | `IList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>` | empty | Evaluated in order; the first `true` approves the call. Also enables "don't ask again" |
| `MaxAutoApprovalIterations` | `int?` | `null` | Cap on re-runs caused by auto-approval. `null` uses the framework's default |

> **This waives the prompt, never the permission.** `RequiredRoles` is checked inside MentorAgent's
> own tool wrapper, which this middleware sits outside of and never sees — so a rule that approves
> everything still cannot let an unauthorised caller run an `Admin`-only action. One is a dialog the
> user sees; the other is who is allowed to act.

> **Why the cap matters.** Each auto-approved re-run is a fresh, billable model call, and a
> per-request iteration limit restarts every time and cannot bound it. This is the same shape as the
> Level-3 termination defect that once ran 770 model calls against a limit of 2 — leave the cap at
> the framework default unless you have measured a reason.

The middleware is **opt-in**: setting `HitlMode = Native` alone does not add it, because with no
rules its only remaining effect would be "don't ask again", which silently widens what a single
approval covers. Set either option to switch it on.

> **Specialist and team tools are not covered.** Level-2 specialists and Level-3 team members
> always use the blocking confirmation flow (see below), so their gated tools never pass through
> `ToolApprovalAgent`: no `AutoApprovalRules` rule approves them, and "don't ask again" does not
> remember them. A headless caller that reaches such a tool through `route_to_specialist` still stops
> at the confirmation banner. Put operations that must run unattended on a coordinator-level
> `[MentorAction]`.

### Level 2 and Level 3 agents are gated too

Specialists (`[MentorAgent]`) and team members (`[TeamMember]`) run their own function-calling loop inside the handoff / group-chat workflow, where the coordinator's middleware cannot reach. Their tools are therefore wrapped in a `GatedAIFunction`, so the gate travels **with the tool** instead of with the agent:

```csharp
// On a Level-2 specialist class — both are enforced, including when the
// coordinator reaches the method through route_to_specialist:
[MentorAction(Description = "Creates an order",
              RequiresConfirmation = true,
              RequiredRoles = ["OrderManager", "Admin"],
              NavigateTo = "/orders")]
public string CreateOrder(int customerId, string items) { … }
```

Role checks, the confirmation banner, action feedback, `NavigateTo`, `OnToolResult`, `OnException` and the per-tool metrics all apply at every level, through one shared implementation — so a tool behaves identically whether the coordinator calls it directly or a specialist does. At every level the model is also offered only the tools the turn's user may run (see [Role-based actions](#role-based-actions)).

> Nested tools always use the **blocking** confirmation flow, even when `HitlMode = Native`: an Agent Framework `ToolApprovalRequestContent` raised inside a workflow never surfaces to the orchestrator, so it could not reach the user. The banner and the client protocol are identical either way, and the user is asked exactly once.

## Evaluation & regression testing

`MentorEvaluator` is a small prompt/token regression harness. It sends each case straight to your configured `ChatClient` (system prompt = `SystemInstructions`; no tools and no MentorAgent pipeline), reads the provider's real token usage from the response, applies a built-in non-empty check and an optional lightweight LLM judge, and **fails** when a run exceeds a token budget or drops below a quality bar, so you can gate CI on prompt/token regressions. It needs a `ChatClient` (Path A) and throws `InvalidOperationException` without one. When you add native Agent Framework `Checks` / `Evaluators` (below), the cases are also run through a `ChatClientAgent` with `agent.EvaluateAsync(...)` and their verdicts gate the report. That second run is one extra model call per case, is not counted in `TotalTokens`, and if it throws, a warning is logged and only the core gates apply. To test the whole mentor — tools, filters, roles, RAG, memory, several turns, streaming — use `RunConversationsAsync` ([Conversations through the mentor](#conversations-through-the-mentor)). Inject `MentorEvaluator`:

```csharp
var report = await evaluator.RunAsync(
    [
        new EvalCase("Hello", "A short friendly greeting.", MaxTokens: 300),
        new EvalCase("List 3 CRM benefits.", "Lists 3 clear benefits.", MaxTokens: 600),
    ],
    new MentorEvalOptions
    {
        SystemInstructions = mySystemPrompt,   // the prompt under test
        Judge = true, MinQuality = 0.6,        // built-in lightweight LLM-judge; scores only cases with an Expectation
        // JudgeClient = judgeClient,          // optional: judge on another model (default: options.ChatClient)
        MaxTotalTokens = 4000,                 // token-regression gate
    });

report.ThrowIfFailed();   // fail the CI test on regression
```

The built-in path uses only already-referenced packages. For **production-grade quality & safety**, plug the framework's native evaluators — no reimplementation:

```csharp
new MentorEvalOptions
{
    // Native deterministic checks applied to every case (Microsoft.Agents.AI). The evaluation
    // agent is your ChatClient + SystemInstructions with NO tools, so a tool-call check cannot
    // pass here. To assert tool use, run a conversation: RunConversationsAsync with ExpectTools.
    Checks = [ EvalChecks.KeywordCheck("benefit") ],

    // Native LLM-judged evaluators — their pass/fail also gates the report:
    //  • FoundryEvals  → Azure AI Foundry (relevance, coherence, groundedness, violence/self-harm/…);
    //                   add Microsoft.Agents.AI.Foundry yourself (see Provider package versions)
    //  • MEAI quality/safety evaluators (Microsoft.Extensions.AI.Evaluation.Quality/Safety)
    Evaluators = [ new FoundryEvals(projectClient, model, FoundryEvals.Relevance, FoundryEvals.Violence) ],
}
```

### Conversations through the mentor

`RunAsync` sends each case to the model alone. `RunConversationsAsync` runs whole conversations through the mentor itself: each `EvalConversation` gets a session of its own, and its `EvalTurn`s go one after another through the real orchestrator, streamed, with the full pipeline (tools, filters, roles, RAG, memory) and the history between turns. The report is the same `MentorEvalReport`.

> **By default every confirmation is answered no: an evaluation does not write data on its own.** The call still counts in `ToolsCalled`, and the notes say `confirmation asked: <tool> — denied`. With `ApproveConfirmations = true` the answer is yes and the action really runs, on the services of the evaluation's scope — use it only on data made for it. It holds with `MentorHitlMode.Blocking` and `MentorHitlMode.Native` alike.

```csharp
var report = await evaluator.RunConversationsAsync(
    [
        new EvalConversation("refund",
        [
            new EvalTurn("Hi, I have a problem with order 1001."),
            new EvalTurn("Refund it, please.")
            {
                ExpectTools = ["issue_refund"],
                MaxTimeToFirstToken = TimeSpan.FromSeconds(3),
            },
        ]),
    ],
    new MentorEvalOptions
    {
        PrepareSession = (sp, ct) =>
        {
            /* sign in a test user, set the page — anonymous otherwise */
            return Task.CompletedTask;
        },
        // ApproveConfirmations = true,   // default: every confirmation is answered no
        MaxTotalTokens = 20_000,
    });

foreach (var r in report.Results.Where(r => !r.Passed))
    output.WriteLine($"✗ {r.Conversation} turn {r.Turn}: called [{string.Join(", ", r.ToolsCalled)}] — {r.Notes}");

report.ThrowIfFailed();
```

| `EvalTurn` member | Meaning |
|---|---|
| `Input` | The user's message |
| `ExpectTools` | Tools the turn must call |
| `ForbidTools` | Tools the turn must not call |
| `Expectation` | What the judge checks the reply against |
| `MaxTokens` | The turn's token budget |
| `MaxTimeToFirstToken` | How soon the first streamed text must arrive |
| `ExpectRefusal` | The refusal the turn must show instead of an answer: `EvalRefusal.OutOfScope`, `EvalRefusal.Guardrail` or `EvalRefusal.Any` |

**Expecting a refusal.** A turn that should not be answered says so with `ExpectRefusal`. A refusal is one of the two judgements on the message: `OutOfScope`, a message outside the application's scope with `RefuseOutOfScope`; `Guardrail`, a message the safety check (`EnableSafetyCheck`) or the host's `InputGuardrail` judged unsafe, or one the model itself refused. When the expected refusal comes, the turn passes, its notes say `refused: out of scope` or `refused: guardrail`, and the conversation goes on; with `Any`, either one will do. A turn that was answered fails (`expected an out-of-scope refusal, the turn was answered`), and so does the other refusal (`expected an out-of-scope refusal, refused: guardrail; error: …`). A safety check that could not answer (`SafetyCheckFailure = Block`), a message too long, the rate limit and a missing tenant are not refusals: they fail the turn even with the expectation (`expected a refusal, the turn failed; error: …`), so a case that must be refused does not pass just when the guardrail is down. The turn's other rules still apply — `ExpectTools` on a refused turn fails — and a refused turn's missing text does not count as an empty response. Without `ExpectRefusal` nothing changes: a refusal fails the turn with `error: …`, and the result's `Refusal` says which one it was.

```csharp
new EvalConversation("scope",
[
    new EvalTurn("Write me a poem about the sea") { ExpectRefusal = EvalRefusal.OutOfScope },
    new EvalTurn("How many open orders are there?") { ExpectTools = ["count_open_orders"] },
]);
// After the run, the first turn's result.Refusal is EvalRefusal.OutOfScope and its notes say "refused: out of scope".
```

- **One session per conversation.** Each conversation has a DI scope of its own, like a new circuit, closed when it ends: a conversation never sees another's history, nor the application's sessions. `RunConversationsAsync` needs the `MentorEvaluator` from the container; one built by hand throws `InvalidOperationException` there, while its `RunAsync` works as before.
- **Who is asking.** A conversation runs outside any request or circuit, so it is anonymous and the tools that need a role are taken away. `PrepareSession` is called with the conversation's services before its first turn: set the user there, and the page if a turn needs one. In a Blazor Server host, for example: `((IHostEnvironmentAuthenticationStateProvider)sp.GetRequiredService<AuthenticationStateProvider>()).SetAuthenticationState(Task.FromResult(new AuthenticationState(user)))`.
- **`ToolsCalled`.** The tools the gate let through — run, or asked for a confirmation whatever the answer — and the page's UI actions, each once, in the order of their first call. A call the gate refuses (a policy, a role) is not there, and the notes say `not run: <tool> (<reason code>)`; a tool the role filter removed is not even offered to the model. It is read from the gate, so it holds with `EnableActionFeedback = false` too.
- **Tokens.** A turn's tokens are every chat-model call of its session: the coordinator, the classifiers and the agents under it, exact even while other sessions talk in the same process. Embedding calls are not counted. `RunAsync` counts only the case's own reply.
- **`TimeToFirstToken`** runs from the start of the turn to its first streamed text, `null` when the turn has none.
- **A turn fails** when an expected tool is missing (`expected X, called none`), a forbidden one was called (`called forbidden X`), it showed an error (`error: …`, a refusal shown as an error included, unless the turn expected it with `ExpectRefusal`), it has no text (`empty response`), it is over `MaxTokens` (`tokens N > M`), its first text came late or never (`first token after N ms > M ms`), or the judge scored it below `MinQuality`. A failed turn does not stop the conversation: the next turns run on the history it left. The report's gates are those of `RunAsync` (`MaxTotalTokens`, the mean of `MinQuality`, every result), over every turn, and `FailureReason` names the first failed turn, for example `1 of 2 turn(s) failed — first: 'orders' turn 2: expected count_open_orders, called none`.
- **`RunAsync` only:** `SystemInstructions`, `Checks` and `Evaluators`. A conversation runs with the mentor's instructions; with those options set, a warning says so.
- **The judge** is `JudgeClient`, or `ChatClient` without it. With neither, and the judge on for turns that have an `Expectation`, `InvalidOperationException` is thrown before the first turn.
- **Cancellation** stops the run with `OperationCanceledException`; no later turn runs.

### Reading the report

`ThrowIfFailed()` is the one-liner for CI, but the report carries everything you need to print a
diff or fail with a useful message.

**`MentorEvalReport`**

| Member | Type | Meaning |
|---|---|---|
| `Results` | `IReadOnlyList<MentorEvalResult>` | One entry per case, or per turn of each conversation, in the order you supplied them |
| `Passed` | `bool` | The gate. False if any case failed, or a budget/quality bar was missed |
| `FailureReason` | `string?` | Why, when `Passed` is false |
| `TotalTokens` | `long` | Sum across cases — the number to trend over time |
| `AverageQuality` | `double?` | Mean judge score, or `null` when nothing was judged |
| `PassRate` | `double` | Share of cases that passed, `0`–`1`. An empty run is `1` |
| `Duration` | `TimeSpan` | Wall-clock time of the whole run |
| `ThrowIfFailed()` | `void` | Throws `InvalidOperationException` with `FailureReason` |

**`MentorEvalResult`**

| Member | Type | Meaning |
|---|---|---|
| `Input` / `Output` | `string` | The case and what the agent actually said |
| `InputTokens` / `OutputTokens` / `TotalTokens` | `long` | Per-case metering |
| `QualityScore` | `double?` | `0`–`1` when judged, else `null` |
| `Passed` | `bool` | This case's verdict |
| `Notes` | `string?` | What the checks or judge objected to |
| `Conversation` / `Turn` | `string?` / `int?` | The conversation's name and the turn, from 1; `null` for an `EvalCase` |
| `ToolsCalled` | `IReadOnlyList<string>` | The tools the turn called; empty for an `EvalCase` |
| `Refusal` | `EvalRefusal?` | The refusal the turn showed instead of an answer, `OutOfScope` or `Guardrail`, expected or not; `null` when it was answered, and for an `EvalCase` |
| `TimeToFirstToken` / `TurnDuration` | `TimeSpan?` | Time to the first streamed text, and of the whole turn; `null` for an `EvalCase` |

So a failing CI run can say which case regressed rather than just that one did:

```csharp
var report = await evaluator.RunAsync(cases, options);

foreach (var r in report.Results.Where(r => !r.Passed))
    output.WriteLine($"✗ {r.Input}  ({r.TotalTokens} tok, q={r.QualityScore:P0})  {r.Notes}");

output.WriteLine($"{report.PassRate:P0} passed · {report.TotalTokens} tokens · {report.Duration.TotalSeconds:F1}s");

// AverageQuality is null when nothing was judged — that is not the same as "scored zero", so
// print it only when there is a number, rather than letting a formatter render null as 0%.
if (report.AverageQuality is { } q)
    output.WriteLine($"mean judge score {q:P0}");

// FailureReason is the sentence ThrowIfFailed() would raise. Log it yourself when you want the
// run recorded rather than aborted.
if (!report.Passed)
    output.WriteLine($"GATE FAILED — {report.FailureReason}");

report.ThrowIfFailed();
```

Track `TotalTokens` between runs to catch the regression that matters most in practice — a prompt
edit that quietly doubles cost while every case still passes.

For full-pipeline token checks, run conversations: `RunConversationsAsync` meters each turn's whole session, classifiers and agents included.

---

## All configuration options

The tables below are the reference. This first block is the *operational* set — limits, safety and
telemetry — because those options are the ones a table alone does not explain well, and the defaults
carry decisions you may want to reverse:

```csharp
builder.Services.AddMentorAgent(options =>
{
    // ---- Limits -----------------------------------------------------------------
    // Refused BEFORE the model call, so an over-long message costs nothing.
    options.MaxMessageLength  = 4000;   // characters; 0 = unlimited
    // History kept per session, trimmed by whole turns with tool calls and results kept
    // (options.ChatReducer replaces the reducer). 0 (or negative) means "no trimming", like
    // RateLimitPerUser and MaxHostedToolCallsPerSession on this same class, and logs a warning
    // when the session is built.
    options.MaxSessionMessages = 50;
    options.RateLimitPerUser   = 20;    // messages/minute; 0 = no limit

    // ---- Confirmations ----------------------------------------------------------
    // Kill switch for the confirmation dialogs of your actions, MCP-client servers and
    // RequiresApproval — not for a hosted MCP server's approvals, which the provider asks for.
    // It silences the BANNER only: RequiredRoles is authorization and keeps refusing an
    // unauthorized caller either way.
    options.RequireConfirmation = true;

    // ---- Diagnostics ------------------------------------------------------------
    // Puts an agent's exception message into the reply the USER reads — which is where a
    // connection string goes to be screenshotted. Development only.
    options.IncludeWorkflowExceptionDetails = builder.Environment.IsDevelopment();

    // ---- Observability ----------------------------------------------------------
    // Your exporter must listen on this exact name, or it receives nothing and the
    // symptom reads as "no traffic" rather than "wrong name".
    options.ObservabilitySourceName = "MyApp.Mentor";
    options.ObservabilityIncludeSensitiveData = builder.Environment.IsDevelopment();

    // ---- Metrics retention ------------------------------------------------------
    options.MetricsRetention           = TimeSpan.FromDays(7);    // hourly buckets kept this long
    options.MetricsPersistenceInterval = TimeSpan.FromSeconds(30); // flush cadence for a store

    // ---- Hosted-tool cost gate --------------------------------------------------
    // Skips provider-billed tools for off-domain questions. The check runs on
    // ClassifierChatClient (falls back to ChatClient), so point that at a cheaper deployment.
    // HostedToolDomainClassifier is a different thing: a Func<string, CancellationToken, Task<bool>>
    // that REPLACES the model call with your own decision.
    options.HostedToolDomainCheck = true;
    options.ClassifierChatClient  = new AzureOpenAIClient(endpoint, credential)
        .GetChatClient("gpt-4o-mini").AsIChatClient();
});
```

### When the options are read and checked

- **The callback runs twice.** `AddMentorAgent` runs it once on a snapshot, to decide what to register,
  and once more when the options are first built. Keep it free of side effects that must happen once.
- **Some options are read at registration.** `EnableObservability`, `ObservabilitySourceName`,
  `McpServerEnabled`, `A2AServerEnabled`, and `AppName` as the MCP server's name come from that snapshot.
  Set them in the `AddMentorAgent` callback: a `Configure<MentorOptions>` or `PostConfigure` registered
  after it does not reach them. Every other option is read from the real options, the A2A agent card's
  too: `MapMentorAgentA2A()` builds it from `AppName`, `AppDescription` and `AgentCard` as they are when it
  runs. `AddMentorAgentMcp()` and `AddMentorAgentA2A()` go after `AddMentorAgent()`
  (`AddMentorAgentServer()` calls both). Called before it, or with `McpServerEnabled` / `A2AServerEnabled`
  turned on after it, they register nothing, and the host stops at start-up with the reason.
- **A callback that throws on the snapshot** leaves those options at their defaults, and a warning with
  the exception is logged when the host starts.
- **The options are checked when the host starts.** In an ASP.NET Core or worker host an invalid
  configuration (no `ChatClient` or `Agent`, empty `ScanAssemblies`, both `ChatHistoryProvider` and
  `ChatHistoryProviderFactory`…) stops the start with the reason, before the first request. A MAUI app has
  no such start: there the error comes the first time MentorAgent is used.
- **A few options can change while the application runs** — see the next section.

### Changing options while the application runs

`MentorOptions` is one object for the process: the value of `IOptions<MentorOptions>`. Most options are read
before the application starts, and are set there. The ones in the table below are read again for every session
or at every turn, so a change made while the application runs applies:

- **Turn**: from the next turn of every session;
- **Session**: to the sessions that begin after the change.

To change one, assign its property on that object, and replace a collection rather than change it in place. A
turn already running may still read the value from before. `MentorRuntimeOptions.Changeable` lists them in code,
each with `MentorOptionReadAt.Turn` or `MentorOptionReadAt.Session`; `MentorRuntimeOptions.IsChangeable(name)`
answers for one.

```csharp
// An admin page that turns on the safety check without a restart.
public sealed class SafetySwitch(IOptions<MentorOptions> options)
{
    public void TurnOn()
    {
        if (MentorRuntimeOptions.IsChangeable(nameof(MentorOptions.EnableSafetyCheck)))
            options.Value.EnableSafetyCheck = true;   // from the next turn of every session
    }
}
```

| Option | Applies | What a change does |
|---|---|---|
| `Language` | Turn | The language of the next turn of every session, and the widget's texts |
| `RateLimitPerUser` | Turn | The limit the next message is counted against; 0 turns it off |
| `RateLimitWindowSecs` | Turn | The window the next message is counted in |
| `ConfirmationMessage` | Turn | The text of the next confirmation banner |
| `ConfirmationTimeout` | Turn | The deadline of the next confirmation |
| `ActionPolicyTimeout` | Turn | The deadline of the next question to a policy or to the governor |
| `EnableActionFeedback` | Turn | Whether the next action shows the bar of the running action |
| `EnableGenerativeCards` | Turn | Whether the next card-shaped result becomes a card |
| `EnableSafetyCheck` | Turn | Whether the next message is checked |
| `CheckAnnouncedActions` | Turn | Whether the next reply that called no tool is checked for an action it announced or reported as done |
| `MentorshipLevel` | Session | The behaviour block of a new session's instructions |
| `ProactiveNextSteps` | Session | Whether a new session's Proactive level closes with next steps |
| `EnableRichResponses` | Session | Whether a new session's instructions carry the formatting block |
| `RefuseOutOfScope` | Session | Whether a new session refuses the messages outside the application's scope |
| `ToolFilterMaxTools` | Session | The most tools a new session's filter sends |
| `MaxSessionMessages` | Session | The history budget of a new session |

Every other option is read before the application starts: one may happen to work when changed later, but
nothing promises it. `RequireConfirmation` is not in the table, because the MCP server decides at start-up which
actions it publishes, so a later change would apply only in part; nor is `MaxMessageLength`, because the widget's
composer receives a copy at start-up. The widget's options (`BotName`, `Theme`, `EnableVoiceInput`…) are copied
to `MentorWidgetOptions` at registration. Each option in the table has a test that changes it on a running
application and sees the change apply, and every option of `MentorOptions` declares when it is read, so a new
one cannot ship without that decision.

### Core

| Property | Type | Default | Description |
|---|---|---|---|
| `AppName` | `string` | *(required)* | Application name injected into the system prompt |
| `AppDescription` | `string` | `""` | Domain description for richer AI context |
| `Language` | `MentorLanguage` | `English` | The application's language, for every session and read again on every turn: the AI's answers, the instructions MentorAgent writes that the model could echo, and the widget UI (see the supported values and *What follows `Language`* below). A `ChatWidget` with its own `Language` overrides it for its page — see [A language per page](#a-language-per-page) |
| `MentorshipLevel` | `MentorshipLevel` | `Standard` | Proactivity level of the AI |
| `ProactiveNextSteps` | `bool` | `false` | With `MentorshipLevel.Proactive`, close answers with 1-3 suggested next steps. Off by default: even limited to what the request can do, the model offered steps the application does not have, 3 in 15 measured turns (BUG-074). The other levels ignore it |
| `ChatClient` | `IChatClient?` | `null` | AI provider via IChatClient (Azure OpenAI, OpenAI, Ollama…) |
| `Agent` | `AIAgent?` | `null` | Pre-built AIAgent (Foundry, Anthropic…). Two calls with one id in a response of your agent's model are not made unique: your agent runs them before MentorAgent's clients see them (BUG-225). |
| `CoordinatorSampling` | `MentorSamplingOptions?` | `null` | The coordinator's sampling on every request of its turns, the forced first call and the tool loop included: `Temperature`, `TopP`, `TopK`, `MaxOutputTokens`, `FrequencyPenalty`, `PresencePenalty`, `Seed`; a value left `null` is the provider's. The classifiers, an `Agent` you build, specialists, teams and the compaction summary keep their own. A negative temperature, a `TopP` outside (0, 1], or a `TopK` or `MaxOutputTokens` below 1 stops the application at startup |
| `EmbeddingGenerator` | `IEmbeddingGenerator<string, Embedding<float>>?` | `null` | Optional embedding model — enables semantic tool filtering |
| `ScanAssemblies` | `Assembly[]` | *(required)* | Assemblies to scan for agents, actions, and pages |

**Supported `MentorLanguage` values:**

| Value | Language |
|---|---|
| `MentorLanguage.English` | English |
| `MentorLanguage.Italian` | Italian |
| `MentorLanguage.French` | French |
| `MentorLanguage.German` | German |
| `MentorLanguage.Spanish` | Spanish |
| `MentorLanguage.Portuguese` | Portuguese |
| `MentorLanguage.Dutch` | Dutch |
| `MentorLanguage.Polish` | Polish |
| `MentorLanguage.Japanese` | Japanese |
| `MentorLanguage.Chinese` | Chinese |

**What follows `Language`.** The assistant answers in it, whatever language the user writes in. The instructions MentorAgent writes that the model could echo into a reply are in it too, in all ten languages: in the coordinator's prompt, the rules with an example sentence (before a confirmation, after a declined one, describing an action, outside the configured scope, Proactive's closing rule) and the grounding rule; in the prompts MentorAgent generates for specialists and team members, the grounding rule; in a tool's result, every instruction MentorAgent adds — declined, stopped, not confirmed in time, result withheld, never run, outcome unknown, nobody to confirm. So does the notice a request carries when its turn left out an action the user may not run, under its English heading. Tool names, JSON keys and the markers in a result are the same in every language. The rest stays in English: the headings and the other rules of the prompts, the line that names the reply language, the context and the other notices added to each request, the prompts of the classifiers and of the onboarding tour's generation, the results of a delegation, and the error messages of a failed action. In `MentorHitlMode.Native` a declined approval also reaches the model after a sentence of Microsoft.Extensions.AI's own, in English. The `Instructions` you write for a specialist or a team member are sent as written. With `English` these texts are the English originals; another language only translates them.

### A language per page

`MentorOptions.Language` is the application's language: every session uses it, and it is read again on every turn, so
a change made while the application runs applies from the next turn. A site in more than one language also gives the
widget the page's language:

```razor
<ChatWidget Language="@_language" />

@code {
    // From the route (/it/…) or from a language switch in your layout.
    [Parameter] public MentorLanguage? Lang { get; set; }
    private MentorLanguage _language => Lang ?? MentorLanguage.English;
}
```

A widget with `Language` uses it for its own texts, its voice (recognition and reading aloud) and its onboarding tour,
and sends it with every turn: the assistant answers in it, and what MentorAgent writes for that turn is in it too —
the instructions of *What follows `Language`*, the confirmation banner, the errors, the instruction to cite documents.
Changing the parameter redraws the widget at once and keeps the conversation; a turn already running ends in its own
language. Without the parameter nothing changes. A value that is not a `MentorLanguage` counts as the application's
language. The WebAssembly widget takes the same parameter and sends it over the hub; a client of your own sends it
with `SendUserMessage`, or as `language` on `/mentor/chat` (see the Server README's protocol reference).

**What stays on the application's language.**
- **A2A tasks and MCP calls.** They come from no page.
- **The texts you set yourself.** `WelcomeMessage`, `InputPlaceholder`, `AiDisclosureMessage` and `BotName` are shown
  as written, in every language. On a site in more than one language leave them `null`, and the widget shows its own
  translated texts.
- **An onboarding tour your code writes.** An `IMentorTour` of yours serves every language unless it implements
  `GetStepsAsync(MentorLanguage? language, CancellationToken ct)`. The tour MentorAgent generates is generated once
  per language.
- **Components used on their own.** Outside a `ChatWidget` — a `ChatInput` in a composed page, `ConfirmationBanner`,
  `McpStatusBadge`, `A2AStatusBadge` — a component uses the `MentorLocalizer` from DI: the application's language,
  read on every use. Inside the widget, `ChatInput`, `WelcomePanel` and `OnboardingTour` follow the widget's language.
  `MentorDashboard` has a `Language` parameter of its own.
- **A coordinator you build yourself** (an `AIAgent` given instead of a `ChatClient`): its instructions are yours, and
  the turn's language does not change them.
- **A specialist with `Instructions` of its own** gets one line, "ALWAYS respond in X for this turn.", when the turn's
  language is not the application's.
- **Your RAG sources** do not receive the turn's language: the documents are the ones they return. The instruction to
  cite them follows the turn.

**The prompt cache.** The instructions that follow `Language` change with the page's language. In the application's
language a request is the one it was before, so the cache does not change; each other language has its own cached
variant, and the first turn after a switch is paid without the cache. If something before MentorAgent's client
changes the start of the instructions, a turn in another language gets the instructions as built, with that one line
asking for its language, and a Warning is logged.

### Widget

| Property | Type | Default | Description |
|---|---|---|---|
| `Theme` | `MentorTheme` | `Default` | Widget visual theme: `Default`, `Dark`, `Minimal` (light), `Custom` or `Auto` (follows the visitor's system) — see [Themes](#themes) |
| `Position` | `ChatPosition` | `BottomRight` | Widget position |
| `PrimaryColor` | `string?` | `null` | Custom hex color |
| `BotName` | `string` | `"Mentor AI"` | Bot name in widget header |
| `AvatarUrl` | `string?` | `null` | Bot avatar image URL |
| `WelcomeMessage` | `string?` | `null` | Initial welcome message |
| `ShowAiDisclosure` | `bool` | `true` | Shows the notice that tells people they are talking to an AI, under the widget's header for the whole conversation. `false` hides it; the replies' machine-readable marker stays — see [Telling people they are talking to an AI](#telling-people-they-are-talking-to-an-ai--showaidisclosure) |
| `AiDisclosureMessage` | `string?` | `null` | The notice's text, shown as written (as text, never as markup) in every language. `null` or white space: the default, translated into the ten languages of `Language` |
| `InputPlaceholder` | `string?` | `null` | Input box placeholder. `null` uses the localized text in `Language` ("Write a message..." in English) |
| `EnableSuggestions` | `bool` | `true` | Proactive suggestion chips |
| `EnableActionFeedback` | `bool` | `true` | Visual feedback during tool calls |
| `EnableVoiceInput` | `bool` | `false` | Microphone via browser Speech Recognition API. See [Voice](#voice-input-and-output) |
| `EnableVoiceOutput` | `bool` | `false` | Text-to-speech via browser Speech Synthesis API |
| `VoiceStreaming` | `bool` | `true` | Speak each sentence as it streams instead of reading the finished answer back |
| `VoiceBargeIn` | `bool` | `true` | Stop playback when the user takes the floor (microphone, send, Stop, new conversation) |
| `VoiceHandsFree` | `bool` | `false` | Keep the conversation going by voice alone. Ignored unless both voice options are on |
| `VoiceRate` | `double` | `1.0` | `SpeechSynthesisUtterance.rate` — useful range 0.5–2.0 |
| `EnableOnboardingTour` | `bool` | `false` | [Guided first-run tour](#onboarding-tour) generated from the registered pages and the assistant's tools |
| `EnableGenerativeCards` | `bool` | `true` | Render a `MentorCard` returned by a tool as [a card](#generative-ui--cards-level-2). `false` falls back to the model describing it in text |
| `EnableRichResponses` | `bool` | `true` | Instructs the coordinator to format answers as Markdown: tables for comparative data, lists for enumerations (generative UI Level 1). The widget always renders Markdown safely; `false` asks for terse plain text |
| `EnableImageInput` | `bool` | `false` | [Multimodal image input](#multimodal-image-input) — 📎 upload, paste, drag & drop and 🔗 URL in the composer. Requires a vision-capable chat model |
| `MaxImageBytes` | `int` | `4194304` | Max size per attached image (4 MB). Enforced server-side |
| `MaxImagesPerMessage` | `int` | `4` | Max images per user turn. Enforced server-side |
| `AllowedImageTypes` | `IReadOnlyList<string>` | `png, jpeg, gif, webp` | MIME **allow-list** for attachments (AF Agent Safety — never a deny-list) |
| `ShowHostedToolsStatus` | `bool` | `false` | Amber badge in the widget header listing the active [hosted tools](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp). Development aid |

### Hosted tools

| Property | Type | Default | Description |
|---|---|---|---|
| `HostedTools` | `MentorHostedTools` | `None` | Provider-hosted tools to declare: `WebSearch`, `CodeInterpreter`, `FileSearch`, `ImageGeneration`, `HostedMcp` (flags). Availability depends on the client and the deployment — see [Hosted tools](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp) |
| `FileSearchVectorStoreIds` | `IReadOnlyList<string>?` | `null` | Vector stores searched by `FileSearch`. **Required** when it is enabled — without ids the tool is skipped and a warning is logged (fail-closed) |
| `FileSearchMaxResults` | `int?` | `null` | Upper bound on file-search matches. `null` lets the provider decide |
| `HostedImageModel` | `string?` | `null` | Model id sent with `ImageGeneration` (e.g. `gpt-image-1-mini`), read by OpenAI proper. **On Azure OpenAI it is not enough on its own**: Azure takes the image deployment from the `x-ms-oai-image-generation-deployment` request header, and without it every image turn fails with *"imagegen deployment must be provided through header"*. See the Azure note under [Hosted tools](#hosted-tools-web-search-code-interpreter-file-search-images-remote-mcp) |
| `HostedImageSize` | `string?` | `null` | Generated image size as `WIDTHxHEIGHT` (e.g. `"1024x1024"`). **The cost knob for image generation** — a larger image is billed more. An unparsable value is ignored with a warning |
| `HostedMcpServers` | `IReadOnlyList<MentorHostedMcpServer>?` | `null` | Remote MCP servers the **provider** connects to, used by `HostedMcp`. **Required** when it is enabled (fail-closed). Per server: `Name`, `Url`, `Description`, `AllowedTools`, `RequireApproval` (default `true`), `AlwaysRequireApprovalTools` / `NeverRequireApprovalTools`, `Headers` |
| `ShowHostedToolActivity` | `bool` | `true` | Show hosted-tool work on the action-feedback line while it happens: "Searching the web…" plus the queries, "Running code…", "Generating the image…". Without it a multi-second provider call looks like a frozen widget. Citations (which need `ShowRagSources`) and generated images are delivered either way |
| `FilterHostedTools` | `bool` | `true` | Let the semantic tool filter decide per turn whether each hosted tool is relevant, instead of declaring them on every message. Needs `EnableToolFiltering` + `EmbeddingGenerator`; without them nothing is filtered and a warning is logged (**fail-open**) |
| `HostedToolFilterMinScore` | `float?` | `null` → **0.15** | Similarity threshold for hosted tools only — deliberately **lower** than `ToolFilterMinScore`, not equal to it: their scores run on a different scale (long English descriptions vs a short message in the user's language), and this is a coarse pre-cut now that `HostedToolDomainCheck` makes the judgement. Raising it to "be safe" makes the verdict flip between rewordings of the same request. Scores are logged at Debug |
| `HostedToolDomainCheck` | `bool` | `false` | Ask a small model whether the message concerns this application **before** a hosted tool runs. A different question from `FilterHostedTools`: *"draw me a dog"* is a real image request and nonsense for a shop, and only this catches it. Runs once per turn and only when a hosted tool already passed relevance — free on ordinary conversation. Fails open |
| `HostedToolDomainScope` | `string?` | `null` | The scope the classifier judges against. `null` derives it from `AppName` + `AppDescription` + page names, plus — when hosted tools are on — the enabled capabilities and the rule that they do not widen the subject (BUG-050). Also what `RefuseOutOfScope` judges against when set. Write it yourself when the derived text is too narrow (shipping, VAT — legitimate and covered by no tool) or too vague, since a vague scope makes the classifier permissive |
| `HostedToolDomainClassifier` | `Func<string, CancellationToken, Task<bool>>?` | `null` | Replaces the model call with your own decision (`true` = in scope). Reuse an existing intent classifier, apply per-user policy, or make the check free |
| `MaxHostedToolCallsPerSession` | `int` | `0` | Hard cap on hosted-tool calls per session; beyond it they stop being declared. `0` = no cap. Complements the filter rather than replacing it: scoring lowers the frequency, only a counter bounds the worst case |

### Session & History

| Property | Type | Default | Description |
|---|---|---|---|
| `MaxSessionMessages` | `int` | `50` | Max messages kept in the default in-memory session history, tool calls and results included; trimmed by whole turns, oldest first ([How the built-in history is trimmed](#how-the-built-in-history-is-trimmed)). `0` or negative = no trimming (a warning is logged). Not read when `ChatReducer` is set; not applied to a `ChatHistoryProvider` / `ChatHistoryProviderFactory` you supply (trim inside your provider), nor with `UseServiceManagedHistory` |
| `ChatReducer` | `IChatReducer?` | `null` | Replaces the reducer of the default in-memory history (`null` = `MentorChatReducer` sized by `MaxSessionMessages`, which keeps tool calls with their results). `new MessageCountingChatReducer(50)` restores the behaviour up to rc.14. One instance serves every session. Not used with a provider you supply or with `UseServiceManagedHistory` (a warning is logged) |
| `ChatHistoryProvider` | `ChatHistoryProvider?` | `null` | Persistent conversation history provider — **one instance, shared by every user in the process**. Correct only for a single-user host, or a provider that partitions its own storage. See the warning below |
| `ChatHistoryProviderFactory` | `Func<IServiceProvider, ChatHistoryProvider>?` | `null` | Builds a provider **once per coordinator** (per circuit / per connection / per app instance) — the same lifetime the default has. **Prefer this whenever the application has more than one user.** Setting both stops the host when it starts |
| `UseServiceManagedHistory` | `bool` | `false` | Set when the chat client keeps the conversation on the **service** (OpenAI/Azure **Responses**, Foundry, Copilot Studio). MentorAgent then installs no history provider — required, since AF forbids combining a conversation id with a `ChatHistoryProvider`. Disables `MaxSessionMessages`, `ChatReducer` and `EnableCompaction`, and any `ChatHistoryProvider` / `ChatHistoryProviderFactory` you set is not used. A model response that gives two calls the same id is passed on as it came, with a warning: the service holds the calls under the ids it generated (BUG-225). |
| `SessionContextPlacement` | `MentorSessionContextPlacement` | `Instructions` | Where the lines that change with every request go: the page, its data, the address, the date and time. `Instructions`: at the end of the instructions. `LastUserMessage`: at the start of the user's last message, so the instructions stay the same and a model that caches by prefix, a local engine above all, reads again only the last exchange. Path A with local history only — see [`SessionContextPlacement`](#the-requests-context-in-the-users-message--sessioncontextplacement) |

### Memory

| Property | Type | Default | Description |
|---|---|---|---|
| `UseMemoryContext` | `bool` | `false` | Contextual memory across sessions |
| `AnonymousIdentity` | `MentorAnonymousIdentity` | `PerSession` | Who a **signed-out** visitor is, for memory and for the rate-limit counter. `PerSession` gives each circuit/connection its own key, so two visitors never read each other's memories and one cannot spend everybody's allowance. `Shared` is the previous behaviour — every session on the literal key `"anonymous"` — and is the right choice for a **single-user** app (MAUI, desktop), where it is what makes memory survive a restart. Ignored for a signed-in user |
| `MemoryContextCount` | `int` | `10` | Number of recent memories in the prompt |
| `MemoryRelevanceFiltering` | `bool` | `false` | Inject only the memories **semantically** relevant to the current message (embedding cosine; identity/preference facts always kept) instead of the last N — saves tokens. Requires `EmbeddingGenerator`; without it, falls back to last-N |
| `MemoryAutoCapture` | `bool` | `true` | The reliable memory writer: a dedicated post-turn extraction saves durable user facts instead of relying on the model to call `remember` (weak models do this inconsistently). Whenever it can run — with a `ChatClient`, or on Path B with a `ClassifierChatClient`, which then makes the call — it is the **only** writer: the redundant `remember` tool + its prompt are dropped (saves tokens). On Path B without a classifier it falls back to the `remember` tool. `forget_all` is always kept. Adds one small model call per user message; set `false` to opt out |

### Agent Skills

| Property | Type | Default | Description |
|---|---|---|---|
| `EnableSkills` | `bool` | `false` | Enables skill discovery and the `load_skill` / `read_skill_resource` tools |
| `SkillsFolder` | `string` | `"Skills"` | Folder to scan for file-based skills (`SKILL.md`). Relative to content root or absolute |
| `SkillSources` | `IList<AgentSkillsSource>` | empty | Additional sources, using the Agent Framework's own abstraction — see [Skills from somewhere else](#skills-from-somewhere-else--agentskillssource) |
| `SkillFilter` | `Func<AgentSkill, bool>?` | `null` | Keeps or drops a skill contributed by `SkillSources`. Does not apply to your own skills |
| `SkillsRefreshInterval` | `TimeSpan?` | `null` | Shares one list from `SkillSources` across every session in the process (wrapped in the framework's `CachingAgentSkillsSource`) and re-fetches it when a session is built after this long. Without it, every new session (circuit, hub connection, SSE request) re-reads the sources when its coordinator is built and keeps that list for its lifetime. Set it for a remote or expensive source |

### RAG

| Property | Type | Default | Description |
|---|---|---|---|
| `UseRag` | `bool` | `false` | Enable RAG. Requires a registered `IMentorRagSource` |
| `RagResultCount` | `int` | `5` | Number of documents retrieved per query |
| `RagMinScore` | `float` | `2` | Minimum relevance score to include a document. `0` = no filtering. Keyword search: integer-like (2 = two matches); vector/cosine: `0.5`–`0.75`. Not set: the source's `DefaultMinScore` when it declares one |
| `RagSystemPromptTemplate` | `string` | `"Use the following documents to answer:\n{documents}"` | Prompt template. Use `{documents}` as placeholder |
| `ShowRagSources` | `bool` | `false` | Show citation chips below AI messages in the widget |
| `RagNoResultsNotice` | `bool` | `false` | Tell the model when the search keeps no document, so it does not answer a question about the documents from memory. In the language of the turn, in that request only. See [RAG configuration options](#rag-configuration-options) |

### MCP

| Property | Type | Default | Description |
|---|---|---|---|
| `McpServers` | `MentorMcpServer[]?` | `null` | External MCP servers to connect as L1 tools |
| `MentorMcpServer.Shared` | `bool?` | `null` | Whether one connection serves the whole process. Unset decides by transport: **`ServerUrl` → shared**, **`Command` → per session**. Set `true` on a stateless local server to start one child process instead of one per visitor (measured: two browser tabs = 8 node processes, 580 MB); set `false` on a URL server that keeps per-connection state |
| `McpServerEnabled` | `bool` | `false` | Expose MentorAgent as an MCP server. Needs `Novior.MentorAgent.Server`: also call `builder.Services.AddMentorAgentMcp()` (or `AddMentorAgentServer()`) and `app.MapMentorAgentMcp()` |
| `McpCallerPrincipal` | `Func<ClaimsPrincipal?>?` | `null` | Resolves who is calling the MCP server, so **role**-gated actions can be published to callers entitled to them. Default `null` withholds every gated action — the safe default. A null principal, an unauthenticated one, or a resolver that throws all withhold too. **Confirmation gates are never satisfied by it**: `RequiresConfirmation` and `RequiresApproval` mean "ask a human", and there is none on the far end of an MCP call — only an `IMentorActionGovernor` can take such a confirmation away |
| `McpServerPath` | `string` | `"/mcp"` | Path `MapMentorAgentMcp()` maps the MCP endpoint at when called without a path. A path passed to the method wins |
| `ShowMcpStatus` | `bool` | `false` | Show MCP server connection status badge in the widget header |

### A2A

| Property | Type | Default | Description |
|---|---|---|---|
| `RemoteAgents` | `MentorRemoteAgent[]?` | `null` | Remote A2A agents to add to the Handoff workflow. Base URL only — SDK auto-appends `/.well-known/agent-card.json` |
| `A2AServerEnabled` | `bool` | `false` | Expose MentorAgent as an A2A agent. Needs `Novior.MentorAgent.Server`: also call `builder.Services.AddMentorAgentA2A()` (or `AddMentorAgentServer()`) and `app.MapMentorAgentA2A()` |
| `A2AServerPath` | `string` | `"/a2a"` | Path `MapMentorAgentA2A()` maps the A2A task handler at when called without a path. A path passed to the method wins. The Agent Card stays at `/.well-known/agent-card.json` |
| `A2AServerUrl` | `string?` | `null` | Full public URL of this agent's A2A endpoint, path included (e.g. `http://localhost:5001/a2a`). Written verbatim into the Agent Card `SupportedInterfaces` so remote consumers can resolve the absolute endpoint; `null` = the card advertises the mapped path. **Required** when this instance is used as a remote agent by other MentorAgent instances |
| `AgentCard` | `AgentCardInfo?` | `null` | Metadata for the A2A Agent Card (`/.well-known/agent-card.json`) |
| `ShowA2AStatus` | `bool` | `false` | Show a badge in the widget header listing configured remote A2A agents. Click the badge to see agent names and URLs |

### Token & cost optimization

| Property | Type | Default | Description |
|---|---|---|---|
| `EnableToolFiltering` | `bool` | `false` | Send only the tools semantically relevant to the message. **Requires `EmbeddingGenerator`**; without it, all tools are sent |
| `ToolFilterMaxTools` | `int` | `12` | Max matched business tools to send (core tools always kept on top) |
| `RequireLookupForRecordQuestions` | `bool` | `false` | Opt-in. When a classifier says a message asks for a value of your records, the first model call must call a declared read-only action (or hand over to a specialist): with semantic tool filtering, one the filter selected; without it, any declared read the user may run. One classifier call per turn whose selection holds a read — without the filter, nearly every turn. Needs declared reads. Needs a provider that honours `tool_choice: "required"`: Ollama ignores it, and a Warning says so. See [Semantic tool filtering](#semantic-tool-filtering) |
| `RequireActionForChangeRequests` | `bool` | `false` | Opt-in. When a classifier says a message asks to carry out one of your confirmation-gated actions and gives every input it needs, the first model call must call one of those actions the filter selected and the user may run (or hand over to a specialist), so the banner comes first; when it asks for a page, `navigate_to`. A request that leaves an input out is not forced. One classifier call per turn, shared with `RequireLookupForRecordQuestions`. Unlike that option, needs `EnableToolFiltering` and an `EmbeddingGenerator`, and `RequireConfirmation` for actions. Needs a provider that honours `tool_choice: "required"`: Ollama ignores it, and a Warning says so. See [Semantic tool filtering](#semantic-tool-filtering) |
| `CheckAnnouncedActions` | `bool` | `true` | After a turn in which the coordinator called no tool, asks a model whether its reply announces an action or reports one as done; if so, one more request in the same turn, with the same tools, told to call the tool now or to say it was not done. Never a required call, once per turn. The question goes to `ClassifierChatClient`, or to `ChatClient` without one. Path A with the history MentorAgent keeps; no effect with `UseServiceManagedHistory` or a pre-built `Agent`. Read at every turn. BUG-220. See [Token & cost optimization](#token--cost-optimization) |
| `ToolFilterMinTools` | `int` | `0` | Once one action clears `ToolFilterMinScore`, at least this many best-ranked actions are sent, whatever their score (up to `ToolFilterMaxTools`). `0` = the threshold alone. A trade-off, not a fix: it can hand the model a read action it needed, or a wrong one it picks instead. See [Semantic tool filtering](#semantic-tool-filtering) |
| `ToolFilterMinScore` | `float` | `0.35` | Minimum cosine similarity (0–1) for a tool to count as relevant. Higher = stricter |
| `EnableCompaction` | `bool` | `false` | Compact long conversation history before each call — what is sent, never what is stored (BUG-104). Locally kept history only (Path A: the built-in provider, `ChatHistoryProviderFactory` or a provider instance); not installed with `UseServiceManagedHistory` |
| `CompactionTokenThreshold` | `int` | `4000` | Token budget that triggers compaction and the truncation backstop |
| `CompactionMaxTurns` | `int` | `8` | Recent user turns kept intact by the sliding-window step |

> `EmbeddingGenerator` (in the [Core](#core) table) powers semantic tool filtering **and** semantic memory (`MemoryRelevanceFiltering`, in [Memory](#memory)). RAG relevance is handled by the vector search + `RagMinScore` (in [RAG](#rag)).

### Middleware, observability & dashboard

| Property | Type | Default | Description |
|---|---|---|---|
| `InputGuardrail` | `Func<string,CancellationToken,Task<bool>>?` | `null` | Custom input guardrail (`true` = safe). Replaces the built-in check; runs whenever set |
| `OutputGuardrail` | `Func<string,CancellationToken,Task<bool>>?` | `null` | Moderate the completed reply (`true` = safe). When set, the reply is buffered and revealed after moderation (no live streaming that turn) |
| `EnableOutputSafetyCheck` | `bool` | `false` | Built-in LLM check on the **reply**: blocks one that leaks system instructions or secrets, discloses someone's personal data, or contains harmful content. Buffers the reply for that turn (no live streaming). Runs on `ClassifierChatClient`, or on `ChatClient` when it is not set, is bounded by `SafetyCheckTimeout` / `SafetyCheckFailure`, and a custom `OutputGuardrail` takes precedence. Requires a `ChatClient` or a `ClassifierChatClient`: on Path B without one it is skipped, with a warning when the coordinator is built |
| `OnToolResult` | `Func<string,object?,object?>?` | `null` | Transform/redact a tool result before it returns to the model, and to an MCP caller. A hook that throws withholds the result: the model is told the action ran and not to run it again (an MCP caller gets an `isError` result) |
| `OnException` | `Func<Exception,string?>?` | `null` | Map an exception to a user-facing message (`null` → built-in mapping). For a tool that throws, the chat never shows the exception's own text while the hook is set: `null` shows the localized "The action could not be completed." The model gets the hook's message, else the exception's, and may repeat it in its reply: return a message for any exception whose text must not reach the user |
| `ConfigureChatClientPipeline` | `Func<ChatClientBuilder,ChatClientBuilder>?` | `null` | Insert custom middleware into the Path A pipeline (outermost). Built once per coordinator — one per scope, so one per circuit on Blazor Server: keep state meant for the whole process (a limit, a counter) in a singleton you inject. Only the coordinator's own calls go through it: the auxiliary calls (classifier, safety checks, out-of-scope refusal, memory extraction, the lookup and action checks) use `ClassifierChatClient ?? ChatClient`, and specialists, team members and the handoff router use `ChatClient`. See its XML documentation |
| `EnableObservability` | `bool` | `false` | Emit OpenTelemetry traces/metrics (GenAI conventions) + MentorAgent spans/counters |
| `ObservabilityIncludeSensitiveData` | `bool` | `false` | Include prompt/response content in telemetry — **Development only** |
| `ObservabilitySourceName` | `string` | `"MentorAgent"` | ActivitySource/Meter name; add via `.AddSource(name).AddMeter(name)` |
| `ModelPricing` | `IReadOnlyDictionary<string,ModelPrice>?` | `null` | Per-model token prices for the dashboard cost estimate (none built in) |
| `DashboardRole` | `string` | `"Admin"` | Role required by `GET /mentor/admin/metrics` in [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) (`""` = open, dev only). The `MentorDashboard` component does **not** check it: protect the page that hosts it yourself (`@attribute [Authorize(Roles = "Admin")]`) |
| `MetricsPersistenceInterval` | `TimeSpan` | `30s` | Flush cadence for a registered `IMentorMetricsStore` (durable dashboard); also flushes on shutdown |
| `MetricsRetention` | `TimeSpan` | `7d` | How far back the dashboard keeps per-model hourly time-series buckets (temporal charts) |

### Model routing & AI utilities

| Property / service | Type | Default | Description |
|---|---|---|---|
| `StrongChatClient` | `IChatClient?` | `null` | Strong model to escalate to (`ChatClient` is the cheap default). Routing needs this **and** what the `RoutingStrategy` requires: the default `Custom` needs `UseStrongModelAsync` or `UseStrongModel`, and `Semantic` needs `EmbeddingGenerator`. Otherwise a warning is logged and every turn stays on `ChatClient` |
| `RoutingStrategy` | `MentorRoutingStrategy` | `Custom` | `Semantic` / `Classifier` / `Cascade` / `Custom` — how the cheap↔strong decision is made |
| `UseStrongModelAsync` | `Func<IReadOnlyList<ChatMessage>,CancellationToken,Task<bool>>?` | `null` | `Custom`: async, context-aware router (takes precedence over `UseStrongModel`) |
| `UseStrongModel` | `Func<string,bool>?` | `null` | `Custom`: legacy sync predicate on the latest user message |
| `RoutingComplexExemplars` | `IReadOnlyList<string>?` | `null` | `Semantic`: example "complex" turns (null → built-in multilingual set) |
| `RoutingThreshold` | `float` | `0.35` | `Semantic`: cosine floor to escalate (higher = stricter) |
| `RoutingClassifierClient` | `IChatClient?` | `null` | `Classifier`/`Cascade`: dedicated judge client (defaults to the cheap `ChatClient`) |
| `ClassifierChatClient` | `IChatClient?` | `null` | Client for MentorAgent's own one-word decisions — input and output safety checks, the hosted-tool / `RefuseOutOfScope` scope check, the request check of `RequireLookupForRecordQuestions` and `RequireActionForChangeRequests`, the check of a reply that announced an action (`CheckAnnouncedActions`, on by default, after the reply), post-turn fact extraction, and the A2A grounding verdict (does a tool-less reply to a peer state application data? BUG-081). Point it at a small, fast deployment: these calls sit in front of the reply (measured: 707–2091 ms on `gpt-4.1` for two tokens of answer). Defaults to `ChatClient`; metered under its own model id. On Path B (a pre-built `Agent`) it is the only client MentorAgent can call on its own: without it the safety checks are skipped |
| `IMentorQueryEmbedding` | *service* | — | The current message's embedding, computed once per turn and shared by routing, tool filtering and memory. Inject it in your `IMentorRagSource` instead of embedding the query again |
| `IMentorStructured` | *service* | — | `GenerateAsync<T>(...)` — typed/structured generation, see [Structured outputs](#structured-outputs) |
| `MentorEvaluator` | *service* | — | `RunAsync(cases, options)` — token/quality regression harness; `RunConversationsAsync(conversations, options)` — conversations through the mentor, with tools and streaming, see [Evaluation & regression testing](#evaluation--regression-testing) |

### Security & Limits

| Property | Type | Default | Description |
|---|---|---|---|
| `EnableSafetyCheck` | `bool` | `false` | AI-based prompt injection detection. Runs on `ClassifierChatClient`, or on `ChatClient` when it is not set: on Path B without one it is skipped, with a warning when the coordinator is built. A custom `InputGuardrail` replaces it |
| `SafetyCheckTimeout` | `TimeSpan` | `00:00:15` | How long the input and output safety checks (built-in or custom guardrail) may take before they count as failed. `TimeSpan.Zero` or negative = no limit; Stop still cancels a check in flight. Raise it for a slow local model, or point the checks at a fast `ClassifierChatClient` |
| `SafetyCheckFailure` | `MentorSafetyCheckFailure` | `Allow` | What a safety check with **no verdict** (timed out, provider error, custom guardrail threw) does: `Allow` processes the message / shows the reply and logs a warning; `Block` refuses the message ("can't check it right now, try again") or withholds the reply. An UNSAFE verdict blocks under both |
| `RefuseOutOfScope` | `bool` | `false` | Politely refuse a message that plainly has nothing to do with this application, without calling the coordinator. Asks its own question, which defaults to answering: a name the classifier does not recognise may be one of the application's records, and greetings, follow-ups and questions about the assistant are answered (BUG-105). A short reply ("yes", a choice the assistant offered) is judged with the end of the assistant's last reply, in the same call; the safety check still judges the message alone (BUG-223). With `EnableSafetyCheck` on it travels in the same classifier call; alone it adds one small call per turn. Judged against `HostedToolDomainScope`, or the app's name, description and pages with their descriptions. Counted in `GuardrailBlockCount` |
| `WarmUpAtStartup` | `bool` | `false` | Build one coordinator when the application starts, so the first visitor does not pay for the shared work: embedding the tool catalogue and the routing exemplars, opening the shared MCP sessions, fetching the remote agent cards. Runs once the host has started (after it starts listening); a failed warm-up never fails the boot, an invalid configuration does ([When the options are read and checked](#when-the-options-are-read-and-checked)). A MAUI app runs no hosted service, so there it does nothing |
| `WarmUpChatModel` | `bool` | `false` | With `WarmUpAtStartup`, one minimal request to the chat model (`ChatClient`, and `StrongChatClient` when set) once the coordinator is built, so the first turn does not pay for the connection, the provider's empty prompt cache or a local model still loading. At most 16 output tokens; it goes straight to the provider's client, so no history, memory, safety check or dashboard turn. **Every start costs one request per client.** A failure is a warning. Without `WarmUpAtStartup` it does nothing and a warning says so; with a pre-built `Agent` (Path B) it calls nothing |
| `MaxMessageLength` | `int` | `4000` | Max message length in characters (0 = unlimited) |
| `RateLimitPerUser` | `int` | `0` | Max messages per user per window (0 = disabled). A signed-in user is counted by `ClaimTypes.NameIdentifier`, else `sub`, `oid` or the name; a signed-out visitor according to `AnonymousIdentity`: per session by default, or one global `"anonymous"` counter shared by every visitor with `MentorAnonymousIdentity.Shared`. MCP `tools/call` has its own allowance with the same limit, per `NameIdentifier`, `sub` or `oid`; a caller with none is counted by remote address when `IHttpContextAccessor` is registered, otherwise all such callers share one |
| `RateLimitWindowSecs` | `int` | `60` | Rate limiting window in seconds |
| `RequireConfirmation` | `bool` | `true` | On/off for the confirmation dialogs of your actions, MCP-client servers and `RequiresApproval`. A hosted MCP server's approvals (`RequireApproval`, `AlwaysRequireApprovalTools`) still show the banner |
| `RequireConfirmationForWrites` | `bool` | `true` | A `[MentorAction]` declaring neither `ReadOnly = true` nor `RequiresConfirmation` asks for confirmation, and is therefore withheld from `/mcp` and the A2A card. Covers `[MentorAction]` methods only: `[Description]`-only methods ask nothing, as before. `false` = the rc.14 behaviour. See [Confirmation dialogs](#confirmation-dialogs) |
| `HitlMode` | `MentorHitlMode` | `Blocking` | How approval is implemented: `Blocking` (MentorAgent's own flow, one streamed run) or `Native` (AF `ApprovalRequiredAIFunction`, for interop; the same model calls, one more agent run per round of confirmations). Same banner and same client protocol either way — see [Human-in-the-loop tool approval](#human-in-the-loop-tool-approval). A later call of a model response with an id the response or the conversation already holds gets the next free `<id>_n` before the function-invoking client sees it, so two calls of one id no longer break the conversation (BUG-225). Two turns in a row that fail the same way while closing the requests left open log an error that suggests a new conversation (`ResetSessionAsync`) |
| `IsReadOnlyAction` | `Func<string, bool>?` | `null` | Declares by tool name the actions that only read — same as `[MentorAction(ReadOnly = true)]`, for `[Description]` methods or central declaration. Published to MCP clients as `readOnlyHint`, which some clients take as leave to skip their own confirmation; the actions `RequireLookupForRecordQuestions` may require. A confirmation-gated tool is never read-only |
| `RequiresApproval` | `Func<string, bool>?` | `null` | Forces approval for a tool **by name**, on top of `[MentorAction(RequiresConfirmation)]`. The way to gate tools you don't own (MCP, skills). Asked again on every call (in `Native` mode too), on every `tools/list` and `tools/call` of the MCP server and on every turn the tool filter matches a tool: keep it a cheap, pure function of the name |
| `ActionPolicyTimeout` | `TimeSpan` | `00:00:05` | How long each call to an [`IMentorActionPolicy`](#deciding-before-an-action-runs--imentoractionpolicy), or to the [`IMentorActionGovernor`](#governing-actions-from-outside-the-code--imentoractiongovernor), may take; then the call it was asked about does not run (`PolicyFailed`, `policy-timeout`), and when MentorAgent decides what to offer the action is left out. Zero or negative = no limit; Stop still cancels the wait |
| `ConfirmationMessage` | `Func<MentorConfirmationContext, string?>?` | `null` | Writes the text of the confirmation banner from the tool, its description, the arguments (in parameter order, cleaned, cut at 200 characters), `Language` and the scope's services. `null`, white space or an exception: the default text |
| `ConfirmationTimeout` | `TimeSpan` | `00:00:00` | How long a confirmation waits for its answer, from when the banner is shown; then it counts as rejected and the action does not run (`Rejected`, `confirmation-timeout`). Zero or negative = no limit. A few minutes suits a web host — see [Confirmation dialogs](#confirmation-dialogs) |
| `AutoApprovalRules` | `IList<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>` | empty | `Native` mode only — see [Approving without asking](#approving-without-asking--autoapprovalrules). Evaluated in order; the first `true` approves the call before any banner appears. Also enables "don't ask again". **Waives the prompt, never `RequiredRoles`** |
| `MaxAutoApprovalIterations` | `int?` | `null` | Cap on re-runs caused by auto-approval. `null` uses the framework's default. Each re-run is a fresh billable model call |
| `IncludeWorkflowExceptionDetails` | `bool` | `false` | Put the exception message of a failing Level-2 / Level-3 workflow agent (handoff, group chat) into the reply the user reads. **Development only, never in production** |

---

## Building your own UI — `IMentorStateService`

`ChatWidget` is a consumer of the same event bus you can subscribe to. Inject `IMentorStateService`
to build a custom transcript, a custom approval dialog, a header indicator — or to replace the
widget entirely while keeping the whole orchestration layer.

```razor
@inject IMentorStateService State
@implements IDisposable

@code {
    protected override void OnInitialized()
    {
        State.OnStreamingChunk += OnChunk;
        State.OnBusyChanged    += OnBusy;
    }

    // Events are raised off the render thread — always marshal back.
    private void OnChunk(string delta) => InvokeAsync(() => { _text += delta; StateHasChanged(); });
    private void OnBusy(bool busy)     => InvokeAsync(() => { _busy  = busy;  StateHasChanged(); });

    public void Dispose()
    {
        State.OnStreamingChunk -= OnChunk;   // unsubscribe, or the component leaks
        State.OnBusyChanged    -= OnBusy;
    }
}
```

### Driving a turn — `IMentorOrchestrator`

`IMentorStateService` is what comes **out** of a turn. `IMentorOrchestrator` is what you push **in**,
and it is the whole input surface — seven members, no more:

```csharp
@inject IMentorOrchestrator Mentor
@inject IMentorStateService State

@code {
    private async Task Send(string text) => await Mentor.SendMessageAsync(text);

    // Stop. Safe to call when nothing is running — it is a no-op, not a throw, which matters
    // because a button's enabled state can be stale after a reconnect. The partial reply stays
    // on screen, and the NEXT turn still works.
    private void Stop() => Mentor.CancelCurrentRequest();

    // New conversation: a fresh AgentSession. On Blazor Server this raises
    // IMentorSessionManager.OnSessionChanged (also raised by RestoreSessionAsync): inject it and
    // subscribe to clear a custom transcript. On WebAssembly no client-side event is raised,
    // so clear your transcript here after the call returns.
    private async Task Reset() => await Mentor.ResetSessionAsync();
}
```

| Member | What it does |
|---|---|
| `SendMessageAsync(string, CancellationToken)` | Run one turn |
| `SendMessageAsync(string, IReadOnlyList<MentorAttachment>?, CancellationToken)` | …with [images attached](#multimodal-image-input) |
| `CancelCurrentRequest()` | Stop the turn in flight. No-op when idle |
| `ResetSessionAsync(CancellationToken)` | Start a new conversation |
| `SerializeSessionAsync(CancellationToken)` | Snapshot the conversation — `null` when there is nothing to save. See [Session serialize and restore](#session-serialize-and-restore) |
| `RestoreSessionAsync(JsonElement, CancellationToken)` | Reinstate a snapshot, refusing anything that is not one of ours |
| `RespondToApprovalAsync(ConfirmationRequest, bool, CancellationToken)` | Answer a [pending approval](#human-in-the-loop-tool-approval) |

**Answering an approval: two routes, and which one you want depends on where your UI runs.**

```csharp
// Blazor Server / in-process — the click handler shares the DI scope with the parked turn:
await State.ConfirmAsync(req.ActionId);      // or State.Cancel(req.ActionId)

// Blazor WebAssembly, or any host driving approval from its own UI — goes over the wire:
await Mentor.RespondToApprovalAsync(req, approved: true);
```

Both release the same parked turn, on either hosting model. On WebAssembly both go over HTTP to
`/mentor/approve` rather than calling a hub method, because SignalR dispatches one hub call at a
time per connection and the turn awaiting approval is holding that slot, so a hub call would
deadlock against the very turn it is trying to release. Stop (`CancelCurrentRequest`) goes the same
way, to `/mentor/cancel`. With an absolute http(s) `HubUrl` both requests are sent to the **hub's
origin**, and with an `AccessTokenProvider` they carry `Authorization: Bearer <token>`, the same
credential the hub uses — so a signed-in user's confirmations and Stop are accepted cross-origin too.
With a relative `HubUrl` (same-origin hosting) they stay relative to the host's
`HttpClient.BaseAddress`; either way the client must register an `HttpClient`: the standalone WebAssembly template does, the client project of a Blazor Web App does not.
Use `ConfirmAsync` / `Cancel` when you hold the `ActionId`, and `RespondToApprovalAsync` when you
hold the `ConfirmationRequest`.

### Complete event reference

| Event | Signature | Fired when |
|---|---|---|
| `OnStreamingChunk` | `Action<string>` | Each streamed delta of the answer |
| `OnStreamingCompleted` | `Action` | The answer is complete |
| `OnBusyChanged` | `Action<bool>` | A turn starts / ends |
| `OnError` | `Action<string>` | Rate limit, guardrail block, or an unhandled failure |
| `OnActionExecuting` | `Action<string>` | A tool call started — the text shown to the user (the action's `Description`, else a readable form of its name, or the routing, team or hosted-tool label), not the tool name. `OnActionStarted` carries the tool name with that text, `OnActionCompleted` the tool name, `OnActionFailed` the tool name and the error |
| `OnActionStarted` | `Action<MentorActionStarted>` | A tool call started: `ToolName` is the tool's technical name, as the model called it, and `DisplayName` the text `OnActionExecuting` carries. Raised where the turn runs — the Blazor Server circuit, the MAUI app, the server for the hub, SSE and A2A — and not by the WebAssembly client; record actions on the server. |
| `OnActionCompleted` | `Action<string>` | A tool call succeeded |
| `OnActionFailed` | `Action<string>` | A tool call threw — `tool: message`, where the message follows `OnException` as above |
| `OnConfirmationRequired` | `Action<ConfirmationRequest>` | A gated tool is waiting for approval |
| `OnApprovalResponse` | `Action<ConfirmationRequest, bool>` | The user's answer reached the waiting turn — not raised after Stop, after the connection or circuit closed, or for a second answer |
| `OnNavigationRequested` | `Action<string>` | The AI wants to route somewhere |
| `OnUIActionExecuting` | `Action<string>` | A page-registered UI action started |
| `OnUIActionCompleted` | `Action<string>` | …and finished |
| `OnRagSourcesReady` | `Action<IReadOnlyList<MentorRagResult>>` | RAG retrieved documents for this turn |
| `OnGeneratedImages` | `Action<IReadOnlyList<string>>` | The hosted image tool produced images |
| `OnCardsReady` | `Action<IReadOnlyList<MentorCard>>` | A tool returned generative-UI cards |
| `OnMcpServerStatusChanged` | `Action<string, bool>` | An MCP server connected (`true`) or dropped |
| `OnHostedToolsDeclared` | `Action<MentorHostedTools>` | Once per connection — what the server actually enabled |
| `OnTeamMemberSpeaking` | `Action<string, string>` | Team name + member, during a Level-3 group chat |

Every event except `OnApprovalResponse` (raised by `ConfirmAsync` / `Cancel` when the answer reaches the waiting turn) has a matching
`Notify*` method on the same interface. Those belong to the orchestrator. Subscribe to the events;
do not raise them.

### Recording which tools ran

`OnActionStarted` carries each tool call's technical name (`ToolName`) next to the text shown
(`DisplayName`). It is raised where the turn runs, so in a Blazor Server app a component in the circuit,
for example in the layout, can log every call:

```razor
@* ActionLog.razor *@
@inject IMentorStateService State
@inject ILogger<ActionLog> Log
@implements IDisposable

@code {
    protected override void OnInitialized() => State.OnActionStarted += OnStarted;

    private void OnStarted(MentorActionStarted action)
        => Log.LogInformation("Tool {Tool} started ({Shown})", action.ToolName, action.DisplayName);

    public void Dispose() => State.OnActionStarted -= OnStarted;
}
```

The WebAssembly client never raises it: there, record actions on the server.

### Replacing the confirmation dialog

`OnConfirmationRequired` hands you a `ConfirmationRequest` with `ActionId`, `ToolName` and a
`Message` that already contains the concrete arguments the AI is about to pass. Answer with
`ConfirmAsync` or `Cancel`:

```csharp
private ConfirmationRequest? _pending;

protected override void OnInitialized()
    => State.OnConfirmationRequired += r => InvokeAsync(() => { _pending = r; StateHasChanged(); });

private async Task Approve()
{
    await State.ConfirmAsync(_pending!.ActionId);
    _pending = null;
}

private void Reject()
{
    State.Cancel(_pending!.ActionId);
    _pending = null;
}
```

The tool stays blocked until one of the two is called, so a dialog you forget to answer hangs that
turn — put `Cancel` on your modal's dismiss path as well as its reject button. A turn can also end
without your answer (Stop, an error, the connection or circuit closing): close the dialog on
`OnBusyChanged(false)` as well, since nothing waits for it any more.

This is unchanged by `HitlMode`: `Blocking` and the Agent Framework's `Native` flow both surface the
same event and take the same reply.

---

## Extensibility

| Extension point | How |
|---|---|
| Custom UI / transcript | Subscribe to `IMentorStateService` — see [Building your own UI](#building-your-own-ui--imentorstateservice) |
| Custom confirmation dialog | `OnConfirmationRequired` + `ConfirmAsync` / `Cancel` |
| Audit of the gate's decisions | Implement `IMentorActionObserver`, register with `AddMentorActionObserver<T>()` — see [Auditing the gate's decisions](#auditing-the-gates-decisions--imentoractionobserver) |
| Who confirmed an action, from its own code | `MentorActionConfirmation.Current` — see [Confirmation dialogs](#confirmation-dialogs) |
| Who may use an action, and whether a call may run, in your own code | Implement `IMentorActionPolicy`, register with `AddMentorActionPolicy<T>()` — see [Deciding before an action runs](#deciding-before-an-action-runs--imentoractionpolicy) |
| Turn actions on and off, set the roles they require, whether they are confirmed and the description the model reads, from outside the code, per session | Ship them with `[MentorAction(Enabled = false)]` and implement `IMentorActionGovernor` (`IsEnabledAsync`, `GetRequiredRolesAsync`, `GetConfirmationAsync`, `GetDescriptionAsync`), registered with `AddMentorActionGovernor<T>()` — see [Governing actions from outside the code](#governing-actions-from-outside-the-code--imentoractiongovernor) |
| The application's actions as MentorAgent sees them | Resolve `IMentorActionCatalog`, registered by `AddMentorAgent()` — see [Deciding before an action runs](#deciding-before-an-action-runs--imentoractionpolicy) |
| Custom card rendering | `<ChatWidget><CardTemplate>` , or `OnCardsReady` for full control |
| Custom onboarding tour | Implement `IMentorTour`, register before `AddMentorAgent()` |
| Custom metrics persistence | Implement `IMentorMetricsStore`, register before `AddMentorAgent()` |
| Custom memory store | Implement `IMentorMemoryStore`, register before `AddMentorAgent()` |
| Custom history provider | Set `options.ChatHistoryProviderFactory`: one provider per coordinator, like the default. `options.ChatHistoryProvider` is one instance shared by every user — see [Persistent conversation history](#persistent-conversation-history) |
| Custom AI provider | Set `options.ChatClient` or `options.Agent` |
| Model for MentorAgent's own checks | Set `options.ClassifierChatClient`: the safety checks, the scope check, the request check of `RequireLookupForRecordQuestions` and `RequireActionForChangeRequests`, the check of a reply that announced an action (`CheckAnnouncedActions`), fact extraction and the A2A grounding verdict run on it |
| Custom agent instructions | Set `Instructions` on `[MentorAgent]` or `[TeamMember]` |
| Custom RAG source | Implement `IMentorRagSource`, register before `AddMentorAgent()` |
| The message's embedding, computed once | Inject `IMentorQueryEmbedding` (in your `IMentorRagSource`, for one) instead of embedding the message again |
| Custom MCP tools | Add entries to `options.McpServers` (HTTP or stdio transport) |
| Custom remote agents | Add entries to `options.RemoteAgents` (A2A protocol) |
| Agents from your own store | Implement `IMentorAgentSource` and register it. `AddMentorAgentDeclarative` is one such source — see [Declarative agents (YAML)](#declarative-agents-yaml) |
| File-based skills | Create `Skills/{name}/SKILL.md` in the project root with `EnableSkills = true` |
| Skills from another source | Add an Agent Framework `AgentSkillsSource` to `options.SkillSources`; `options.SkillFilter` keeps or drops its skills — see [Skills from somewhere else](#skills-from-somewhere-else--agentskillssource) |
| Dual-role skill class | Decorate with `[MentorSkill]` + add `[Description]`/`[MentorAction]` methods — knowledge + tools in one class. Register in DI when the class has methods |
| Knowledge-only skill | Decorate with `[MentorSkill]` — no methods, no DI registration needed |
| Page UI actions (typed) | Use `RegisterUIAction<TParam>` or `RegisterUIActionAsync<TParam>` for automatic JSON deserialization |

---

## Requirements

| Requirement | Version |
|---|---|
| .NET | 10.0 |
| Blazor | For the widget: a Blazor Web App (Server or Auto render mode) or Hybrid (MAUI). Any ASP.NET Core app with `Novior.MentorAgent.Server`, no Blazor needed |
| Novior.MentorAgent.Abstractions | same version (installed with it) |
| Microsoft.Agents.AI | 1.27.0 |
| Microsoft.Agents.AI.Workflows | 1.27.0 |
| Microsoft.AspNetCore.Components.Authorization | 10.0.3 |
| Microsoft.AspNetCore.Components.Web | 10.0.3 |
| Microsoft.Extensions.AI | 10.11.0 |
| Microsoft.Extensions.VectorData.Abstractions | 10.10.0 (the vector store abstractions of `MentorVectorStoreRagSource`; Agent Framework brings it too) |
| A2A | 1.0.0-preview3 (the A2A client) |
| ModelContextProtocol | 2.3.0 (the MCP client) |
| System.ClientModel | 1.10.0 |

No AI provider SDK: the host adds its own — see [Provider package versions](#provider-package-versions).

The MCP and A2A **servers** are in `Novior.MentorAgent.Server`, with `ModelContextProtocol.AspNetCore` 2.3.0 and
`A2A.AspNetCore` 1.0.0-preview3. Up to 1.0.0-rc.14 `MentorAgent` referenced
`ModelContextProtocol.AspNetCore` and `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` itself, and with them
ASP.NET Core and the `Microsoft.Agents.AI.Hosting` packages.

`Microsoft.Agents.AI.Foundry` and `Azure.AI.Projects` are not dependencies of MentorAgent (they were up
to 1.0.0-rc.12, unused by the library). To use `AIProjectClient` or `FoundryEvals`, add
`Microsoft.Agents.AI.Foundry` yourself.

### Provider package versions

MentorAgent references no provider SDK, so `Azure.AI.OpenAI`, `OpenAI`, `Microsoft.Extensions.AI.OpenAI`,
`Microsoft.Agents.AI.OpenAI`, `Azure.Identity` and the rest come at the versions you choose. One
combination needs a hold:

**Azure OpenAI with the Responses API.** `Azure.AI.OpenAI` 2.9.0-beta.1, the latest published version,
calls a `ResponsesClient` constructor that `OpenAI` 2.11.0 removed. With `OpenAI` 2.11 or later — what
`Microsoft.Extensions.AI.OpenAI` 10.7.0+, `Microsoft.Agents.AI.OpenAI` and `Microsoft.Agents.AI.Foundry`
1.18.0+ bring in — `AzureOpenAIClient.GetResponsesClient()` throws when your code calls it, although the build
is clean:

```
System.MissingMethodException: Method not found: 'Void OpenAI.Responses.ResponsesClient..ctor(
System.ClientModel.Primitives.ClientPipeline, OpenAI.OpenAIClientOptions)'.
```

Hold `OpenAI` at 2.10:

```xml
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="[10.6.0]" />
<!-- and, only if you use it: -->
<PackageReference Include="Microsoft.Agents.AI.Foundry" Version="[1.17.0-preview.260804.1]" />
```

Azure OpenAI with Chat Completions (`GetChatClient`), OpenAI direct and Ollama through its OpenAI endpoint
need no hold.

**`Microsoft.Extensions.AI.OpenAI` 10.10.0 with `OpenAI` 2.14.** Every Responses API call that carries a tool
throws a `TypeLoadException`. `Microsoft.Extensions.AI.OpenAI` 10.10.1 fixes it: take that version or a later one.

Up to 1.0.0-rc.14 MentorAgent referenced `Azure.AI.OpenAI`, `Azure.Identity` and
`Microsoft.Extensions.AI.OpenAI [10.6.0]` itself, and stopped every build that resolved `OpenAI` 2.11 or
later with `error MENTOR001` — a project on the current Agent Framework that never called
`GetResponsesClient()` included. The check and its `MentorAgentSkipOpenAIVersionCheck` switch are gone with
those dependencies.

> ✅ **Blazor WebAssembly** is supported via `Novior.MentorAgent.Server` + `Novior.MentorAgent.Blazor`. See [Blazor Server vs Blazor WASM](#blazor-server-vs-blazor-wasm).
> ✅ **Blazor Web App with Auto render mode** is supported — either with `@rendermode="InteractiveServer"` on `<ChatWidget />` (simplest, with per-page interactivity) or via the full WASM setup with `Novior.MentorAgent.Server` + `Novior.MentorAgent.Blazor`.

---

## Migrating from the MentorAgent.* packages

From 1.0.0 the five packages are published as `Novior.MentorAgent.*`, and their namespaces, assemblies and
static-asset path follow. The `MentorAgent.*` IDs end with 1.0.0-rc.15: nothing newer is published under
them, and nuget.org marks them deprecated with the new ID as the alternative. The names your code calls do
not change (see *What stays the same* below): the move is a change of packages, `using` lines and paths.

| Up to 1.0.0-rc.15 | From 1.0.0 |
|---|---|
| `MentorAgent` | [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) |
| `MentorAgent.Abstractions` | [Novior.MentorAgent.Abstractions](https://www.nuget.org/packages/Novior.MentorAgent.Abstractions) |
| `MentorAgent.Blazor` | [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) |
| `MentorAgent.Server` | [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) |
| `MentorAgent.Declarative` | [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) |

**1 · Swap the packages** in every project that references one, a class library included.
`MentorAgent.Abstractions` comes with the others and needs no line of its own:

```bash
dotnet remove package MentorAgent.Server
dotnet add package Novior.MentorAgent.Server
```

**2 · Check that no old package is left.** This is the break most likely to go unnoticed: the old and the
new package side by side build without an error, your `using MentorAgent.*` lines keep resolving to the old
one, and the application goes on running 1.0.0-rc.15. In every project,

```bash
dotnet list package --include-transitive
```

must list no package whose ID is `MentorAgent` or starts with `MentorAgent.`.

**3 · Namespaces.** `MentorAgent.X` becomes `Novior.MentorAgent.X` in every `using` and `@using`
(`_Imports.razor` included) and in every fully qualified name; the type names stay. Replace there only, not
every `MentorAgent` in your code: `AddMentorAgent()`, `MapMentorAgent…()`, `[MentorAgent]` and
`MentorAgentBlazorOptions` keep their names.

```csharp
using Novior.MentorAgent.Extensions;           // was MentorAgent.Extensions
using Novior.MentorAgent.Abstractions.Models;  // was MentorAgent.Abstractions.Models
```

**4 · Static assets in a page you wrote.** The widget's stylesheet and script are served under the package
ID, `_content/Novior.MentorAgent.Abstractions/`. `<ChatWidget />` adds them itself in a Blazor Server or
Blazor Web App page; a page that links them by hand changes the path — the `wwwroot/index.html` of a Blazor
WebAssembly client or of a MAUI app:

```html
<link rel="stylesheet" href="_content/Novior.MentorAgent.Abstractions/css/MentorAgent.css" />
<script src="_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js"></script>
```

The old path answers `404` and nothing else complains: the widget shows without its styles and without
voice. The file names do not change.

**5 · Log filters.** Log categories follow the namespaces: `MentorAgent.Core.MentorOrchestrator` is now
`Novior.MentorAgent.Core.MentorOrchestrator`. A filter on `"MentorAgent"` no longer matches anything, and
the Debug lines it turned on stop without a warning:

```json
{ "Logging": { "LogLevel": { "Novior.MentorAgent": "Debug" } } }
```

**6 · Update the five together.** Each package requires the others at exactly its own version:
`Novior.MentorAgent.Server` 1.0.0 needs `Novior.MentorAgent` 1.0.0, not 1.0.0 or later.

**What stays the same:**

- the methods, types and attributes you call — `AddMentorAgent()`, `AddMentorAgentServer()`,
  `MapMentorAgent…()`, `MentorOptions`, `[MentorAgent]`, `[MentorAction]` — and the `"MentorAgent"`
  configuration section;
- the routes (`/mentor-hub`, `/mentor/…`, `/mcp`, `/a2a`), the CSS classes and what the widget keeps in
  the browser;
- the telemetry names: `AddSource("MentorAgent")` and `AddMeter("MentorAgent")` (`ObservabilitySourceName`),
  and the `mentoragent.*` metrics;
- the session snapshots: one saved by 1.0.0-rc.15 is restored by 1.0.0;
- the `MentorAgentMcp` CORS policy, the `MentorAgent.css` and `MentorAgent.js` files and `window.MentorAgent`.

---

## Running more than one instance

MentorAgent keeps the state of a conversation in the memory of the process that serves it: the turn in progress, a
confirmation waiting for its answer, the scope of a hub connection and its history. Behind a load balancer, **every
request of a client must reach the instance that serves it**: turn on session affinity ("sticky sessions") for the hub
connection **and** for the HTTP requests of the same client — `/mentor/approve`, `/mentor/cancel`,
`/mentor/session` — for example with an affinity cookie. Affinity on the SignalR connection alone is not enough.
Blazor Server already needs affinity for its circuits; a Blazor Server host that also serves the hub,
`/mentor/chat`, MCP or A2A needs it for those too.

- **No backplane.** MentorAgent sends each hub event only to the connection that asked for the turn, from the
  instance that holds it. A SignalR backplane (Redis, Azure SignalR Service) would not help either: confirmations,
  Stop and sessions are found in the memory of the instance, not through SignalR groups.
- **Confirmations and Stop on another instance.** `POST /mentor/approve` and `POST /mentor/cancel` answer 404 there,
  for a hub connection and for a `/mentor/chat` stream alike, and the turn goes on on its own instance. A
  confirmation then waits until `ConfirmationTimeout` — with no limit by default — a Stop, or the end of its
  connection: set `ConfirmationTimeout` where an answer can be lost this way.
- **A conversation that moves.** A SignalR reconnection is a new connection, with an empty history, on any instance,
  the same one included, unless the client restores its snapshot with `/mentor/session`. The snapshot opens on any
  instance that shares the Data Protection keys.
- **MCP server.** The SDK's stateful transport keeps each `Mcp-Session-Id` in the memory of the process; the MCP
  specification answers a session it does not know with 404.
- **A2A.** The A2A SDK keeps the tasks in memory: `GetTask` and `CancelTask` on another instance do not find them. A
  shared `ITaskStore` would not be enough for `CancelTask`, since only the instance that runs a turn can stop it. A
  MentorAgent caller that asks with `returnImmediately` reads its task with `GetTask`, so the peer needs affinity too.
- **Counted per instance.** `RateLimitPerUser` (a user spread over N instances gets up to N times the limit); the
  dashboard's metrics — with an `IMentorMetricsStore` each instance saves its own whole snapshot, so on a shared store
  the last write wins, it is not a sum; the generated tour, one model call per instance; the embedding and A2A card
  caches; the connections to MCP servers and remote agents.
- **Memory.** The default `InMemoryMentorMemoryStore` is per process: a fact remembered on one instance is not on the
  others. Register a shared `IMentorMemoryStore` (see
  [`InMemoryMentorMemoryStore`](#inmemorymentormemorystore--production-limitations)).

MentorAgent has no shared state store of its own: a deployment that cannot keep affinity is not supported today. This
section says what the code does; it has not been tried on a deployment of two instances.

---

## Serving more than one tenant

One instance of the application can serve more than one company — more than one *tenant*. MentorAgent needs to
know which tenant each request belongs to, and your application tells it with an `IMentorTenantResolver`:

```csharp
builder.Services.AddMentorAgent(options => { /* … */ });
builder.Services.AddMentorTenantResolver<ClaimTenantResolver>();   // one per application

public sealed class ClaimTenantResolver : IMentorTenantResolver
{
    public ValueTask<string?> ResolveAsync(MentorTenantRequest request, CancellationToken cancellationToken)
        => ValueTask.FromResult(request.User?.FindFirst("tenant_id")?.Value);   // null: the request is refused
}

// In an action, a policy, or a service the governor resolves from MentorActionRequest.Services:
public sealed class OrderActions(IMentorTenant tenant, OrderStore store)
{
    [MentorAction(Description = "Counts open orders", ReadOnly = true)]
    public Task<int> CountOpenOrders() => store.CountOpenAsync(tenant.Id!);
}
```

- **When it is asked.** Once for every unit of work, before anything else is done for it: when a turn starts — in a
  Blazor circuit, on the hub, on `/mentor/chat`, in a task from another agent (A2A) — and for every `tools/list` and
  `tools/call` of the MCP server. `MentorTenantRequest` carries the signed-in user (`User`; on the MCP server, the
  caller `McpCallerPrincipal` resolves, else the request's user), the surface (`Surface`, a `MentorActionSurface`)
  and the request's scoped services (`Services`), for an `IHttpContextAccessor` or anything else of your own.
- **What reads it.** `IMentorTenant.Id`, scoped: in a turn, the tenant resolved when the turn started; during a call
  to the MCP server, that call's own. The MCP server's tool classes are shared by every call, yet two callers at
  once each read their own. `IMentorTenant.GetIdAsync()` gives the same answer, and in a scope that had no turn yet
  (a page that only reads the memory, a restore before the first message) it asks the resolver first, under the
  same rules.
- **Never without a tenant.** A resolver that throws, or answers `null`, an empty string or white space, refuses the
  request: the turn is refused before the model is called (the user reads that the organization could not be
  identified, and an A2A task fails), a `tools/call` is answered with an error, and a `tools/list` lists nothing.
  Going on as the single tenant would put the request in the data every tenant shares. Only the exception's type is
  logged, never its message.
- **A conversation stays with its tenant.** A turn whose tenant is not the one its scope's first turn had (a
  circuit, a hub connection) is refused, and the user is told to reload the page to start a new conversation.
- **Memory and the rate limit are the tenant's own.** The key of the user's memories and of `RateLimitPerUser`
  (`MentorMemoryService.GetUserIdAsync()`) is prefixed with the tenant: `tenant:contoso/<the user's key>`. The same
  user in two tenants keeps two memories and two allowances, and the shared anonymous bucket
  (`MentorAnonymousIdentity.Shared`) is one per tenant. The tenant id is escaped in the key, so an id with a `/`
  cannot pass for another. A custom `IMentorMemoryStore` receives these keys as it always received the user's:
  nothing changes in its interface. The MCP server's allowance per caller is per tenant too.
- **A session snapshot is restored for its tenant only.** `SerializeSessionAsync` (and `GET /mentor/session`)
  writes the tenant into the snapshot. `RestoreSessionAsync` (and `POST /mentor/session`) refuses — with an
  `ArgumentException`, a 400 on the server — a snapshot of another tenant, one that names no tenant where a resolver
  is registered (nobody can say whose it is), and one that names a tenant where no resolver is. The conversation in
  place is not touched.
- **History.** The history MentorAgent keeps by default is a coordinator's own, so it is already apart.
  `ChatHistoryProviderFactory` runs in the conversation's scope, after the tenant is resolved: read `IMentorTenant`
  there to key your storage. A single `ChatHistoryProvider` instance serves every tenant; with a resolver
  registered, a Warning at start-up says so.
- **Metrics and the dashboard are the tenant's own.** Every counter and the turn's span carry `mentoragent.tenant`;
  the dashboard and `GET /mentor/admin/metrics` show the reader's tenant only. See
  [With more than one tenant](#with-more-than-one-tenant) in the token & cost dashboard.
- **Retrieval is the tenant's own.** A RAG source is given the turn's tenant in `MentorRagRequest.TenantId` and
  returns that tenant's documents; MentorAgent filters nothing itself. A source that implements only the
  `(query, maxResults)` overload is never given the tenant and serves every tenant the same documents: a Warning
  says so once, at its first search with a tenant.
- **The logs name the tenant.** Every log line of a turn, MentorAgent's and your own code's in that turn, and of a
  call to the MCP server is written in a scope with one pair, `mentoragent.tenant`, the key of the metrics and the
  span. A provider that includes scopes shows it: the console with `IncludeScopes = true`, OpenTelemetry with
  `IncludeScopes = true` (the attribute `mentoragent.tenant`), Serilog with `Enrich.FromLogContext()`. As text it
  reads `mentoragent.tenant=contoso`. Without a resolver there is no scope.
- **Before the first turn.** A reset or a restore that comes before the first turn resolves the tenant first, and
  builds nothing without one. Reading `IMentorTenant.Id` where nothing was resolved yet throws, instead of reading
  as the single tenant. The start-up warm-up (`WarmUpAtStartup`) serves nobody: it resolves no tenant and does not
  ask the governor, so it costs the governor nothing and leaves nothing to the sessions.
- **Without a resolver** there is one tenant, as before: `IMentorTenant.Id` is `null` and nothing else changes.
- **Budgets are not MentorAgent's.** A budget that stops a tenant belongs to the service in front of it.

The two texts the user reads come from the localizer, in the 10 languages: `tenant.unresolved` (*"This request
could not be processed: the organization it belongs to could not be identified."*) and `tenant.changed` (*"This
conversation belongs to another organization. Reload the page to start a new one."*).

---

## Versioning and support

- **One version for the family.** Novior.MentorAgent, Novior.MentorAgent.Abstractions,
  Novior.MentorAgent.Blazor, Novior.MentorAgent.Server and Novior.MentorAgent.Declarative are released
  together with one version number, and each one requires the others at exactly that version. Upgrade them
  together.
- **Semantic Versioning 2.0.0, from 1.0.0.** A breaking change to the public contract ships only in a new
  major version. Minor versions add features; patch versions fix defects.
- **What the contract is:** the public C# API of the five packages; the hub methods, hub events, SSE
  frames and HTTP endpoints documented in the Novior.MentorAgent.Server README; the names of the tools the
  library itself gives the model (`route_to_specialist`, `navigate_to`, `load_skill` and the others this
  README documents); and the documented configuration keys.
- **What it is not:** what the model says or which tool it chooses on a given turn, the wording of the
  prompts the library builds, and log messages. The `Novior.MentorAgent` log category prefix is stable; the
  text after it is not.
- **Dependencies.** Microsoft Agent Framework, Microsoft.Extensions.AI, ModelContextProtocol and A2A are checked
  every day for new versions, and a new version is taken in the next minor version of MentorAgent. A minor version
  may move them to a newer compatible line, including one with no stable release yet, as
  `Microsoft.Agents.AI.Declarative` and `A2A` are today. The versions in [Requirements](#requirements) are the ones
  each release is tested with. NuGet lets an application take newer ones, and nothing is guaranteed for them; a
  newer version that breaks MentorAgent — as A2A 1.0.0-preview3 did with 1.1.0 — is fixed in the next version and
  listed under *Known issues* in the CHANGELOG until then. The AI provider's packages are not among them: their
  versions are the host's (see [Provider package versions](#provider-package-versions)).
- **Support.** .NET 10. The latest minor version gets every fix. When a new minor version is released, the
  previous one still gets security fixes for 3 months; when a new major version is released, the previous major
  gets them for 6 months. 1.0.x gets security fixes until 21 January 2027. A vulnerability report is acknowledged
  within 5 working days, best effort, and its fix is announced as a GitHub Security Advisory and in the release
  notes. See [SECURITY.md](https://github.com/Novior-dev/MentorAgent/blob/main/SECURITY.md).
- **Questions, bugs and requests.** Questions go to
  [Discussions](https://github.com/Novior-dev/MentorAgent/discussions), bugs and feature requests to
  [issues](https://github.com/Novior-dev/MentorAgent/issues/new/choose) on GitHub, and a vulnerability privately to
  security@mentoragent.net. See [SUPPORT.md](https://github.com/Novior-dev/MentorAgent/blob/main/SUPPORT.md).
- **What changed.** Every package contains `CHANGELOG.md` with the whole history, and its release notes
  on nuget.org are that version's section.

---

## Related Packages

| Package | Purpose |
|---------|---------|
| **Novior.MentorAgent** ← you are here | Blazor Server and .NET MAUI Blazor Hybrid — the full AI assistant in one package |
| [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) | Any ASP.NET Core app — headless AI backend (SignalR + SSE), and the MCP server and A2A agent for any host, Blazor Server included |
| [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) | Blazor WASM / Auto client — the same widget over SignalR |
| [Novior.MentorAgent.Abstractions](https://www.nuget.org/packages/Novior.MentorAgent.Abstractions) | Shared contracts, models and UI components (transitive — never installed directly) |
| [Novior.MentorAgent.Declarative](https://www.nuget.org/packages/Novior.MentorAgent.Declarative) | Optional — Level-2 specialists defined in YAML |

---

## License

Apache License 2.0 — the full text is in the `LICENSE` file inside this package, with the copyright notice in `NOTICE`. Versions up to and including 1.0.0-rc.14 were released under the MIT License, which still applies to them.

MentorAgent is a trademark: see [TRADEMARKS.md](https://github.com/Novior-dev/MentorAgent/blob/main/TRADEMARKS.md).
