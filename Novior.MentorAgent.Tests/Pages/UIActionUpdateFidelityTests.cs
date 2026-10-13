using System.Runtime.CompilerServices;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// What survives when <see cref="UIActionsMiddleware"/> has to rebuild a streaming update.
///
/// <para>
/// BUG-052 made the middleware forward updates as they arrive and withhold only calls to its own
/// registered actions. When an update carries <b>both</b> — a UI-action call and something else —
/// the original cannot be passed through unchanged, so a new one is built from the surviving
/// content. Everything not copied onto that new object is silently lost.
/// </para>
///
/// <para>
/// <c>FakeChatClient</c> emits one content per update, so that branch is unreachable through the
/// usual fixture and the whole suite stayed green with properties being dropped. Real providers
/// batch content, so this drives a client that produces the mixed update directly.
/// </para>
/// </summary>
public sealed class UIActionUpdateFidelityTests
{
    /// <summary>Emits exactly one update carrying text, a UI-action call, and full metadata.</summary>
    private sealed class MixedUpdateClient : IChatClient
    {
        internal const string Ours = "highlight_row";

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new TextContent("Evidenzio la riga."),
                    new FunctionCallContent("call-1", Ours, new Dictionary<string, object?> { ["parameter"] = 5 }),
                ],
                ModelId = "gpt-test",
                ResponseId = "resp-1",
                MessageId = "msg-1",
                ConversationId = "conv-1",
                AuthorName = "assistant-1",
                FinishReason = ChatFinishReason.Stop,
                AdditionalProperties = new AdditionalPropertiesDictionary { ["provider_trace"] = "abc123" },
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static async Task<List<ChatResponseUpdate>> StreamAsync()
    {
        var page = new MentorPageContext();
        page.RegisterUIAction(MixedUpdateClient.Ours, "Evidenzia una riga", _ => { });

        var middleware = new UIActionsMiddleware(
            new MixedUpdateClient(), page, new RecordingStateService(),
            NullLogger<UIActionsMiddleware>.Instance);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in middleware.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "evidenzia la riga 5")], null, deadline.Token))
            updates.Add(u);

        return updates;
    }

    [Fact]
    public async Task The_rebuilt_update_keeps_the_metadata_the_original_carried()
    {
        // Each of these is a real consumer's input: FinishReason is why the turn ended,
        // ConversationId is what service-managed history is keyed on, AdditionalProperties and
        // AuthorName are the provider's own annotations. Dropping any of them is invisible until
        // something downstream needs it.
        var updates = await StreamAsync();

        var carried = updates.FirstOrDefault(u => u.Contents.OfType<TextContent>().Any());
        carried.Should().NotBeNull("the text half of the mixed update has to reach the caller");

        carried!.ModelId.Should().Be("gpt-test");
        carried.ResponseId.Should().Be("resp-1");
        carried.MessageId.Should().Be("msg-1");
        carried.ConversationId.Should().Be("conv-1");
        carried.AuthorName.Should().Be("assistant-1");
        carried.FinishReason.Should().Be(ChatFinishReason.Stop);
        carried.AdditionalProperties.Should().NotBeNull();
        carried.AdditionalProperties!["provider_trace"].Should().Be("abc123");
    }

    [Fact]
    public async Task The_UI_action_call_is_still_withheld_from_the_rebuilt_update()
    {
        // The guarantee the rebuild exists to keep. If preserving metadata were achieved by passing
        // the original update through untouched, this is the test that would catch it.
        var updates = await StreamAsync();

        updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>()
            .Select(f => f.Name)
            .Should().NotContain(MixedUpdateClient.Ours,
                "the outer pipeline has never heard of this tool and could not resolve a call to it");
    }
}
