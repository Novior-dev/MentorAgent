using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Declarative;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// Story 1274 — the agents of an <see cref="IMentorAgentSource"/> as a promised behaviour. A source is asked for every new
/// session, with the session's id, and what it loaded and what it left out reaches every
/// <see cref="IMentorAgentSourceObserver"/>: a definition that does not load, or a tool taken away from an agent, is seen
/// by whoever governs the agents, not only by whoever reads the server's log.
/// </summary>
public sealed class AgentSourceReportTests
{
    /// <summary>Gives one agent and counts its calls, by <c>GetAgentsAsync</c> alone: a source written before 1.3.0.</summary>
    private sealed class CountingSource : IMentorAgentSource
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal ConcurrentQueue<string?> Sessions { get; } = new();

        public Task<IReadOnlyList<AIAgent>> GetAgentsAsync(MentorAgentSourceContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            Sessions.Enqueue(context.SessionId);
            return Task.FromResult<IReadOnlyList<AIAgent>>(
                [new ChatClientAgent(context.ChatClient, "You handle returns.", "ReturnsAgent", "Handles returns and refunds.")]);
        }
    }

    private sealed class EmptySource : IMentorAgentSource
    {
        public Task<IReadOnlyList<AIAgent>> GetAgentsAsync(MentorAgentSourceContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AIAgent>>([]);
    }

    /// <summary>Throws with a message that must not travel: the source is the host's code.</summary>
    private sealed class FailingSource : IMentorAgentSource
    {
        public Task<IReadOnlyList<AIAgent>> GetAgentsAsync(MentorAgentSourceContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("Server=db;Password=hunter2");
    }

    /// <summary>Says what it left out, through <c>LoadAsync</c>.</summary>
    private sealed class RejectingSource : IMentorAgentSource
    {
        public Task<IReadOnlyList<AIAgent>> GetAgentsAsync(MentorAgentSourceContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AIAgent>>([]);

        public Task<MentorAgentSourceResult> LoadAsync(MentorAgentSourceContext context, CancellationToken ct = default)
            => Task.FromResult(new MentorAgentSourceResult(
                [], [new("refunds.yaml", MentorAgentRejectionReasons.InvalidDefinition, "line 3: unexpected key")]));
    }

    private sealed class RecordingObserver : IMentorAgentSourceObserver
    {
        internal ConcurrentQueue<MentorAgentSourceReport> Reports { get; } = new();

        public void OnAgentsLoaded(MentorAgentSourceReport report) => Reports.Enqueue(report);
    }

    private sealed class ThrowingObserver : IMentorAgentSourceObserver
    {
        public void OnAgentsLoaded(MentorAgentSourceReport report) => throw new InvalidOperationException("observer down");
    }

    private static RecordingObserver Observer(IServiceProvider services)
        => services.GetServices<IMentorAgentSourceObserver>().OfType<RecordingObserver>().Single();

    private static string SessionOf(IServiceProvider services) => services.GetRequiredService<MentorSessionIdentity>().Id;

    private static List<AIAgent> ExternalAgents(MentorHostFixture host)
        => (List<AIAgent>)host.Orchestrator.GetType()
            .GetField("_externalAgents", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(host.Orchestrator)!;

    // ── When a source is asked ───────────────────────────────────────────────────

    [Fact]
    public async Task A_source_is_asked_once_per_session_with_its_id_and_not_at_every_turn()
    {
        var source = new CountingSource();
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false,
            s => s.AddSingleton<IMentorAgentSource>(source));
        host.Chat!.SayRepeatedly("ok", 8);

        await host.RunTurnAsync("ciao");
        await host.RunTurnAsync("ancora");
        source.Calls.Should().Be(1, "a session builds its coordinator once: a turn does not ask the source again");

        // Another session — another circuit, another hub connection — is another scope of the same application.
        await using var other = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        try { await other.ServiceProvider.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("ciao"); }
        catch { /* the scripted model may not see the turn through; the source is asked before the model */ }

        source.Calls.Should().Be(2, "a new session builds a coordinator of its own, and asks the source again");
        source.Sessions.Should().Equal([SessionOf(host.Scope.ServiceProvider), SessionOf(other.ServiceProvider)],
            "the source is told the session, by the id the governor and the action observers are given");
        source.Sessions.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task On_mentor_chat_a_source_is_asked_at_every_request_and_on_the_hub_once_per_connection()
    {
        // What the README promises: on /mentor/chat a request is a session, and its coordinator is built for it.
        var source = new CountingSource();
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false,
            configureServices: s => s.AddSingleton<IMentorAgentSource>(source));
        app.Chat.SayRepeatedly("ok", 12);

        (await app.Client.PostAsJsonAsync("/mentor/chat", new { message = "ciao" })).Dispose();
        (await app.Client.PostAsJsonAsync("/mentor/chat", new { message = "ancora" })).Dispose();
        source.Calls.Should().Be(2, "every request to /mentor/chat builds its coordinator. Warnings: {0}", app.Logs.WarningText);
        source.Sessions.Should().OnlyHaveUniqueItems("each request is a session of its own");

        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendUserMessage", new { text = "ciao", attachments = (object?)null, language = "it" });
        await hub.InvokeAsync("SendUserMessage", new { text = "ancora", attachments = (object?)null, language = "it" });
        source.Calls.Should().Be(3, "a hub connection is one session: its second message does not ask the source again");
    }

    // ── What the observers receive ───────────────────────────────────────────────

    [Fact]
    public async Task Every_source_is_reported_with_what_it_loaded_also_when_it_loaded_nothing()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddSingleton<IMentorAgentSource>(new CountingSource());
            s.AddSingleton<IMentorAgentSource>(new EmptySource());
            s.AddMentorAgentSourceObserver<RecordingObserver>();
        });
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");

        var reports = Observer(host.Scope.ServiceProvider).Reports.ToList();
        reports.Select(r => r.Source).Should().Equal(typeof(CountingSource).FullName, typeof(EmptySource).FullName);
        reports[0].Loaded.Should().Equal("ReturnsAgent");
        reports[1].Loaded.Should().BeEmpty("a source that loaded nothing is reported too: no agent, not silence");
        reports.Should().AllSatisfy(r =>
        {
            r.Rejected.Should().BeEmpty("a source with GetAgentsAsync alone leaves nothing out");
            r.SessionId.Should().Be(SessionOf(host.Scope.ServiceProvider));
        });
    }

    [Fact]
    public async Task A_source_that_throws_is_reported_by_the_exception_s_type_and_the_others_still_load()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddSingleton<IMentorAgentSource>(new FailingSource());
            s.AddSingleton<IMentorAgentSource>(new CountingSource());
            s.AddMentorAgentSourceObserver<RecordingObserver>();
        });
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");

        var reports = Observer(host.Scope.ServiceProvider).Reports.ToList();
        var failed = reports.Single(r => r.Source == typeof(FailingSource).FullName);
        failed.Loaded.Should().BeEmpty();
        failed.Rejected.Should().Equal(new MentorAgentRejection(
            typeof(FailingSource).FullName!, MentorAgentRejectionReasons.SourceFailed, nameof(InvalidOperationException)));
        reports.Single(r => r.Source == typeof(CountingSource).FullName).Loaded.Should().Equal("ReturnsAgent");
        host.Logs.HasWarning("(source-failed)").Should().BeTrue(host.Logs.WarningText);
        host.Logs.WarningText.Should().NotContain("hunter2", "only the type: the source's message can carry anything");
    }

    [Fact]
    public async Task What_a_source_leaves_out_reaches_the_observers_and_a_warning_with_the_code()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddSingleton<IMentorAgentSource>(new RejectingSource());
            s.AddMentorAgentSourceObserver<RecordingObserver>();
        });
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");

        Observer(host.Scope.ServiceProvider).Reports.Should().ContainSingle().Which.Rejected.Should().Equal(
            new MentorAgentRejection("refunds.yaml", MentorAgentRejectionReasons.InvalidDefinition, "line 3: unexpected key"));
        host.Logs.HasWarning("refunds.yaml (invalid-definition)").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task An_observer_that_throws_stops_no_session_and_no_other_observer()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddSingleton<IMentorAgentSource>(new CountingSource());
            s.AddMentorAgentSourceObserver<ThrowingObserver>();
            s.AddMentorAgentSourceObserver<RecordingObserver>();
        });
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");

        host.Chat.Calls.Last(c => c.Tools.Count > 0).Instructions.Should().Contain("ReturnsAgent",
            "the agent joined the coordinator although an observer threw");
        Observer(host.Scope.ServiceProvider).Reports.Should().ContainSingle("the next observer is still called");
        host.Logs.HasWarning("threw (InvalidOperationException)").Should().BeTrue(host.Logs.WarningText);
        host.Logs.WarningText.Should().NotContain("observer down");
    }

    [Fact]
    public void The_reason_codes_are_the_documented_ones_and_do_not_change()
    {
        // Whoever governs the agents matches on these strings: they stay the same from one version to the next.
        typeof(MentorAgentRejectionReasons).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Should().BeEquivalentTo(["invalid-definition", "not-an-agent", "foreign-tools", "invalid-configuration",
                                      "source-failed", "tool-not-found", "hosted-tool-removed"]);
    }

    // ── A definition and the actions of the session ──────────────────────────────

    [Fact]
    [Trait("Rule", "declarative.functions-match")]
    [Trait("Rule", "enabled.off-by-default")]
    public async Task A_definition_that_names_an_action_off_for_the_session_gives_its_agent_no_such_tool()
    {
        // Found with story 1274: archive_order ships off, so the session has no archive_order — yet a definition that
        // named it got from the Agent Framework's factory a function of that name with nothing behind it. Nothing ran,
        // but the agent's model read the name of an action that is off, against enabled.off-by-default. It is taken away
        // and reported as a name the session does not have.
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddMentorAgentSourceObserver<RecordingObserver>();
            s.AddMentorAgentDeclarative(d => d.Definitions.Add("""
                kind: GptComponentMetadata
                name: OrdersDesk
                description: Counts and archives the orders.
                instructions: You count and archive orders.
                tools:
                  - kind: function
                    name: count_open_orders
                  - kind: function
                    name: archive_order
                """));
        });
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");

        var agent = ExternalAgents(host).Should().ContainSingle(host.Logs.ExceptionText).Subject;
        ((ChatOptions)agent.GetService(typeof(ChatOptions))!).Tools!.Select(t => t.Name)
            .Should().Equal(["count_open_orders"], "an action that is off for the session is described to no model");
        var report = Observer(host.Scope.ServiceProvider).Reports.Should().ContainSingle().Subject;
        report.Loaded.Should().Equal("OrdersDesk");
        var rejection = report.Rejected.Should().ContainSingle().Subject;
        rejection.Name.Should().Be("inline[0]");
        rejection.Tool.Should().Be("archive_order");
        rejection.ReasonCode.Should().Be(MentorAgentRejectionReasons.ToolNotFound);
    }
}
