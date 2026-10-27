using System.Runtime.CompilerServices;
using A2A;
using Novior.MentorAgent.Options;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Builds <see cref="ChatClientAgent"/> wrappers for each remote A2A agent configured
/// in <see cref="MentorOptions.RemoteAgents"/>.
///
/// Remote agents participate in the Handoff workflow exactly like local
/// <c>[MentorAgent]</c> classes, using an <see cref="A2AChatClient"/> adapter
/// to bridge the A2A protocol to the <see cref="IChatClient"/> interface.
///
/// Each remote agent gets its own <see cref="HttpClient"/> instance, which is
/// kept alive for the lifetime of the scoped service and disposed together with
/// it. This prevents two problems:
/// 1. "using var httpClient" disposing the client while A2A agents still hold it.
/// 2. Header pollution — agents with different auth headers would contaminate
///    a shared client's DefaultRequestHeaders.
/// </summary>
internal sealed class A2AAgentBuilder : IDisposable
{
    private readonly MentorOptions _options;
    private readonly ILogger<A2AAgentBuilder> _logger;

    // One HttpClient per remote agent — tracked so they can be disposed
    // when DI disposes this scoped service at end-of-scope.
    private readonly List<HttpClient> _httpClients = [];

    // The cards, shared by every session; the clients built from them are not (they carry the
    // remote conversation's context id). Null only when a test builds this by hand.
    private readonly A2ACardCache? _cards;

    public A2AAgentBuilder(
        IOptions<MentorOptions> options,
        ILogger<A2AAgentBuilder> logger,
        A2ACardCache? cards = null,
        // Optional so the builder can still be constructed by hand; without the shared sentinel
        // there is nothing to be quiet about.
        CoordinatorAnnouncement? announcement = null)
    {
        _options      = options.Value;
        _logger       = logger;
        _cards        = cards;
        _announcement = announcement;
    }

    private readonly CoordinatorAnnouncement? _announcement;

    /// <summary>
    /// "Remote agent connected" describes the application's peer, not the visitor: after the first
    /// coordinator has announced it, later circuits keep the line at Debug (BUG-068, addendum).
    /// </summary>
    private ILogger BuildLog => QuietBuildLogger.Wrap(_logger, _announcement?.Quiet == true);

