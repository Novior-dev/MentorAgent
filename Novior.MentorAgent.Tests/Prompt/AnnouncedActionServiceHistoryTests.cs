using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CallObserver = Novior.MentorAgent.Tests.Gating.ModelCallObserverTests.CallObserver;
using Recorded = Novior.MentorAgent.Tests.Tenancy.TenantMetricsTests.Recorded;

namespace Novior.MentorAgent.Tests.Prompt;

/// <summary>
/// BUG-238 — with the history kept by the service (<see cref="MentorOptions.UseServiceManagedHistory"/>, the Responses
/// API) <see cref="MentorOptions.CheckAnnouncedActions"/> did nothing: every message of a request is stored there, and a
/// second request without one is refused (400, missing_required_parameter 'input'). The responses form a tree: the second
/// request is sent from the response the turn started from, with the user's message again and the check's instruction
/// in the request's instructions, on a copy of the session the session manager moves to once that request succeeded.
/// The first reply stays on the service on a branch nobody follows. Only for a turn of one request: a turn that called a
/// tool is not branched, so nothing that ran is ever left out of the conversation.
/// </summary>
public sealed class AnnouncedActionServiceHistoryTests
{
    private static readonly string Instruction =
        AnnouncedActionChatClient.Heading + MentorPromptTexts.English.AnnouncedActionNotRun;

