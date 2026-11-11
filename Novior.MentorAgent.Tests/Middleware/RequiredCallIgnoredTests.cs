using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// Story 167 — <c>RequireLookupForRecordQuestions</c> and <c>RequireActionForChangeRequests</c> require the first call
/// with <c>ChatToolMode.RequireAny</c>, <c>tool_choice: "required"</c> on the wire. A provider that ignores it — Ollama
/// does — answered in text, and the options did nothing with no sign of it. A request that required a call and came
/// back with none now leaves a warning, once per provider client: a provider that honours the requirement cannot
/// answer so. A call to a tool the request did not offer is still a call, and so is one that arrives streamed.
/// </summary>
public sealed class RequiredCallIgnoredTests
{
    private const string Warning = "without calling one: RequireLookupForRecordQuestions and RequireActionForChangeRequests have no effect";

    private static int Warnings(TestLogCollector logs)
        => logs.Records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains(Warning, StringComparison.Ordinal));

    // ── The middleware, on both paths ────────────────────────────────────────────

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ILoggerFactory _factory;
        internal MentorHostFixture Host { get; }
        internal FakeChatClient Provider { get; } = new();
        internal TestLogCollector Logs { get; } = new();
        internal UIActionsMiddleware Middleware { get; }

        internal Rig()
        {
            Host = MentorHostFixture.Create();
            _factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(Logs)));
            Middleware = new UIActionsMiddleware(
                Provider, Host.Get<IMentorPageContext>(), Host.Get<IMentorStateService>(),
                _factory.CreateLogger<UIActionsMiddleware>(), Provider);
        }

        public async ValueTask DisposeAsync()
        {
            _factory.Dispose();
            await Host.DisposeAsync();
        }
    }

    /// <summary>A request that requires a call, as the tool filter and the forced read send it.</summary>
    private static ChatOptions Required()
        => ToolFilterChatClient.Forced(new ChatOptions(), [AIFunctionFactory.Create(() => 3, "count_open_orders")], null);

    private static readonly ChatMessage[] Question = [new(ChatRole.User, "How many orders are open?")];

    private static async Task StreamAsync(UIActionsMiddleware middleware, ChatOptions? options)
    {
        await foreach (var _ in middleware.GetStreamingResponseAsync(Question, options)) { }
    }

    [Fact]
    public async Task A_required_call_answered_in_text_leaves_a_warning_on_either_path()
    {
        await using (var rig = new Rig())
        {
            rig.Provider.Say("There are 3 open orders.");
            await rig.Middleware.GetResponseAsync(Question, Required());
            Warnings(rig.Logs).Should().Be(1);
        }

        await using (var rig = new Rig())
        {
            rig.Provider.Say("There are 3 open orders.");
            await StreamAsync(rig.Middleware, Required());
            Warnings(rig.Logs).Should().Be(1, "streamed, the updates are looked at as they go by");
        }
    }

    [Fact]
    public async Task A_call_never_leaves_the_warning_streamed_or_not_even_to_a_tool_the_request_did_not_offer()
    {
        await using var rig = new Rig();
        rig.Provider.CallTool("count_open_orders").CallTool("count_open_orders")
            .CallTool("not_offered_tool").CallTool("not_offered_tool");

        await rig.Middleware.GetResponseAsync(Question, Required());
        await StreamAsync(rig.Middleware, Required());
        await rig.Middleware.GetResponseAsync(Question, Required());
        await StreamAsync(rig.Middleware, Required());

        Warnings(rig.Logs).Should().Be(0, "the provider honoured the requirement: what the model called is the gate's business");
    }

    [Fact]
    public async Task A_request_that_required_no_call_answered_in_text_leaves_no_warning()
    {
        await using var rig = new Rig();
        rig.Provider.Say("Hello!").Say("Hello!");

        await rig.Middleware.GetResponseAsync(Question, new ChatOptions { ToolMode = ChatToolMode.RequireAny });
        await StreamAsync(rig.Middleware, null);

        Warnings(rig.Logs).Should().Be(0, "only the marker of a forced call says a call was required by MentorAgent");
    }

    [Fact]
    public async Task The_warning_is_said_once_per_provider_client()
    {
        await using var rig = new Rig();
        rig.Provider.SayRepeatedly("There are 3 open orders.", 3);

        await rig.Middleware.GetResponseAsync(Question, Required());
        await StreamAsync(rig.Middleware, Required());
        // Another session's pipeline over the same provider client.
        var other = new UIActionsMiddleware(rig.Provider, rig.Host.Get<IMentorPageContext>(), rig.Host.Get<IMentorStateService>(),
            LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(rig.Logs))).CreateLogger<UIActionsMiddleware>(), rig.Provider);
        await other.GetResponseAsync(Question, Required());

        Warnings(rig.Logs).Should().Be(1);
    }

    // ── In a turn ────────────────────────────────────────────────────────────────

    private static readonly string[] Reads = ["count_open_orders", "get_active_orders", "show_order_card"];

    private static MentorHostFixture ForcedReadHost()
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.RequireLookupForRecordQuestions = true;
            o.IsReadOnlyAction = name => Reads.Contains(name);
            o.MemoryAutoCapture = false;
        });
        host.Auth.SignIn("ada", "Manager", "Admin");
        return host;
    }

    [Fact]
    public async Task A_provider_that_ignores_tool_choice_in_a_turn_is_reported_once_and_the_turn_answers()
    {
        await using var host = ForcedReadHost();
        // The classifier says it is a record question; the provider answers the forced request in text, twice.
        host.Chat!.Say("LOOKUP").Say("Ci sono 3 ordini aperti.").Say("LOOKUP").Say("Ancora 3.");

        await host.RunTurnAsync("Quanti ordini sono aperti?");
        await host.RunTurnAsync("E adesso quanti sono?");

        host.Chat.Calls.Where(c => c.ToolNames.Count > 0).Should().OnlyContain(c => c.ToolMode == ChatToolMode.RequireAny);
        Warnings(host.Logs).Should().Be(1, host.Logs.WarningText);
    }

    [Fact]
    public async Task Two_sessions_over_one_provider_client_report_it_once()
    {
        await using var host = ForcedReadHost();
        host.Chat!.Say("LOOKUP").Say("Ci sono 3 ordini aperti.").Say("LOOKUP").Say("Ancora 3.");

        await host.RunTurnAsync("Quanti ordini sono aperti?");
        // A second session: a sibling scope, a pipeline of its own over the same MentorOptions.ChatClient.
        await using (var other = host.Get<IServiceScopeFactory>().CreateAsyncScope())
            await other.ServiceProvider.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("Quanti ordini sono aperti?");

        host.Chat.Calls.Count(c => c.ToolNames.Count > 0 && c.ToolMode == ChatToolMode.RequireAny).Should().Be(2);
        Warnings(host.Logs).Should().Be(1, "once per provider client, not per session");
    }

    [Fact]
    public async Task A_provider_that_honours_tool_choice_in_a_turn_is_not_reported()
    {
        await using var host = ForcedReadHost();
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync("Quanti ordini sono aperti?");

        Warnings(host.Logs).Should().Be(0, host.Logs.WarningText);
    }
}
