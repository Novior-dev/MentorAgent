using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// A scripted <see cref="IChatClient"/> — the piece of infrastructure that makes most of MentorAgent
/// testable at all.
///
/// <para>
/// Almost every behaviour worth pinning is a decision the library makes <em>before</em> the model is
/// called: which tools it sends, which instructions it assembles, whether it escalates to the strong
/// model, whether it streams or buffers. A real provider answers none of those questions and charges
/// for the privilege of being non-deterministic about it. This fake records the request instead, so a
/// test can assert on what MentorAgent decided rather than on what a model happened to say.
/// </para>
///
/// <para>
/// Responses are queued. When the queue runs dry the same trailing response is repeated, so a test
/// that only cares about the first turn does not have to script the rest.
/// </para>
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>> _script = new();
    private Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>? _last;

    /// <summary>Model id reported on every response — routing and per-model metrics key on this.</summary>
    internal string ModelId { get; set; } = "fake-model";

    /// <summary>
    /// The deployment this client is pointed at, as reported through <see cref="ChatClientMetadata"/>.
    /// Defaults to <see cref="ModelId"/>.
    ///
    /// <para>
    /// The two are separate because a real provider separates them: Azure OpenAI returns an
    /// <b>empty</b> model id on the chat response while the client obviously still knows its own
    /// deployment name. Setting <c>ModelId = ""</c> with a <c>DeploymentId</c> reproduces that, which
    /// is what BUG-030 needed and no test could express before.
    /// </para>
    /// </summary>
    internal string? DeploymentId { get; set; }

    /// <summary>Every call this client received, in order.</summary>
    internal List<RecordedCall> Calls { get; } = [];

    /// <summary>Number of calls, including the streaming ones.</summary>
    internal int CallCount => Calls.Count;

    /// <summary>The most recent call, or <see langword="null"/> when it was never invoked.</summary>
    internal RecordedCall? LastCall => Calls.Count == 0 ? null : Calls[^1];

    /// <summary>Thrown on the next call, once, then cleared. For exception-mapping tests.</summary>
    internal Exception? ThrowOnNextCall { get; set; }

    /// <summary>Awaited before each response — lets a test interleave a cancel or an approval.</summary>
    internal Func<Task>? BeforeRespond { get; set; }

    /// <summary>Delay between streamed chunks, so a test can cancel mid-stream deterministically.</summary>
    internal TimeSpan ChunkDelay { get; set; } = TimeSpan.Zero;

    // ── Scripting ────────────────────────────────────────────────────────────────

    /// <summary>Queues a plain text reply.</summary>
    internal FakeChatClient Say(string text) => Respond((_, _) => TextResponse(text));

    /// <summary>Queues the same text for the next <paramref name="times"/> calls.</summary>
    internal FakeChatClient SayRepeatedly(string text, int times)
    {
        for (var i = 0; i < times; i++) Say(text);
        return this;
    }

    /// <summary>Queues a reply that asks for a tool call.</summary>
    internal FakeChatClient CallTool(string name, object? arguments = null)
        => Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, ToArgs(arguments))]))
        {
            ModelId = ModelId,
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        });

    /// <summary>Queues a fully custom response, computed from the request.</summary>
    internal FakeChatClient Respond(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> factory)
    {
        _script.Enqueue(factory);
        return this;
    }

    /// <summary>Queues a reply with explicit token counts, for metering assertions.</summary>
    internal FakeChatClient SayWithUsage(string text, long input, long output)
        => Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = ModelId,
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        });

    // ── IChatClient ──────────────────────────────────────────────────────────────

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await PrepareAsync(messages, options, streaming: false, cancellationToken);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await PrepareAsync(messages, options, streaming: true, cancellationToken);

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (content is TextContent text)
                {
                    // Split into word-sized deltas: a single-chunk "stream" would hide every
                    // ordering bug this fake exists to expose.
                    foreach (var chunk in SplitForStreaming(text.Text))
                    {
                        if (ChunkDelay > TimeSpan.Zero)
                            await Task.Delay(ChunkDelay, cancellationToken);

                        yield return new ChatResponseUpdate(message.Role, chunk)
                        {
                            ModelId = response.ModelId,
                            ConversationId = response.ConversationId,
                        };
                    }
                }
                else
                {
                    yield return new ChatResponseUpdate(message.Role, [content])
                    {
                        ModelId = response.ModelId,
                        ConversationId = response.ConversationId,
                    };
                }
            }
        }

        if (response.Usage is { } usage)
            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(usage)],
                // A scripted Responses-API reply (Respond(... { ConversationId = "resp_1" })) reaches a
                // streaming caller the way the real client delivers it, on the updates.
                ConversationId = response.ConversationId,
            };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(defaultModelId: DeploymentId ?? ModelId)
            : null;

    public void Dispose() { }

    // ── Internals ────────────────────────────────────────────────────────────────

    private async Task<ChatResponse> PrepareAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool streaming,
        CancellationToken ct)
    {
        var list = messages.ToList();

        // Snapshot the request. ChatOptions is mutable and shared down the pipeline, so copying the
        // parts tests assert on is what keeps a later mutation from rewriting history.
        var tools = options?.Tools?.OfType<AIFunction>().ToList() ?? [];

        // Every tool, not only the callable functions. Provider-hosted tools are AITool but not
        // AIFunction, so a test that asserted on Tools alone could not see them at all — which is
        // how hosted-tool relevance filtering went untested despite being a billed capability.
        var allTools = options?.Tools?.ToList() ?? [];

        Calls.Add(new RecordedCall(
            Messages: list,
            Instructions: options?.Instructions,
            ToolNames: tools.Select(t => t.Name).ToList(),
            Tools: tools,
            AllTools: allTools,
            ConversationId: options?.ConversationId,
            ModelId: options?.ModelId,
            Streaming: streaming)
        {
            ToolMode = options?.ToolMode,
            Temperature = options?.Temperature,
            MaxOutputTokens = options?.MaxOutputTokens,
            AdditionalPropertyKeys = options?.AdditionalProperties?.Keys.ToList() ?? [],
        });

        if (ThrowOnNextCall is { } ex)
        {
            ThrowOnNextCall = null;
            throw ex;
        }

        if (BeforeRespond is { } hook) await hook();

        ct.ThrowIfCancellationRequested();

        var factory = _script.Count > 0 ? _script.Dequeue() : _last;
        _last = factory ?? _last;

        return factory is null
            ? TextResponse("(fake)")
            : factory(list, options);
    }

    private ChatResponse TextResponse(string text)
        => new(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = ModelId,
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        };

    private static IEnumerable<string> SplitForStreaming(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ') continue;
            yield return text[start..(i + 1)];
            start = i + 1;
        }

        if (start < text.Length) yield return text[start..];
    }

    private static IDictionary<string, object?> ToArgs(object? arguments)
    {
        if (arguments is null) return new Dictionary<string, object?>();
        if (arguments is IDictionary<string, object?> dict) return dict;

        return arguments.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(arguments));
    }

    /// <summary>One recorded request, with the parts assertions are written against.</summary>
    internal sealed record RecordedCall(
        IReadOnlyList<ChatMessage> Messages,
        string? Instructions,
        IReadOnlyList<string> ToolNames,
        IReadOnlyList<AIFunction> Tools,
        IReadOnlyList<AITool> AllTools,
        string? ConversationId,
        string? ModelId,
        bool Streaming)
    {
        /// <summary>What the request said about tool use — <c>RequireAny</c> on a forced retry (BUG-081).</summary>
        internal ChatToolMode? ToolMode { get; init; }

        /// <summary>The keys of <see cref="ChatOptions.AdditionalProperties"/> the provider client received.</summary>
        internal IReadOnlyList<string> AdditionalPropertyKeys { get; init; } = [];

        /// <summary>The temperature the request asked for — a classifier call is expected to be deterministic.</summary>
        internal float? Temperature { get; init; }

        /// <summary>The output cap the request asked for.</summary>
        internal int? MaxOutputTokens { get; init; }

        /// <summary>A tool as the model saw it — name, description and schema.</summary>
        internal AIFunction? Tool(string name) => Tools.FirstOrDefault(t => t.Name == name);

        /// <summary>The provider-hosted tools declared this turn, by CLR type name.</summary>
        internal IReadOnlyList<string> HostedToolNames =>
            [.. AllTools.Where(t => t is not AIFunction).Select(t => t.GetType().Name)];

        /// <summary>The last user message's text — the usual subject of a routing assertion.</summary>
        internal string LastUserText =>
            Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

        /// <summary>Everything the system was told this turn, concatenated.</summary>
        internal string SystemText => string.Join('\n', Messages
            .Where(m => m.Role == ChatRole.System)
            .Select(m => m.Text));
    }
}
