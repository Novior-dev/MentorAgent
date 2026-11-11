# MentorAgent

[![NuGet](https://img.shields.io/nuget/v/Novior.MentorAgent.svg)](https://www.nuget.org/packages/Novior.MentorAgent)
[![Downloads](https://img.shields.io/nuget/dt/Novior.MentorAgent.svg)](https://www.nuget.org/packages/Novior.MentorAgent)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Build](https://dev.azure.com/novior/Novior/_apis/build/status/6?branchName=master)](https://dev.azure.com/novior/Novior/_build/latest?definitionId=6&branchName=master)

MentorAgent is an open-source .NET library that puts AI agents inside your applications, under your rules: your C#
methods become tools, and on each one you declare which roles may use it and whether the user must confirm it first.
Use the ready-made chat widget in Blazor and MAUI, or connect any front end (React, Angular, Vue, mobile) over SignalR
or SSE; AI assistants and other agents reach the same tools over MCP and A2A.

**Website:** [mentoragent.net](https://mentoragent.net) · **Documentation:** [docs.mentoragent.net](https://docs.mentoragent.net) · **Blog:** [mentoragent.net/blog](https://mentoragent.net/blog)

Built on Microsoft Agent Framework, with any provider that offers an `IChatClient`: Azure OpenAI, OpenAI, Ollama and others.

## Which package

| You are building | Install |
|---|---|
| A Blazor Server app, or a .NET MAUI Blazor Hybrid app | `Novior.MentorAgent` |
| The client project of a Blazor WebAssembly or Auto app | `Novior.MentorAgent.Blazor`, with `Novior.MentorAgent.Server` on the server |
| Any other frontend (React, Angular, Vue, mobile), a Web API or a headless backend; an MCP server or an A2A agent | `Novior.MentorAgent.Server` |
| Specialist agents defined in YAML | `Novior.MentorAgent.Declarative`, next to the others |

`Novior.MentorAgent.Abstractions` comes with them: you never install it yourself.

## Quick start

```bash
dotnet add package Novior.MentorAgent
dotnet add package Microsoft.Extensions.AI.OpenAI
```

```csharp
// Program.cs — using Microsoft.Extensions.AI; using Novior.MentorAgent.Extensions;
builder.Services.AddScoped<OrderService>();
builder.Services.AddMentorAgent(options =>
{
    options.AppName        = "Orders";
    options.ChatClient     = new OpenAI.Chat.ChatClient("gpt-4.1-mini", builder.Configuration["OpenAI:Key"]).AsIChatClient();
    options.ScanAssemblies = [typeof(Program).Assembly];
});

// OrderService.cs — using Novior.MentorAgent.Attributes;
public class OrderService
{
    [MentorAction(Description = "Cancels an order", RequiresConfirmation = true, RequiredRoles = ["Manager"])]
    public Task<bool> CancelOrderAsync(int orderId, string reason) { ... }
}
```

```razor
@* MainLayout.razor, in an interactive app — @using Novior.MentorAgent.Abstractions.Components *@
<ChatWidget />
```

Ask the widget to cancel an order: the assistant offers `CancelOrderAsync` only to a signed-in user with the
`Manager` role, and runs it only after that user confirms in the widget.

## Start from a template

A clone of this repository has a `dotnet new` template, [templates/mentoragent-blazor](templates/mentoragent-blazor):
a Blazor Server helpdesk with MentorAgent inside, and a test project.

```bash
dotnet new install templates/mentoragent-blazor      # from a clone of this repository
dotnet new mentoragent-blazor -n Helpdesk
cd Helpdesk
dotnet user-secrets set "Assistant:ApiKey" "<your key>" --project Helpdesk
dotnet run --project Helpdesk
```

The app keeps five tickets in memory and has three actions: `list_open_tickets` reads, `assign_ticket` asks for
a confirmation, and `close_ticket` asks for one too and needs the `Agent` role. A test sign-in at the top of the
page switches between Alex, an `Agent`, and Sam, who has no role and is never offered `close_ticket`; replace
`TestSignIn` with your application's authentication. The model comes from the `Assistant` section of the
configuration: `Model` (default `gpt-4.1-mini`), `ApiKey` in the user secrets, and `Endpoint`, empty for OpenAI
or the address of an OpenAI-compatible service, Azure OpenAI included. Without a key the app starts and the
page says what is missing.

`dotnet test` runs three tests with no key and no network: a scripted model drives the same assistant through
`MentorEvaluator.RunConversationsAsync` — Alex closes a ticket after confirming, the same request with the
default no leaves it open, and Sam is never offered `close_ticket`.

The template is not a NuGet package yet: install it from a clone.

## Learn more

- **Documentation:** [docs.mentoragent.net](https://docs.mentoragent.net)
- **Website:** [mentoragent.net](https://mentoragent.net)
- **The full reference:** [the package README](Novior.MentorAgent/README.md), the same as on
  [nuget.org](https://www.nuget.org/packages/Novior.MentorAgent)
- **A running example:** the [demo](demo/), a small expense-claims app with MentorAgent inside
- **Help and contributions:** [SUPPORT.md](SUPPORT.md), [CONTRIBUTING.md](CONTRIBUTING.md),
  [CODE_OF_CONDUCT.md](.github/CODE_OF_CONDUCT.md)
- **Vulnerabilities:** [SECURITY.md](SECURITY.md) — privately, never in a public issue
- **Action rules:** [ACTION-RULES.md](ACTION-RULES.md) — which methods become actions, and how roles
  and confirmations add up
- **What changed:** [CHANGELOG.md](CHANGELOG.md)
- **What 1.3.0 promises:** [the API notes](docs/api-notes-1.3.0.md) — which methods become actions, YAML agents
  while the application runs, and the options that may change while it runs

## License

Apache License 2.0: see [LICENSE](LICENSE) and [NOTICE](NOTICE). "MentorAgent" and its logo are trademarks: see
[TRADEMARKS.md](TRADEMARKS.md) for how they can be used.