    private static MentorHostFixture Host(FakeChatClient? classifier, Action<MentorOptions>? configure = null,
        Action<IServiceCollection>? services = null, Action<IServiceCollection>? afterMentorAgent = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = true;
            o.UseServiceManagedHistory = true;
            if (classifier is not null) o.ClassifierChatClient = classifier;
            configure?.Invoke(o);
        }, services, withRecorder: true, afterMentorAgent: afterMentorAgent);

    private static FakeChatClient Classifier(params string[] answers)
    {
        var classifier = new FakeChatClient();
        foreach (var answer in answers) classifier.Say(answer);
        return classifier;
    }

    /// <summary>A reply of the Responses API: text, with the id of the response, which the next request names.</summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> Text(FakeChatClient chat, string text, string? id)
        => (_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = chat.ModelId, ConversationId = id };

    /// <summary>A reply of the Responses API that calls <paramref name="tool"/>.</summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> Call(FakeChatClient chat, string tool, string? id)
        => (_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-" + id, tool, new Dictionary<string, object?>())]))
        {
            ModelId = chat.ModelId,
            ConversationId = id,
        };

    /// <summary>The coordinator's requests: the ones that carried tools.</summary>
    private static List<FakeChatClient.RecordedCall> Coordinator(FakeChatClient chat)
        => [.. chat.Calls.Where(c => c.Tools.Count > 0)];

    private static bool HeadingIn(IEnumerable<ChatMessage> messages)
        => messages.Any(m => (m.Text ?? "").Contains(AnnouncedActionChatClient.Heading));

    private static string? SnapshotConversationId(JsonElement snapshot)
        => snapshot.GetProperty("session").TryGetProperty("conversationId", out var id) ? id.GetString() : null;

    // ── The branch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_second_request_is_sent_from_the_response_the_turn_started_from_with_the_users_message()
    {
        var classifier = Classifier("NONE", "ANNOUNCED", "NONE");
        await using var host = Host(classifier);
        var chat = host.Chat!;
        chat.Respond(Text(chat, "Hello.", "resp_1"))
            .Respond(Text(chat, "Procedo a contare gli ordini aperti…", "resp_2"))
            .Respond(Call(chat, "count_open_orders", "resp_3"))
            .Respond(Text(chat, "Ci sono 3 ordini aperti.", "resp_4"))
            .Respond(Text(chat, "Prego.", "resp_5"));

        (await host.RunTurnAsync("ciao")).Should().BeNull();
        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull(host.Logs.WarningText);
        (await host.RunTurnAsync("grazie")).Should().BeNull();

        var requests = Coordinator(chat);
        requests.Select(r => r.ConversationId).Should().Equal(
            new string?[] { null, "resp_1", "resp_1", "resp_3", "resp_4" },
            "the second request branches from the turn's start, its tool's result goes on from its own response, and " +
            "the next turn starts from the end of the branch, not from the reply left behind (resp_2)");

        var (first, second) = (requests[1], requests[2]);
        second.Messages.Select(m => m.Text).Should().Equal(first.Messages.Select(m => m.Text),
            "the user's message is sent again, and only it");
        HeadingIn(second.Messages).Should().BeFalse("a message is stored on the service: the instruction is not one");
        second.Instructions.Should().Contain(Instruction);
        second.Instructions!.Replace("\n\n" + Instruction, "").Should().Be(first.Instructions,
            "the instruction is added to the request's instructions, the agent's and this request's notices kept");
        first.Instructions.Should().Contain(MentorPromptTexts.English.ActionsNotAvailable.Trim()[..40],
            "the notice of the actions this user may not run is one of them");
        second.ToolNames.Should().Equal(first.ToolNames, "the same tools");
        (requests[3].Instructions ?? "").Should().NotContain(AnnouncedActionChatClient.Heading,
            "the request that reads the tool's result is not told again");
        HeadingIn(requests.SelectMany(r => r.Messages)).Should().BeFalse();

        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.CountOpenOrders));
        host.Recorder!.StreamedText.Should().Contain("Procedo a contare").And.Contain("Ci sono 3 ordini aperti.",
            "the user reads the first reply, already streamed, and then the second, as with the history kept here");
        host.Logs.Records.Should().Contain(r => r.Message.Contains("on a branch from the turn's start"));
        classifier.CallCount.Should().Be(3, "each turn's reply is checked once, the second request's never");
    }

    [Fact]
    public async Task On_the_first_turn_the_second_request_starts_a_conversation_and_the_instruction_is_no_message()
    {
        // The first request of a conversation names no response: there the instruction would have been a user message at
        // the end, stored on the service like every other.
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier);
        var chat = host.Chat!;
        chat.Respond(Text(chat, "Procedo a contare gli ordini aperti…", "resp_1"))
            .Respond(Call(chat, "count_open_orders", "resp_2"))
            .Respond(Text(chat, "Ci sono 3 ordini aperti.", "resp_3"))
            .Respond(Text(chat, "ok", "resp_4"));

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull(host.Logs.WarningText);
        classifier.Say("NONE");
        (await host.RunTurnAsync("grazie")).Should().BeNull();

        var requests = Coordinator(chat);
        requests.Select(r => r.ConversationId).Should().Equal(new string?[] { null, null, "resp_2", "resp_3" });
        HeadingIn(requests[1].Messages).Should().BeFalse();
        requests[1].Instructions.Should().EndWith(Instruction);
        requests[1].Messages.Select(m => m.Text).Should().Equal(requests[0].Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task The_users_message_is_sent_again_whole_with_its_picture()
    {
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier, o => o.EnableImageInput = true);
        var chat = host.Chat!;
        chat.Respond(Text(chat, "Ho registrato la ricevuta.", "resp_1"))
            .Respond(Text(chat, "Non è stato fatto.", "resp_2"));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        await host.Orchestrator.SendMessageAsync("registra questa ricevuta",
            [new MentorAttachment { MimeType = "image/png", DataBase64 = Convert.ToBase64String(png) }]);

        var requests = Coordinator(chat);
        requests.Should().HaveCount(2, host.Logs.WarningText);
        var resent = requests[1].Messages.Should().ContainSingle().Subject;
        resent.Contents.OfType<DataContent>().Should().ContainSingle()
            .Which.Data.ToArray().Should().Equal(png, "the picture is part of the message sent again");
        resent.Text.Should().Be(requests[0].Messages.Single().Text);
    }

    // ── What is kept ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_snapshot_after_the_turn_holds_the_branch_and_the_turn_after_a_restore_goes_on_from_it()
    {
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier);
        var chat = host.Chat!;
        chat.Respond(Text(chat, "Procedo a contare gli ordini aperti…", "resp_1"))
            .Respond(Call(chat, "count_open_orders", "resp_2"))
            .Respond(Text(chat, "Ci sono 3 ordini aperti.", "resp_3"));
        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull(host.Logs.WarningText);

        var snapshot = (await host.Orchestrator.SerializeSessionAsync())!.Value;
        SnapshotConversationId(snapshot).Should().Be("resp_3", "the snapshot is taken from the branch's end");

        await using var restoredHost = Host(Classifier("NONE"));
        var restoredChat = restoredHost.Chat!;
        restoredChat.Respond(Text(restoredChat, "Prego.", "resp_4"));
        await restoredHost.Orchestrator.RestoreSessionAsync(snapshot);
        (await restoredHost.RunTurnAsync("grazie")).Should().BeNull();

        Coordinator(restoredChat).Single().ConversationId.Should().Be("resp_3");
    }

    [Fact]
    public async Task A_second_request_that_fails_leaves_the_session_on_the_first_reply()
    {
        var classifier = Classifier("ANNOUNCED", "NONE");
        await using var host = Host(classifier);
        var chat = host.Chat!;
        chat.Respond(Text(chat, "Procedo a contare gli ordini aperti…", "resp_1"))
            .Respond((_, _) => throw new HttpRequestException("503"))
            .Respond(Text(chat, "ok", "resp_2"));

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull("the user has the first reply");
        (await host.RunTurnAsync("e allora?")).Should().BeNull();

        host.Logs.WarningText.Should().Contain("second request after an announced action failed");
        Coordinator(chat).Select(r => r.ConversationId).Should().Equal(new string?[] { null, null, "resp_1" },
            "a branch whose request failed is not the session's: the conversation goes on from the reply it had");
    }

    [Fact]
    public async Task A_stop_during_the_second_request_stops_it_and_leaves_the_session_on_the_first_reply()
    {
        var classifier = Classifier("ANNOUNCED", "NONE");
        await using var host = Host(classifier);
        var chat = host.Chat!;
        chat.HonourCancellation = true;
        chat.Respond(Text(chat, "Procedo a contare gli ordini aperti…", "resp_1"))
            .Respond((_, _) =>
            {
                host.Orchestrator.CancelCurrentRequest();   // Stop while the second request runs
                throw new OperationCanceledException();
            })
            .Respond(Text(chat, "ok", "resp_2"));

        await host.RunTurnAsync("quanti ordini sono aperti?");
        (await host.RunTurnAsync("e allora?")).Should().BeNull();

        host.Get<TestOrderService>().Invoked.Should().BeEmpty("the second request was stopped before it called anything");
        Coordinator(chat).Select(r => r.ConversationId).Should().Equal(new string?[] { null, null, "resp_1" },
            "nothing after the Stop; the next turn goes on from the reply the user had");
        HeadingIn(Coordinator(chat)[2].Messages).Should().BeFalse();
        (Coordinator(chat)[2].Instructions ?? "").Should().NotContain(AnnouncedActionChatClient.Heading,
            "the instruction is not left for the next turn");
    }

    // ── When there is no branch ───────────────────────────────────────────────────

    [Fact]
    public async Task A_turn_that_called_a_tool_is_not_branched_and_not_checked()
    {
        // A branch from the turn's start would leave out what ran: the model would read the user's message again, and
        // could run the action twice.
        var classifier = Classifier("CLAIMED");
        await using var host = Host(classifier);
        var chat = host.Chat!;
        chat.Respond(Call(chat, "count_open_orders", "resp_1"))
            .Respond(Text(chat, "Ho annullato l'ordine 1001.", "resp_2"));

        (await host.RunTurnAsync("annulla l'ordine 1001")).Should().BeNull();

        classifier.CallCount.Should().Be(0);
        Coordinator(chat).Should().HaveCount(2);
        host.Logs.Records.Should().Contain(r => r.Message.Contains("more than one request in the turn"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_a_session_manager_of_the_application_the_check_is_not_made_and_a_warning_says_so(bool check)
    {
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier, o => o.CheckAnnouncedActions = check,
            afterMentorAgent: s => s.AddScoped<IMentorSessionManager, ApplicationSessionManager>());
        var chat = host.Chat!;
        chat.Respond(Text(chat, "Procedo a contare gli ordini aperti…", "resp_1"));

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull();

        host.Logs.HasWarning("CheckAnnouncedActions has no effect with UseServiceManagedHistory and an IMentorSessionManager of the application")
            .Should().Be(check, "warnings seen:{0}{1}", Environment.NewLine, host.Logs.WarningText);
        Coordinator(chat).Should().ContainSingle();
        classifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task With_the_librarys_session_manager_no_warning_is_given_about_the_check()
    {
        await using var host = Host(Classifier("NONE"));
        var chat = host.Chat!;
        chat.Respond(Text(chat, "ok", "resp_1"));

        (await host.RunTurnAsync("ciao")).Should().BeNull();

        host.Logs.WarningText.Should().NotContain("CheckAnnouncedActions has no effect");
    }

    /// <summary>A session manager of the application's own: it delegates, and the library cannot move it to a branch.</summary>
    private sealed class ApplicationSessionManager : IMentorSessionManager
    {
        private readonly MentorSessionManager _inner = new();

        public event Action? OnSessionChanged
        {
            add => _inner.OnSessionChanged += value;
            remove => _inner.OnSessionChanged -= value;
        }

        public Task<AgentSession> GetOrCreateSessionAsync(AIAgent agent, CancellationToken ct = default) => _inner.GetOrCreateSessionAsync(agent, ct);
        public Task ResetAsync(AIAgent agent, CancellationToken ct = default) => _inner.ResetAsync(agent, ct);
        public Task<JsonElement?> SerializeCurrentSessionAsync(AIAgent agent) => _inner.SerializeCurrentSessionAsync(agent);
        public Task RestoreSessionAsync(AIAgent agent, JsonElement serialized, CancellationToken ct = default) => _inner.RestoreSessionAsync(agent, serialized, ct);
    }

    // ── The model, the count, the observers ─────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_second_request_is_served_by_the_model_of_the_turn_and_routing_decides_nothing_again(bool serviceHistory)
    {
        var decisions = 0;
        var strong = new FakeChatClient { ModelId = "strong-model" };
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier, o =>
        {
            o.UseServiceManagedHistory = serviceHistory;
            o.StrongChatClient = strong;
            o.RoutingStrategy = MentorRoutingStrategy.Custom;
            // The strong model for the turn's first decision, the cheap one for any other: a second decision would show.
            o.UseStrongModelAsync = (_, _) => Task.FromResult(Interlocked.Increment(ref decisions) == 1);
        });
        string? Id(string id) => serviceHistory ? id : null;
        strong.Respond(Text(strong, "Procedo a contare gli ordini aperti…", Id("resp_1")))
            .Respond(Call(strong, "count_open_orders", Id("resp_2")))
            .Respond(Text(strong, "Ci sono 3 ordini aperti.", Id("resp_3")));

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull(host.Logs.WarningText);

        decisions.Should().Be(1, "the routing of the turn is decided once, on the user's message");
        Coordinator(strong).Should().HaveCount(3, "the reply, the second request, and the one that reads the result");
        Coordinator(host.Chat!).Should().BeEmpty("the cheap model served nothing of the turn");
    }

    [Fact]
    public async Task The_turn_counts_once_and_the_observers_see_each_call_with_its_purpose()
    {
        using var telemetry = new Recorded();
        var observer = new CallObserver();
        var classifier = Classifier("ANNOUNCED");
        await using var host = Host(classifier, o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = telemetry.Source;
        }, s => s.AddMentorActionObserver(observer));
        var chat = host.Chat!;
        chat.Respond(Text(chat, "Procedo a contare gli ordini aperti…", "resp_1"))
            .Respond(Call(chat, "count_open_orders", "resp_2"))
            .Respond(Text(chat, "Ci sono 3 ordini aperti.", "resp_3"));

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull(host.Logs.WarningText);
        await host.Get<MentorActionDecisions>().FlushAsync();

        telemetry.Measured.Count(m => m.Instrument == "mentoragent.turns").Should().Be(1);
        observer.Calls.Select(c => c.Purpose).Should().Equal(
            MentorModelCallPurpose.Reply, MentorModelCallPurpose.AnnouncedActionCheck,
            MentorModelCallPurpose.Reply, MentorModelCallPurpose.Reply);
        observer.Calls.Select(c => c.TurnId).Distinct().Should().ContainSingle("all of them are the one turn's");
    }

    // ── The session manager ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_branch_keeps_the_state_bag_field_by_field_and_changes_only_the_conversation_id()
    {
        var agent = new ChatClientAgent(new FakeChatClient(), new ChatClientAgentOptions());
        var session = await agent.CreateSessionAsync("resp_9");
        session.StateBag.SetValue("Application", new Dictionary<string, object> { ["count"] = 3, ["name"] = "ada" });
        session.StateBag.SetValue("Other", new[] { "a", "b" });

        var branch = await MentorSessionManager.BranchAsync(agent, session, "resp_1");
        var fresh = await MentorSessionManager.BranchAsync(agent, session, conversationId: null);

        MentorSessionManager.ConversationIdOf(branch).Should().Be("resp_1");
        MentorSessionManager.ConversationIdOf(fresh).Should().BeNull("a branch from the first turn starts a conversation");
        MentorSessionManager.ConversationIdOf(session).Should().Be("resp_9", "the turn's session is not changed");

        async Task<JsonNode> WithoutId(AgentSession s)
        {
            var json = JsonNode.Parse((await agent.SerializeSessionAsync(s)).GetRawText())!.AsObject();
            json.Remove("conversationId");
            return json;
        }
        var before = await WithoutId(session);
        before["stateBag"]!.AsObject().Should().ContainKeys("Application", "Other");
        JsonNode.DeepEquals(await WithoutId(branch), before).Should().BeTrue("the state bag is the same, field by field");
        JsonNode.DeepEquals(await WithoutId(fresh), before).Should().BeTrue();
    }

    [Fact]
    public async Task A_branch_is_installed_only_while_the_turns_session_is_still_the_session()
    {
        var agent = new ChatClientAgent(new FakeChatClient(), new ChatClientAgentOptions());
        var manager = new MentorSessionManager();
        var changed = 0;
        manager.OnSessionChanged += () => changed++;
        var turnSession = await manager.GetOrCreateSessionAsync(agent);
        var branch = await MentorSessionManager.BranchAsync(agent, turnSession, "resp_1");

        (await manager.InstallBranchAsync(turnSession, branch)).Should().BeTrue();
        (await manager.GetOrCreateSessionAsync(agent)).Should().BeSameAs(branch, "a request after the turn goes on from the branch");
        changed.Should().Be(0, "the conversation goes on: a host that clears its transcript on a change must not");

        await manager.ResetAsync(agent);
        changed = 0;
        var late = await MentorSessionManager.BranchAsync(agent, branch, "resp_2");
        (await manager.InstallBranchAsync(branch, late)).Should().BeFalse("a reset during the second request wins");
        (await manager.GetOrCreateSessionAsync(agent)).Should().NotBeSameAs(late);
    }
}