    /// <summary>
    /// Resolves agent cards for all configured remote agents and builds
    /// <see cref="ChatClientAgent"/> instances. Returns an empty list when no
    /// remote agents are configured. On Path B (no <c>ChatClient</c>) the orchestrator does not
    /// call it: a pre-built AIAgent gets no handoff tool, so no turn could reach a remote agent (BUG-179).
    /// </summary>
    public async Task<List<ChatClientAgent>> BuildAsync(CancellationToken ct = default)
    {
        var agents = new List<ChatClientAgent>();

        if (_options.RemoteAgents is not { Length: > 0 })
            return agents;

        foreach (var remote in _options.RemoteAgents)
        {
            try
            {
                // Each agent gets its own HttpClient so that auth headers
                // from one agent do not leak into another agent's requests.
                // The client is stored in _httpClients and disposed with this service.
                var httpClient = new HttpClient();
                _httpClients.Add(httpClient);

                // Inject custom auth / service headers for this specific agent
                if (remote.Headers is { Count: > 0 })
                    foreach (var (key, value) in remote.Headers)
                        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);

                // Resolve agent card from the remote endpoint.
                // A2ACardResolver expects the BASE URI — it automatically appends
                // /.well-known/agent-card.json. Do NOT include the path here.
                var baseUri = new Uri(remote.AgentCardUrl);

                // Fetched once per process rather than once per visitor. The key carries the headers
                // as well as the URL: a per-tenant token must never be served another tenant's card.
                var cacheKey = $"{remote.Name}|{remote.AgentCardUrl}|" +
                               string.Join(",", (remote.Headers ?? new Dictionary<string, string>())
                                   .OrderBy(h => h.Key, StringComparer.Ordinal)
                                   .Select(h => $"{h.Key}={h.Value}"));

                async Task<A2A.AgentCard> FetchAsync(CancellationToken token)
                {
                    var fetched = await new A2ACardResolver(baseUri, httpClient).GetAgentCardAsync(token);

                    // Normalised here, inside the fetch, so the cached card is already absolute:
                    // a shared card must never be mutated by the session that happens to read it.
                    if (fetched.SupportedInterfaces is not null)
                        foreach (var iface in fetched.SupportedInterfaces)
                            if (!string.IsNullOrWhiteSpace(iface.Url)
                                && !Uri.IsWellFormedUriString(iface.Url, UriKind.Absolute))
                                iface.Url = new Uri(baseUri, iface.Url).AbsoluteUri;

                    return fetched;
                }

                var card = _cards is not null
                    ? await _cards.GetAsync(cacheKey, remote.Name, remote.AgentCardUrl, FetchAsync, ct)
                    : await FetchAsync(ct);

                // The relative-URL normalisation the factory needs (A2AClientFactory.Create calls
                // new Uri(iface.Url) directly, and a remote that did not set A2AServerUrl publishes
                // a bare path) now happens inside FetchAsync, before the card is cached.

                // Create A2A client using the best available protocol binding.
                // httpClient must stay alive for the lifetime of a2aClient — see Dispose().
                var a2aClient = A2AClientFactory.Create(card, httpClient);

                // Wrap as IChatClient so Agent Framework can use it in Handoff
                var chatClient = A2AChatClient.ForCard(card, a2aClient, remote.Name, _logger);

                var agent = new ChatClientAgent(
                    chatClient,
                    new ChatClientAgentOptions
                    {
                        Name        = remote.Name,
                        ChatOptions = new ChatOptions
                        {
                            Instructions = $"You are {remote.Name}. {remote.Description}",
                            ToolMode     = ChatToolMode.Auto,
                        }
                    });

                agents.Add(agent);

                BuildLog.LogInformation(
                    "[MentorAgent:A2A] Remote agent '{Name}' connected from {Url}.",
                    remote.Name, remote.AgentCardUrl);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A build the turn stopped is not an agent that is down (BUG-211): it ends here, and the next message
                // builds the coordinator again, with the agent. Swallowed, the Stop left the session without it.
                //
                // The cache already said this once, as a message, with the agent and the URL in it
                // (and the stack trace at Debug). Repeating it per session was one full stack trace
                // per visitor for an agent that is simply down.
                if (_cards is not null)
                    _logger.LogDebug(
                        "[MentorAgent:A2A] Remote agent '{Name}' unavailable for this session.", remote.Name);
                else
                    _logger.LogWarning(ex,
                        "[MentorAgent:A2A] Failed to connect to remote agent '{Name}' at {Url}.",
                        remote.Name, remote.AgentCardUrl);
            }
        }

        return agents;
    }

    public void Dispose()
    {
        foreach (var client in _httpClients)
            client.Dispose();
        _httpClients.Clear();
    }
}

// ── A2AChatClient ──────────────────────────────────────────────────────────────

/// <summary>
/// Adapts <see cref="IA2AClient"/> to <see cref="IChatClient"/>, enabling A2A
/// agents to participate in the Agent Framework Handoff workflow without changes
/// to <see cref="HandoffBuilder"/>.
/// </summary>
internal sealed class A2AChatClient : IChatClient
{
    private readonly IA2AClient _a2aClient;
    private readonly bool       _streaming;
    private readonly ILogger    _logger;
    private string?             _contextId;   // persisted across conversation turns

    public ChatClientMetadata Metadata { get; }

    public A2AChatClient(IA2AClient a2aClient, string name, bool streaming = true, ILogger? logger = null)
    {
        _a2aClient = a2aClient;
        _streaming = streaming;
        _logger    = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Metadata   = new ChatClientMetadata(nameof(A2AChatClient), defaultModelId: name);
    }

