using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Sessions;

/// <summary>
/// BUG-110 in the core: a session snapshot the host cannot use must be refused before it replaces the
/// live conversation, and a restored conversation the service no longer knows must not poison every
/// turn after it.
///
/// <para>
/// Driven live on the published rc.13: a Responses snapshot restored on a Chat Completions host was
/// accepted, then every turn failed ("Service did not return a valid conversation id…") until a reset; a
/// Chat Completions snapshot restored on a Responses host was accepted with its history silently empty;
/// <c>{"mentorAgentSession":1.5,…}</c> answered 500 with a FormatException; a stale Responses pointer
/// failed every turn until a reset.
/// </para>
/// </summary>
public sealed class SessionSnapshotCompatibilityTests
{
    private const string Secret = "PRATICA-5151";

    private static async Task<JsonElement> SnapshotAfterOneTurnAsync(Action<Novior.MentorAgent.Options.MentorOptions>? configure = null,
                                                                   Action<Fakes.FakeChatClient>? script = null)
    {
        await using var host = MentorHostFixture.Create(o => { o.MemoryAutoCapture = false; configure?.Invoke(o); });
        if (script is not null) script(host.Chat!); else host.Chat!.Say("ok");
        (await host.RunTurnAsync($"il mio numero di pratica è {Secret}")).Should().BeNull();
        return (await host.Orchestrator.SerializeSessionAsync())!.Value;
    }

