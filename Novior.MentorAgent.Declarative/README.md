# Novior.MentorAgent.Declarative

> **New package IDs.** From 1.0.0 the packages, their namespaces and their assemblies are `Novior.MentorAgent.*`; up to 1.0.0-rc.15 they were `MentorAgent.*`. See *Migrating from the MentorAgent.\* packages* in the [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) README.

> **Optional package.** Install it only if you want to define specialist agents in **YAML files**
> instead of C# classes. Everything MentorAgent does works without it.

Define a Level-2 specialist in a text file, drop it next to your application, and the assistant can hand off to it — no new class, no `[MentorAgent]` attribute, no recompile of the agent's behaviour.

Built on the Agent Framework's declarative agent factory (`Microsoft.Agents.AI.Declarative`).

---

## Package Family

| Package | Install when |
|---------|-------------|
| [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) | Blazor Server app |
| [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) | Web API / headless backend, or Blazor Auto server-side project |
| [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) | Blazor WASM / Blazor Auto client project |
| [Novior.MentorAgent.Abstractions](https://www.nuget.org/packages/Novior.MentorAgent.Abstractions) | Never directly — it arrives with any of the above |
| **Novior.MentorAgent.Declarative** ← you are here | You want YAML-defined agents. Add it alongside `Novior.MentorAgent` or `Novior.MentorAgent.Server` |

---

## Table of Contents

- [What it does](#what-it-does)
- [Getting started](#getting-started)
- [The YAML format](#the-yaml-format)
  - [Two format details that cost an afternoon](#two-format-details-that-cost-an-afternoon)
- [Tools: named, not defined](#tools-named-not-defined)
- [Security — a definition file is code](#security--a-definition-file-is-code)
- [When to use YAML and when to use C#](#when-to-use-yaml-and-when-to-use-c)
- [Loading definitions from somewhere else](#loading-definitions-from-somewhere-else)
- [Configuration options](#configuration-options)
  - [`ConfigurationSection` — exposing configuration to a definition](#configurationsection--exposing-configuration-to-a-definition)
- [How to test it](#how-to-test-it)
- [Why a separate package](#why-a-separate-package)
- [Requirements](#requirements)
- [Related Packages](#related-packages)
- [License](#license)

---

## What it does

MentorAgent's [three-level agent model](https://www.nuget.org/packages/Novior.MentorAgent) has a coordinator (L1) that can hand off to specialists (L2). Normally a specialist is a C# class:

```csharp
[MentorAgent(Name = "ShippingAgent", Description = "Answers questions about deliveries")]
public class ShippingAgent                      // register it: builder.Services.AddScoped<ShippingAgent>();
{
    [MentorAction(Description = "Looks up a tracking number")]   // tool name: get_tracking
    public string GetTracking(int orderId) => /* … */;
}
```

This package adds a second way to declare the *agent* — its name, its instructions, its model settings and which tools it may use — as a file:

```yaml
kind: Prompt
name: ShippingAgent
description: Answers questions about deliveries and shipping costs
instructions: |
  You handle shipping questions only. Use the available tools to look up real orders;
  never invent a tracking number. If the question is not about shipping, say so and stop.
model:
  options:
    temperature: 0.2
tools:
  - kind: function
    name: get_order_status
  - kind: function
    name: get_all_orders
```

Both kinds end up in the same handoff graph, so `route_to_specialist` reaches them identically and the user cannot tell which is which.

---

## Getting started

### Installation

```bash
dotnet add package Novior.MentorAgent.Declarative
```

### Registration

```csharp
using Novior.MentorAgent.Declarative;   // AddMentorAgentDeclarative
using Novior.MentorAgent.Extensions;    // AddMentorAgent

builder.Services.AddMentorAgentDeclarative(o => o.Directory = "Agents");

builder.Services.AddMentorAgent(o =>
{
    o.ChatClient     = chatClient;
    o.AppName        = "ShopFlow";
    o.ScanAssemblies = [typeof(Program).Assembly];
});
```

Order does not matter — MentorAgent asks every registered agent source while it builds a coordinator. A coordinator is built lazily for **each session scope** (each Blazor circuit, each SignalR connection to Novior.MentorAgent.Server, each SSE request), on its first message, or once at startup when `WarmUpAtStartup` is on. So the definitions are read again for every new scope. An edited file reaches new sessions without a restart, existing sessions keep the agents they were built with, and the log lines shown under [How to test it](#how-to-test-it) appear then, not when the application starts.

### Make sure the files reach the output folder

A definition that is not copied is the most common way this feature appears not to work. In your `.csproj`:

```xml
<ItemGroup>
  <Content Include="Agents\**\*.agent.yaml" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

If the directory is missing you get a warning naming the resolved path, `[MentorAgent:Declarative] Directory '…' does not exist.`, when a coordinator is built: on a session's first message, or at startup with `WarmUpAtStartup`. That message exists because the failure is otherwise silent.

---

## The YAML format

The schema comes from the Agent Framework, not from MentorAgent. The fields that matter in practice:

| Field | Meaning |
|---|---|
| `kind` | `Prompt` for a prompt-based agent. Required |
| `name` | The specialist's name. This is what appears in handoff logs — keep it stable |
| `description` | What this specialist is for. The coordinator reads it to decide when to route here |
| `instructions` | The agent's system prompt, used **exactly as written**. Use a `\|` block for multiple lines. MentorAgent adds nothing to it, including the grounding rule its generated specialist prompts carry, so write your own: *state only facts a tool returned; if nothing provides what is asked, say so, and never estimate or invent a number, a name, a date or a status*. Without it, a specialist asked for a figure its tools cannot return may make one up |
| `model.options` | `temperature`, `topP`, and other per-agent model settings |
| `tools` | Names of tools this agent may call — each entry needs `kind` **and** `name`, see below |
| `outputSchema` | Optional JSON-schema-style shape for a typed answer |

`description` is worth care: it is the coordinator's only basis for choosing this agent over another. "Handles orders" competes badly with "Handles orders, shipping and returns for existing customers".

### Two format details that cost an afternoon

Neither is in the Agent Framework guide, and both fail in a way that points somewhere else.

**Every `tools` entry needs `kind`.** The reader accepts `codeInterpreter`, `fileSearch`,
`function`, `webSearch` and `mcp`, but `function` is the only one MentorAgent keeps: it binds to a
tool of your application. Omit `kind` and loading fails with `NotSupportedException` — not a
validation message naming the line.

The other four are **not** names of your tools, and they do not add anything. The Agent Framework
would turn them into provider-hosted tools (web search, code interpreter, file search, or a remote
MCP server at whatever endpoint the file gives), configured by the file alone. MentorAgent removes
them from the agent when it loads and logs a warning naming them:

```
[MentorAgent:Declarative] 'shipping.agent.yaml': removed web_search. A definition file can only use the application's own tools (`kind: function`); web search, code interpreter, file search and MCP servers are configured by the host, through MentorOptions.HostedTools and McpServers.
```

The agent still loads, with its `function` tools only. If those tools cannot be taken out, the
whole agent is skipped with an error instead. To give the assistant these capabilities, configure
them in the host (`MentorOptions.HostedTools`, `McpServers`), where the host's own controls apply.

**Folded scalars (`>`) are not supported by this reader.** Use `|`, or a single line. With `>` the
parse error reports the *end of the file*, so you will look everywhere except at the block that
caused it:

```yaml
description: >                     # ✗ parse error, blamed on the last line of the file
  Handles shipping and delivery.

description: |                     # ✓
  Handles shipping and delivery.

description: Handles shipping.     # ✓
```

---

## Tools: named, not defined

A `tools:` entry **names** a tool; it does not create one. The name must match one of your application's **Level-1 methods**: a public instance method with `[MentorAction]` or `[Description]` on a class in `ScanAssemblies` that is registered in DI, including a `[MentorSkill]` class. That is the whole list the factory is given. The methods of a C# `[MentorAgent]` specialist, MCP client tools, `[MentorTeam]` tools and the built-in tools (navigation, memory, UI actions) are **not** in it, so naming one of them binds nothing real (see below).

```yaml
tools:
  - kind: function              # required — see below
    name: get_order_status      # must match exactly; a typo gives the agent a declaration with nothing behind it
```

The tool name is the **snake_case of the C# member**, with a trailing `Async` dropped:
`GetOrderStatusAsync()` → `get_order_status`, matched exactly and case-sensitively. Get it wrong and
nothing tells you at load time. The agent does not simply go without the tool: for a name that matches
nothing, the Agent Framework creates a **declaration-only** function of that name and gives it to the
agent. The model sees it and may call it, but there is no code behind it, so the call never runs and
the specialist has no data to answer with. Check every name against your `[MentorAction]` /
`[Description]` methods.

This is the design point of the whole package. MentorAgent hands the factory the application's real tool list, **already wrapped in its gate**, so a YAML agent calling `create_order` still hits:

- `RequiredRoles` — the role check, exactly as a C# specialist does
- human approval — the confirmation banner, if the tool requires one
- action feedback, per-tool metrics and tracing

An agent defined in a file therefore has no capability your application did not already have, and no shortcut around the controls on it. That holds for the other `kind` values too: `webSearch`, `codeInterpreter`, `fileSearch` and `mcp` entries would add provider-hosted tools outside your list, so MentorAgent removes them with a warning (see [Two format details that cost an afternoon](#two-format-details-that-cost-an-afternoon)).

---

## Security — a definition file is code

Read this before pointing `Directory` anywhere.

A definition chooses the model, writes the system instructions, and names the tools the agent may call. Anyone who can write that file can rewrite the assistant's persona and widen which tools it reaches for. That makes it **code**, whatever its file extension says.

- **Load only from deploy-time locations.** An application directory or an embedded resource. Never an upload folder, never a user-writable path, never a path built from request input.
- **Review definitions like source.** Put them in version control and through the same review as a `.cs` file.
- **The gate still holds.** A file cannot invent a tool or bypass a role check — that is enforced, not advisory. A `kind: function` entry resolves only against your own, gated tools, and a `webSearch`, `codeInterpreter`, `fileSearch` or `mcp` entry is removed with a warning, so a file cannot add web search, a code interpreter or an MCP server of its own choosing. But it *can* instruct the agent to try things, so the controls on your tools remain the thing that actually stops it.

The second point is the one people skip: YAML feels like configuration, and configuration feels safe to let more people edit.

---

## When to use YAML and when to use C#

| | YAML | C# `[MentorAgent]` |
|---|---|---|
| Change an agent's instructions | Edit a file | Recompile |
| New tool / new logic | Not possible — tools stay in C# | Where it belongs |
| Compile-time checking | None; a bad tool name is silent | Full |
| Who can author it | Anyone who can edit a reviewed file | Developers |
| Fits when | Wording and routing get tuned often | The agent has real behaviour |

A good rule: **behaviour in C#, phrasing in YAML.** If you find yourself wanting a loop or a branch in a definition file, that agent wants to be a class.

You can mix freely — both kinds coexist in the same handoff graph.

---

## Loading definitions from somewhere else

Definitions do not have to be files. Pass them as strings for agents stored in a database, a configuration service, or a test:

```csharp
builder.Services.AddMentorAgentDeclarative(o =>
{
    o.Definitions.Add("""
        kind: Prompt
        name: FaqAgent
        description: Answers frequently asked questions about the shop
        instructions: Answer briefly, in the user's language. Say so when you do not know.
        """);
});
```

To ship definitions **inside the assembly**, the safest deploy-time location, embed them and pass their text as inline definitions:

```xml
<ItemGroup>
  <EmbeddedResource Include="Agents\**\*.agent.yaml" />
</ItemGroup>
```

```csharp
builder.Services.AddMentorAgentDeclarative(o =>
{
    var asm = typeof(Program).Assembly;
    foreach (var name in asm.GetManifestResourceNames()
                            .Where(n => n.EndsWith(".agent.yaml", StringComparison.Ordinal))
                            .Order(StringComparer.Ordinal))
    {
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        o.Definitions.Add(reader.ReadToEnd());
    }
});
```

For a fully custom source — one that hits your own store, or refreshes on a schedule — implement `IMentorAgentSource` from the `Novior.MentorAgent` package directly and register it. `AddMentorAgentDeclarative` is one implementation of that interface, not a privileged path:

```csharp
using Novior.MentorAgent.Core;       // IMentorAgentSource, MentorAgentSourceContext
using Microsoft.Agents.AI;    // AIAgent

public sealed class DatabaseAgentSource : IMentorAgentSource
{
    // Called once per coordinator build, i.e. once per session, not once per process.
    // Cache here if reading your store is expensive.
    public async Task<IReadOnlyList<AIAgent>> GetAgentsAsync(
        MentorAgentSourceContext context, CancellationToken ct = default)
    {
        // context.ChatClient — your MentorOptions.ChatClient wrapped only in the token meter
        //                      (not the coordinator's pipeline), so usage lands in the metrics
        // context.Tools      — the Level-1 tools, already gated
        …
    }
}

builder.Services.AddSingleton<IMentorAgentSource, DatabaseAgentSource>();
```

---

## Configuration options

| Option | Type | Default | Description |
|---|---|---|---|
| `Directory` | `string?` | `null` | Folder to scan, absolute or relative to the app base directory. **Deploy-time paths only** |
| `SearchPattern` | `string` | `"*.agent.yaml"` | File pattern inside `Directory` |
| `Recursive` | `bool` | `false` | Scan subdirectories too |
| `Definitions` | `IList<string>` | empty | YAML supplied inline, loaded in addition to `Directory` |
| `ConfigurationSection` | `string?` | `null` | Name of the configuration section the YAML may reference. `null` exposes nothing |

### `SearchPattern` and `Recursive` — what the scan is allowed to reach

Both defaults are deliberately narrow, and widening them is a decision worth making on purpose
rather than by accident:

```csharp
builder.Services.AddMentorAgentDeclarative(o =>
{
    o.Directory = "Agents";

    // Default "*.agent.yaml", not "*.yaml". A deployment folder holds other YAML — a CI file,
    // a Helm values file — and reading one of those as an agent definition would at best fail
    // loudly. Widen it only if your definitions genuinely do not carry the suffix.
    o.SearchPattern = "*.agent.yaml";

    // Default false. A nested folder is exactly where a definition gets added without review,
    // and a definition file is code: it picks the model, writes the instructions and names the
    // callable tools. Turn it on when your layout needs it, not "just in case".
    o.Recursive = true;    // now Agents/support/*.agent.yaml is loaded too
});
```

Inline `Definitions` are loaded first (named `inline[0]`, `inline[1]`, … in the log), then files in a **stable order** (sorted by path), so two definitions declaring the same agent
name resolve identically on every machine rather than depending on the file system's enumeration
order. A missing `Directory` is warned about — almost always a `CopyToOutputDirectory` miss, where
the assistant otherwise starts fine and is simply missing a specialist nobody thinks to look for.

### `ConfigurationSection` — exposing configuration to a definition

Default `null`: no configuration reaches the YAML at all, and the definitions are self-contained.
Set it to expose one section, so a definition can reference values instead of hardcoding them:

```json
// appsettings.json
{
  "AgentSettings": {
    "SupportEmail": "help@contoso.com",
    "MaxRefund": "250"
  }
}
```

```csharp
builder.Services.AddMentorAgentDeclarative(o =>
{
    o.Directory            = "Agents";
    o.ConfigurationSection = "AgentSettings";   // only AgentSettings:* reaches the YAML
});
```

The values arrive as Power Fx **variables**, one per key, named by the key's **full configuration path**: `AgentSettings:SupportEmail`, `AgentSettings:MaxRefund`, plus one for the section `AgentSettings` itself. The factory enumerates the section with `AsEnumerable()`, which does not make paths relative.
How a definition references them is part of the Agent Framework's declarative schema, not something
MentorAgent defines, so check the framework's documentation for the expression syntax before
relying on it.

**Name a section. Never hand over the whole `IConfiguration`.** The factory loads whatever
configuration it is given into the Power Fx engine as variables — one per key, **in its
constructor**. A single key that Power Fx rejects as a name (in the version this package uses, an
empty or whitespace-only key) takes the entire factory down before any definition is read. The
exception does not say which key it was: Power Fx's message is the fixed text `Invalid name: ${name}`.
The placeholder is never filled in, so do not look for a key literally named `${name}`. This is not
hypothetical: a key contributed by an unrelated configuration provider produced

```
ArgumentException: Invalid name: ${name}
```

and no agents loaded at all. Naming one section bounds the blast radius to keys you control.

MentorAgent catches that failure and logs the cause rather than letting it surface as a generic
startup error, then returns no agents:

```
[MentorAgent:Declarative] The agent factory rejected the configuration exposed to YAML
(AgentSettings). Every key in it becomes a Power Fx variable and must be a valid identifier.
Narrow MentorDeclarativeOptions.ConfigurationSection, or leave it null.
```

If you see it, the fix is a narrower section — or `null`, which is the right setting unless you
actually need substitution.

---

## How to test it

1. Put `shipping.agent.yaml` in an `Agents` folder, with `CopyToOutputDirectory`.
2. Start the app and send a first message (or set `WarmUpAtStartup = true`, which builds a coordinator at startup). The log should then show, at Information level (the tool count is every Level-1 tool handed to the factory, not the number the file names):
   ```
   [MentorAgent:Declarative] Agent 'ShippingAgent' loaded from shipping.agent.yaml (12 tool(s) available to it).
   [MentorAgent] Handoff: declarative agent 'ShippingAgent' added to workflow.
   ```
3. Ask something in that agent's area — *"where is order 1001?"*. The answer should come back through it.
4. **Negative check — a bad file does not take the app down.** Break the YAML deliberately: you get
   `'shipping.agent.yaml' could not be loaded — skipped`, and everything else still starts.
5. **Negative check — the gate holds.** Name a tool carrying `RequiredRoles` in the YAML and ask the
   agent to use it while unauthenticated. It must be refused, the same way a C# specialist is, and no
   action taken.

---

## Why a separate package

`Microsoft.Agents.AI.Declarative` brings the **Power Fx interpreter** (YAML expressions are Power Fx), the Agents object model in three assemblies, `Microsoft.ML.Tokenizers` and several more.

That is a fair price for file-based authoring and pure overhead for everyone else, so it stays out of the `Novior.MentorAgent` core package. Installing this one is an explicit decision to pay it.

The reference is `Microsoft.Agents.AI.Declarative` **1.18.0-rc1**, on the same 1.18 line as `Microsoft.Agents.AI` 1.18.0 in the core package, so adding it does not move the rest of the library onto a different Agent Framework version. The declarative package is still a prerelease upstream.

---

## Requirements

- .NET 10
- `Novior.MentorAgent` (or `Novior.MentorAgent.Server`) configured with a `ChatClient` — declarative agents need one, and are skipped with a warning without it
- A provider supporting the model options you use in the definitions

---

## Related Packages

| Package | Purpose |
|---------|---------|
| [Novior.MentorAgent](https://www.nuget.org/packages/Novior.MentorAgent) | Blazor Server — full AI assistant |
| [Novior.MentorAgent.Server](https://www.nuget.org/packages/Novior.MentorAgent.Server) | Any ASP.NET Core app — headless AI backend |
| [Novior.MentorAgent.Blazor](https://www.nuget.org/packages/Novior.MentorAgent.Blazor) | Blazor WASM — SignalR client |
| [Novior.MentorAgent.Abstractions](https://www.nuget.org/packages/Novior.MentorAgent.Abstractions) | Shared contracts and UI components |

---

## License

Apache License 2.0 — the full text is in the `LICENSE` file inside this package, with the copyright notice in `NOTICE`. Versions up to and including 1.0.0-rc.14 were released under the MIT License, which still applies to them.
