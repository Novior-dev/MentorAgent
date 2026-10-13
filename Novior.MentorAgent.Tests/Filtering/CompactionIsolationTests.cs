#pragma warning disable MAAI001 // Compaction API is experimental
using FluentAssertions;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Filtering;

/// <summary>
/// BUG-104 at the Agent Framework level: <see cref="CompactionIsolationChatClient"/> against the framework's
/// own <c>CompactionProvider</c>, with nothing of MentorAgent's around it — and a canary for the day the
/// framework fixes microsoft/agent-framework#8441 itself, when the client can go.
/// </summary>
public sealed class CompactionIsolationTests
{
    private const string Question = "Che ore sono? Pratica OMEGA-9090";

    private static async Task<List<ChatMessage>> StoredAfterAToolTurnAsync(bool isolate, bool streaming)
    {
        var fake = new FakeChatClient();
        fake.CallTool("get_time").Say("Sono le 12.");

        IChatClient compacted = new ChatClientBuilder(fake)
            .UseAIContextProviders(new CompactionProvider(
                new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(100_000))))
            .Build();
        if (isolate) compacted = new CompactionIsolationChatClient(compacted);

        var history = new RecordingHistoryProvider();
        var agent = compacted.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "12:00", "get_time")] },
            ChatHistoryProvider = history,
        });
        var session = await agent.CreateSessionAsync();

        if (streaming)
            await foreach (var _ in agent.RunStreamingAsync(Question, session)) { }
        else
            await agent.RunAsync(Question, session);

        return history.Stored;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_the_copy_a_tool_turn_is_stored_whole(bool streaming)
    {
        var stored = await StoredAfterAToolTurnAsync(isolate: true, streaming);

        stored.Count(m => m.Role == ChatRole.User && m.Text == Question).Should().Be(1);
        stored.Should().NotContain(m => m.AdditionalProperties != null
                                         && m.AdditionalProperties.Values.Any(v => v != null && v.ToString()!.Contains("Compaction")),
            "the compaction provider marks its own copies, never what is stored");
    }

    [Fact]
    public void The_callers_message_and_its_properties_are_not_touched()
    {
        var properties = new AdditionalPropertiesDictionary { ["host.marker"] = "kept" };
        var message = new ChatMessage(ChatRole.User, "ciao") { AdditionalProperties = properties };

        var copies = CompactionIsolationChatClient.Copy([message]);
        copies[0].AdditionalProperties!["_attribution"] = "written by a provider";

        message.AdditionalProperties.Should().BeSameAs(properties);
        message.AdditionalProperties.Should().ContainKey("host.marker").And.NotContainKey("_attribution");
        copies[0].Contents.Should().BeSameAs(message.Contents, "contents are shared: no strategy mutates them");
    }

    [Fact]
    public async Task Without_the_copy_the_framework_still_drops_the_question()
    {
        // The canary. It passes while the framework has the defect; the day it turns red, the Agent
        // Framework version MentorAgent references clones before indexing (microsoft/agent-framework#8461)
        // and CompactionIsolationChatClient can be removed.
        var stored = await StoredAfterAToolTurnAsync(isolate: false, streaming: true);

        stored.Count(m => m.Role == ChatRole.User && m.Text == Question).Should().Be(0,
            "AF fixed microsoft/agent-framework#8441: remove CompactionIsolationChatClient");
    }

    /// <summary>A history provider with the base class's default filters, like the samples' and the README's.</summary>
    private sealed class RecordingHistoryProvider : ChatHistoryProvider
    {
        internal List<ChatMessage> Stored { get; } = [];

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IEnumerable<ChatMessage>>([.. Stored]);

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            Stored.AddRange(context.RequestMessages);
            Stored.AddRange(context.ResponseMessages ?? []);
            return ValueTask.CompletedTask;
        }
    }
}