    /// <summary>
    /// A client for the peer <paramref name="card"/> describes, streamed only when the card declares streaming
    /// (BUG-197).
    /// </summary>
    internal static A2AChatClient ForCard(AgentCard card, IA2AClient a2aClient, string name, ILogger logger)
        => new(a2aClient, name, card.Capabilities?.Streaming == true, logger);

    /// <summary>
    /// What the peer is sent: the last user message, without the marker <c>route_to_specialist</c>
    /// addressed to the <b>local</b> router. Left in, it crossed the wire and the peer's coordinator
    /// read it as its own order — a peer with a remote agent of the same name delegated onward, and
    /// two hosts that peer each other never stopped (BUG-080, found live on the published rc.8).
    /// </summary>
    private static string OutboundText(IEnumerable<ChatMessage> messages)
        => SpecialistMarker.Strip(
            messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty);

    /// <summary>
    /// The request as it goes on the wire. Built by hand rather than through the SDK's text-only
    /// extension because of the metadata: the name <b>this</b> host has for the peer is in the request
    /// text ("quanti prodotti ha ShopFlowRemote?") and the peer has no way to know it means itself —
    /// on the published rc.9 it answered "I have no access to ShopFlowRemote" 3 times in 6 (BUG-081).
    /// As metadata it is data, not text: a peer that does not know the key ignores it.
    /// </summary>
    private SendMessageRequest OutboundRequest(IEnumerable<ChatMessage> messages) => new()
    {
        Message = new A2A.Message
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Role      = A2A.Role.User,
            Parts     = [Part.FromText(OutboundText(messages))],
            ContextId = _contextId,
            Metadata  = Metadata.DefaultModelId is { Length: > 0 } name
                ? new() { [MentorTurnOrigin.AddressedAsMetadataKey] = System.Text.Json.JsonSerializer.SerializeToElement(name) }
                : null,
        },
    };

    // ── Stop (BUG-210) ─────────────────────────────────────────────────────────

    /// <summary>How long a stopped turn waits, at most, for the peer to take its cancel.</summary>
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How often a task the peer is still working on is read again: from a quarter of a second up to one.</summary>
    private static readonly TimeSpan FirstPoll = TimeSpan.FromMilliseconds(250), LongestPoll = TimeSpan.FromSeconds(1);

    /// <summary>
    /// BUG-210: the turn that delegated the task ended before its answer — a Stop, a client that went away, a failure —
    /// and nobody waits for it any more. The peer is told to cancel it rather than finish it: up to 1.1.0 it was told
    /// nothing, and ran it to the end, actions that ask for no confirmation included. Awaited, because the HTTP client
    /// that carries the cancel is disposed with the session's scope, but never longer than <see cref="CancelTimeout"/>;
    /// a peer that does not take it costs a Debug line, not the turn's error.
    /// </summary>
    private async Task CancelRemoteTaskAsync(string taskId)
    {
        using var timeout = new CancellationTokenSource(CancelTimeout);
        try
        {
            await _a2aClient.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, timeout.Token);
            _logger.LogInformation(
                "[MentorAgent:A2A] Task '{TaskId}' of the remote agent '{Name}' cancelled: the turn that delegated it " +
                "ended before its answer.", taskId, Metadata.DefaultModelId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "[MentorAgent:A2A] Task '{TaskId}' of the remote agent '{Name}' could not be cancelled.",
                taskId, Metadata.DefaultModelId);
        }
    }

    /// <summary>
    /// The token a request to the peer runs under, in place of the turn's. A Stop cancels it at once when the task's id
    /// is known, and the task is then cancelled by its id; when it is not known yet — a Stop before the peer's first
    /// event, or during a request that returns the task — the request stays open <see cref="CancelTimeout"/> longer,
    /// for the id to arrive. Dropped at once, it left the task running with nothing to cancel it by.
    /// </summary>
    private sealed class RemoteTaskWatch : IDisposable
    {
        private readonly CancellationTokenSource _request = new();
        private readonly CancellationTokenRegistration _onStop;
        private string? _id;

        internal RemoteTaskWatch(CancellationToken turn)
            => _onStop = turn.Register(() =>
            {
                if (Id is null) _request.CancelAfter(CancelTimeout);
                else _request.Cancel();
            });

        internal CancellationToken Request => _request.Token;

        /// <summary>The task's id, once something names it; the first one stays.</summary>
        internal string? Id
        {
            get => Volatile.Read(ref _id);
            set => Interlocked.CompareExchange(ref _id, value, null);
        }

        public void Dispose()
        {
            _onStop.Dispose();
            _request.Dispose();
        }
    }

    /// <summary>
    /// The peer's events, with a read that fails once the turn is stopped reported as the cancellation it is. The
    /// connection is torn down under a stopped read, which can then surface as an <see cref="IOException"/>, and the
    /// workflow above would read the Stop as a peer that failed.
    /// </summary>
    private static async IAsyncEnumerable<T> AsCancelled<T>(
        IAsyncEnumerable<T> events, [EnumeratorCancellation] CancellationToken request, CancellationToken turn)
    {
        await using var e = events.GetAsyncEnumerator(request);
        while (true)
        {
            bool more;
            try
            {
                more = await e.MoveNextAsync();
            }
            catch (Exception ex) when (turn.IsCancellationRequested)
            {
                throw new OperationCanceledException("The delegated request was cancelled.", ex, turn);
            }
            if (!more) yield break;
            yield return e.Current;
        }
    }

    /// <summary>A state the peer is still working in: the task can be read again, and cancelled.</summary>
    private static bool InProgress(TaskState? state) => state is TaskState.Submitted or TaskState.Working;

    /// <summary>
    /// Reads the task until the peer is done with it. Cancelled by the turn, or failing, it cancels the task first.
    /// </summary>
    private async Task<AgentTask> UntilDoneAsync(AgentTask task, CancellationToken ct)
    {
        var done = false;
        try
        {
            for (var delay = FirstPoll; InProgress(task.Status?.State);
                 delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LongestPoll.Ticks)))
            {
                await Task.Delay(delay, ct);
                try
                {
                    task = await _a2aClient.GetTaskAsync(new GetTaskRequest { Id = task.Id }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The delegated request was cancelled.", ex, ct);
                }
            }
            done = true;
            return task;
        }
        finally
        {
            if (!done)
                await CancelRemoteTaskAsync(task.Id);
        }
    }

    // ── Non-streaming ──────────────────────────────────────────────────────────

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Told the task at once rather than waiting for the reply (BUG-210): a caller that waits holds no id, and a
        // Stop could only drop the request while the peer ran the task to its end. A peer that answers at once with
        // a message, or with the finished task, is read as before.
        var request = OutboundRequest(messages);
        request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
        SendMessageResponse response;
        using (var watch = new RemoteTaskWatch(cancellationToken))
        {
            try
            {
                response = await _a2aClient.SendMessageAsync(request, watch.Request);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The delegated request was cancelled.", ex, cancellationToken);
            }
        }
        // A turn stopped while the task was being started reads it no further: UntilDoneAsync cancels it at once.
        if (response.PayloadCase == SendMessageResponseCase.Task && response.Task is { } started
            && InProgress(started.Status?.State))
            response = new SendMessageResponse { Task = await UntilDoneAsync(started, cancellationToken) };
        cancellationToken.ThrowIfCancellationRequested();

        // Keep the context ID alive for follow-up turns
        _contextId = response.PayloadCase switch
        {
            SendMessageResponseCase.Message => response.Message?.ContextId ?? _contextId,
            SendMessageResponseCase.Task    => response.Task?.ContextId    ?? _contextId,
            _                              => _contextId,
        };

        return new ChatResponse([ExtractChatMessage(response)]);
    }

    // ── Streaming ──────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // BUG-197: a peer that does not declare streaming is asked once, without a stream. From A2A
        // 1.0.0-preview3 its server refuses a streamed request, and the SDK's client reads the refusal as
        // a stream with nothing in it: the delegated turn came back empty, with no error.
        if (!_streaming)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate
            {
                Role     = ChatRole.Assistant,
                Contents = [.. response.Messages.SelectMany(m => m.Contents)],
            };
            yield break;
        }

        var answered = false;
        // The task the stream belongs to, from its first event, and whether the stream got as far as its end: a turn
        // that leaves it before, a Stop above all, cancels the task (BUG-210).
        using var watch = new RemoteTaskWatch(cancellationToken);
        var ended = false;
        try
        {
            await foreach (var evt in AsCancelled(_a2aClient.SendStreamingMessageAsync(
                OutboundRequest(messages), watch.Request), watch.Request, cancellationToken))
            {
                watch.Id = evt.Task?.Id ?? evt.StatusUpdate?.TaskId ?? evt.ArtifactUpdate?.TaskId;

                // Stopped before this event named the task: the stream was kept open for it (RemoteTaskWatch).
                if (cancellationToken.IsCancellationRequested) break;

                // A peer answers in one of two shapes, and both are in the specification: a bare
                // Message, or a Task whose answer arrives on its final status. Only the first was read
                // here — and a MentorAgent server answers with the second, so MentorAgent's own client
                // heard nothing from MentorAgent's own server (BUG-078).
                A2A.Message? answer = null;

                switch (evt.PayloadCase)
                {
                    case StreamResponseCase.Message when evt.Message is { } msg:
                        _contextId = msg.ContextId ?? _contextId;
                        answer = msg;
                        break;

                    case StreamResponseCase.Task when evt.Task is { } task:
                        _contextId = task.ContextId ?? _contextId;
                        if (IsFinal(task.Status?.State)) answer = task.Status?.Message;
                        break;

                    case StreamResponseCase.StatusUpdate when evt.StatusUpdate is { } update:
                        _contextId = update.ContextId ?? _contextId;
                        // A Working status carries a progress note ("Processing…"), not the answer.
                        if (IsFinal(update.Status?.State)) answer = update.Status?.Message;
                        break;
                }

                if (answer is null) continue;

                answered = ended = true;
                var chatMsg = answer.ToChatMessage();
                yield return new ChatResponseUpdate
                {
                    Role     = ChatRole.Assistant,
                    Contents = [.. chatMsg.Contents],
                };
            }
            ended |= !cancellationToken.IsCancellationRequested;
        }
        finally
        {
            if (!ended && watch.Id is { } taskId)
                await CancelRemoteTaskAsync(taskId);
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (!answered)
            _logger.LogWarning(
                "[MentorAgent:A2A] The remote agent '{Name}' ended its stream without an answer, so the delegated " +
                "request comes back empty. A peer that refuses streaming answers so too.",
                Metadata.DefaultModelId);
    }

    /// <summary>A state after which the task says nothing more — its message is the outcome.</summary>
    private static bool IsFinal(TaskState? state) => state is
        TaskState.Completed or TaskState.Failed or TaskState.Rejected or TaskState.Canceled
        or TaskState.InputRequired or TaskState.AuthRequired;

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ChatMessage ExtractChatMessage(SendMessageResponse response)
    {
        if (response.PayloadCase == SendMessageResponseCase.Message && response.Message is { } msg)
            return msg.ToChatMessage();

        if (response.PayloadCase == SendMessageResponseCase.Task && response.Task is { } task)
        {
            // The outcome of a finished task is on its status; history is the fallback for a peer
            // that records its reply there instead.
            if (IsFinal(task.Status?.State) && task.Status?.Message is { } final
                && final.Parts.Any(p => !string.IsNullOrWhiteSpace(p.Text)))
                return final.ToChatMessage();

            var lastAgent = task.History?.LastOrDefault(m => m.Role == A2A.Role.Agent);
            if (lastAgent is not null)
                return lastAgent.ToChatMessage();
        }

        return new ChatMessage(ChatRole.Assistant, "No response from remote agent.");
    }

    public TService? GetService<TService>(object? key = null) where TService : class => null;

    public object? GetService(Type serviceType, object? key = null) => null;

    public void Dispose() => (_a2aClient as IDisposable)?.Dispose();
}