    /// <summary>A service-managed host: the scripted reply carries the conversation id the service would.</summary>
    private static void ServiceReply(Fakes.FakeChatClient chat)
        => chat.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { ConversationId = "resp_1" });

    // ── The marker and the payload ─────────────────────────────────────────────

    [Theory]
    [InlineData("1.5")]
    [InlineData("1e20")]
    [InlineData("2147483648")]
    public async Task A_non_integer_marker_is_refused_as_an_argument(string marker)
    {
        var good = await SnapshotAfterOneTurnAsync();
        var bad = JsonNode.Parse(good.GetRawText())!;
        bad["mentorAgentSession"] = JsonNode.Parse(marker);

        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.SayRepeatedly("ok", 2);
        await host.RunTurnAsync("ciao, prima domanda");

        var act = () => host.Orchestrator.RestoreSessionAsync(JsonSerializer.SerializeToElement(bad));

        await act.Should().ThrowAsync<ArgumentException>("the contract: anything that is not a snapshot is an argument error");
        await host.RunTurnAsync("seconda domanda");
        host.Chat.Calls[^1].Messages.Should().Contain(m => m.Text.Contains("prima domanda"), "the live conversation is kept");
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"x\"")]
    [InlineData("[]")]
    public async Task A_stamped_envelope_around_a_payload_that_is_not_a_session_is_refused(string payload)
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.Say("ok");
        await host.RunTurnAsync("ciao");

        var act = () => host.Orchestrator.RestoreSessionAsync(
            JsonDocument.Parse($$"""{"mentorAgentSession":1,"session":{{payload}}}""").RootElement);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── How the history is kept ────────────────────────────────────────────────

    [Fact]
    public async Task A_snapshot_says_how_its_history_is_kept()
    {
        var snapshot = await SnapshotAfterOneTurnAsync();

        snapshot.GetProperty("history").GetString().Should().Be("local");
        snapshot.GetProperty("historyKeys").EnumerateArray().Select(k => k.GetString())
            .Should().Equal("InMemoryChatHistoryProvider");
    }

    [Fact]
    public async Task A_service_managed_snapshot_is_refused_on_a_host_that_keeps_history_locally()
    {
        var snapshot = await SnapshotAfterOneTurnAsync(o => o.UseServiceManagedHistory = true, ServiceReply);

        await using var target = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        target.Chat!.SayRepeatedly("ok", 2);
        await target.RunTurnAsync("ciao");

        var act = () => target.Orchestrator.RestoreSessionAsync(snapshot);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("service");
        (await target.RunTurnAsync("e adesso?")).Should().BeNull("the live conversation still works");
        target.Chat.LastCall!.ConversationId.Should().BeNull();
    }

    [Fact]
    public async Task A_local_snapshot_is_refused_on_a_service_managed_host()
    {
        var snapshot = await SnapshotAfterOneTurnAsync();

        await using var target = MentorHostFixture.Create(o => { o.MemoryAutoCapture = false; o.UseServiceManagedHistory = true; });
        ServiceReply(target.Chat!);
        await target.RunTurnAsync("ciao");

        var act = () => target.Orchestrator.RestoreSessionAsync(snapshot);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("local");
    }

    [Fact]
    public async Task A_snapshot_saved_before_rc14_is_judged_by_its_conversation_id()
    {
        // rc.13 snapshots carry no history stamp; the service's pointer in the payload says which kind
        // of host saved it.
        var snapshot = JsonNode.Parse((await SnapshotAfterOneTurnAsync(o => o.UseServiceManagedHistory = true, ServiceReply)).GetRawText())!;
        snapshot.AsObject().Remove("history");
        snapshot.AsObject().Remove("historyKeys");

        await using var target = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        target.Chat!.Say("ok");
        await target.RunTurnAsync("ciao");

        var act = () => target.Orchestrator.RestoreSessionAsync(JsonSerializer.SerializeToElement(snapshot));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_snapshot_from_another_history_provider_is_refused()
    {
        // The provider keeps the transcript under its own key: restored on a host whose provider reads
        // another key, the history is silently empty.
        var snapshot = await SnapshotAfterOneTurnAsync(o => o.ChatHistoryProviderFactory = _ => new OtherProvider());

        await using var target = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        target.Chat!.Say("ok");
        await target.RunTurnAsync("ciao");

        var act = () => target.Orchestrator.RestoreSessionAsync(snapshot);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("OtherProvider");
    }

    [Fact]
    public async Task A_snapshot_restores_on_a_host_that_keeps_history_the_same_way()
    {
        // The control: the checks refuse the incompatible, not the compatible.
        var snapshot = await SnapshotAfterOneTurnAsync();

        await using var target = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        target.Chat!.SayRepeatedly("ok", 2);
        await target.Orchestrator.RestoreSessionAsync(snapshot);
        await target.RunTurnAsync("qual è il mio numero di pratica?");

        target.Chat.LastCall!.Messages.Should().Contain(m => m.Text.Contains(Secret));
    }

    // ── A restored conversation the service no longer knows ────────────────────

    private static async Task<MentorHostFixture> RestoredServiceHostAsync()
    {
        var snapshot = await SnapshotAfterOneTurnAsync(o => o.UseServiceManagedHistory = true, ServiceReply);
        var host = MentorHostFixture.Create(o => { o.MemoryAutoCapture = false; o.UseServiceManagedHistory = true; }, withRecorder: true);
        await host.Orchestrator.RestoreSessionAsync(snapshot);
        return host;
    }

    // The provider texts below are Azure OpenAI's own, captured on 2026-09-30 (Responses API, v1 and preview).

    [Theory]
    [InlineData(400, "HTTP 400 (invalid_request_error: previous_response_not_found) Parameter: previous_response_id Previous response with id 'resp_1' not found.")]
    [InlineData(404, "HTTP 404 (invalid_request_error) Conversation with id 'conv_1' not found.")]
    public async Task A_restored_conversation_the_service_no_longer_knows_is_replaced_after_one_failed_turn(int status, string providerMessage)
    {
        await using var host = await RestoredServiceHostAsync();
        host.Chat!.ThrowOnNextCall = new HttpRequestException(providerMessage, null, (HttpStatusCode)status);
        ServiceReply(host.Chat);

        await host.RunTurnAsync("prima domanda");
        await host.RunTurnAsync("seconda domanda");

        host.Chat.Calls[0].ConversationId.Should().Be("resp_1", "the first turn tried the restored conversation");
        host.Chat.Calls[^1].ConversationId.Should().BeNull("after the service said it does not know it, a new one starts");
        host.Logs.HasWarning("no longer known to the service").Should().BeTrue();
    }

    [Fact]
    public async Task A_transient_failure_keeps_the_restored_conversation()
    {
        // A 503 says nothing about the conversation: dropping the user's history for it would be the
        // worse failure.
        await using var host = await RestoredServiceHostAsync();
        host.Chat!.ThrowOnNextCall = new HttpRequestException("Service unavailable.", null, HttpStatusCode.ServiceUnavailable);
        ServiceReply(host.Chat);

        await host.RunTurnAsync("prima domanda");
        await host.RunTurnAsync("seconda domanda");

        host.Chat.Calls[^1].ConversationId.Should().Be("resp_1");
    }

    [Fact]
    public async Task A_successful_first_turn_keeps_the_restored_conversation()
    {
        await using var host = await RestoredServiceHostAsync();
        ServiceReply(host.Chat!);

        await host.RunTurnAsync("prima domanda");

        host.Chat!.Calls[^1].ConversationId.Should().Be("resp_1");
        host.Logs.HasWarning("no longer known to the service").Should().BeFalse();
    }

    [Theory]
    [InlineData(400, "HTTP 400 (content_filter): The response was filtered due to the prompt triggering Azure OpenAI's content management policy.")]
    [InlineData(400, "HTTP 400 (invalid_request_error: context_length_exceeded) This model's maximum context length is 128000 tokens.")]
    [InlineData(404, "HTTP 404 (invalid_request_error: DeploymentNotFound) The API deployment for this resource does not exist. If you created the deployment within the last 5 minutes, please wait a moment and try again.")]
    [InlineData(404, "Not Found")]
    public async Task A_failure_that_is_about_the_request_keeps_the_restored_conversation(int status, string providerMessage)
    {
        // Review of the rc.14 fix: a 400 is also a content filter, a context that outgrew the window, an image
        // the provider could not download, and a 404 is also a deployment that does not exist — on a perfectly
        // good conversation. Replacing it would lose it, and a client that saves on 200 would then overwrite
        // the snapshot it could have restored once the operator fixed the deployment.
        await using var host = await RestoredServiceHostAsync();
        host.Chat!.ThrowOnNextCall = new HttpRequestException(providerMessage, null, (HttpStatusCode)status);
        ServiceReply(host.Chat);

        await host.RunTurnAsync("prima domanda");
        await host.RunTurnAsync("seconda domanda");

        host.Chat.Calls[^1].ConversationId.Should().Be("resp_1");
        host.Logs.HasWarning("no longer known to the service").Should().BeFalse();
    }

    [Fact]
    public async Task A_turn_on_the_previous_session_neither_confirms_nor_discards_a_restore()
    {
        // A restore arrives over HTTP while a hub turn may still run on the session before it: that turn's
        // outcome says nothing about the restored conversation.
        var snapshot = await SnapshotAfterOneTurnAsync(o => o.UseServiceManagedHistory = true, ServiceReply);
        await using var host = MentorHostFixture.Create(o => { o.MemoryAutoCapture = false; o.UseServiceManagedHistory = true; });
        ServiceReply(host.Chat!);
        await host.RunTurnAsync("ciao");

        var sessions = (Novior.MentorAgent.Core.MentorSessionManager)host.Get<IMentorSessionManager>();
        var agent = (AIAgent)host.Orchestrator.GetType()
            .GetField("_coordinator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(host.Orchestrator)!;
        var previous = await sessions.GetOrCreateSessionAsync(agent);

        await host.Orchestrator.RestoreSessionAsync(snapshot);
        var restored = await sessions.GetOrCreateSessionAsync(agent);

        sessions.ConfirmRestored(previous);
        sessions.IsUnconfirmedRestore(restored).Should().BeTrue("the turn that succeeded ran on the previous session");
        (await sessions.ReplaceUnconfirmedRestoreAsync(agent, previous)).Should().BeFalse();
        (await sessions.GetOrCreateSessionAsync(agent)).Should().BeSameAs(restored);
    }

    /// <summary>A provider that keeps its transcript under a key of its own.</summary>
    private sealed class OtherProvider : ChatHistoryProvider
    {
        private readonly ProviderSessionState<List<ChatMessage>> _transcript =
            new(_ => [], nameof(OtherProvider), AIJsonUtilities.DefaultOptions);

        public override IReadOnlyList<string> StateKeys => [nameof(OtherProvider)];

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IEnumerable<ChatMessage>>([.. _transcript.GetOrInitializeState(context.Session)]);

        protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            var history = _transcript.GetOrInitializeState(context.Session);
            history.AddRange(context.RequestMessages);
            history.AddRange(context.ResponseMessages ?? []);
            _transcript.SaveState(context.Session, history);
            return ValueTask.CompletedTask;
        }
    }
}
