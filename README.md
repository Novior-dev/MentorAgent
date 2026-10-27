# MentorAgent

[![NuGet](https://img.shields.io/nuget/v/Novior.MentorAgent.svg)](https://www.nuget.org/packages/Novior.MentorAgent)
[![Downloads](https://img.shields.io/nuget/dt/Novior.MentorAgent.svg)](https://www.nuget.org/packages/Novior.MentorAgent)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

Add AI agents to your .NET app: your methods become tools with role checks and human confirmation.
Use the ready-made chat widget in Blazor and MAUI, or connect any frontend through SignalR, SSE, MCP or A2A.
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

## Learn more

- **Documentation:** [docs.mentoragent.net](https://docs.mentoragent.net)
- **Website:** [mentoragent.net](https://mentoragent.net)
- **The full reference:** [the package README](Novior.MentorAgent/README.md), the same as on
  [nuget.org](https://www.nuget.org/packages/Novior.MentorAgent)
- **A running example:** the [demo](demo/), a small expense-claims app with MentorAgent inside
- **Help and contributions:** [SUPPORT.md](SUPPORT.md), [CONTRIBUTING.md](CONTRIBUTING.md),
  [CODE_OF_CONDUCT.md](.github/CODE_OF_CONDUCT.md)
- **Vulnerabilities:** [SECURITY.md](SECURITY.md) — privately, never in a public issue
- **What changed:** [CHANGELOG.md](CHANGELOG.md)

## License

Apache License 2.0: see [LICENSE](LICENSE) and [NOTICE](NOTICE). "MentorAgent" and its logo are trademarks: see
[TRADEMARKS.md](TRADEMARKS.md) for how they can be used.
