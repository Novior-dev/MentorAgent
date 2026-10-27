using System.Collections.Concurrent;
using System.Security.Claims;
using A2A;


namespace Novior.MentorAgent.Core;

/// <summary>
/// A2A protocol handler that bridges incoming A2A tasks to <see cref="MentorOrchestrator"/>.
///
/// Registered via:
/// <code>
/// services.AddA2AAgent&lt;MentorAgentA2AHandler&gt;(agentCard);
/// </code>
/// The SDK registers the handler as a singleton. Each task runs in a DI scope of its own, so that the scoped
/// <see cref="MentorOrchestrator"/> is properly resolved.
/// </summary>
internal sealed class MentorAgentA2AHandler : IAgentHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MentorAgentA2AHandler> _logger;
    private readonly IMentorHttpUser? _httpUser;

    /// <summary>The turns running here, by task id: what a <c>tasks/cancel</c> stops (BUG-199).</summary>
    private readonly ConcurrentDictionary<string, RunningTask> _running = new(StringComparer.Ordinal);

    /// <summary>A task's turn, and whether its caller cancelled it.</summary>
    private sealed class RunningTask(MentorOrchestrator orchestrator)
    {
        public MentorOrchestrator Orchestrator => orchestrator;
        public volatile bool CancelledByCaller;
    }

    public MentorAgentA2AHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<MentorAgentA2AHandler> logger,
        IMentorHttpUser? httpUser = null)
    {
        _scopeFactory = scopeFactory;
        _logger       = logger;
        _httpUser     = httpUser;
    }

    // ── IAgentHandler ──────────────────────────────────────────────────────────

    public async Task ExecuteAsync(
        RequestContext context,
        AgentEventQueue queue,
        CancellationToken cancellationToken)
    {
        // Who is calling, read first: once the task is submitted the SDK may answer the caller (ReturnImmediately), the
        // request ends, and the turn runs on without it. Read after the submit, the request was closing or gone, and an
        // authenticated peer became anonymous (BUG-226).
        var caller = _httpUser?.User;

        // Async scope: MentorOrchestrator is IAsyncDisposable only, and disposing a plain scope
        // that resolved it throws "type only implements IAsyncDisposable" — after the turn had run
        // and been paid for, turning every answer into "An internal error occurred" (BUG-078).
        await using var scope = _scopeFactory.CreateAsyncScope();

        // The turn is known by the task's id before the caller is told the id, so a tasks/cancel finds the turn it names.
        var running = new RunningTask(scope.ServiceProvider.GetRequiredService<MentorOrchestrator>());
        _running[context.TaskId] = running;
        try
        {
            await RunAsync(context, queue, scope.ServiceProvider, running, caller, cancellationToken);
        }
        finally
        {
            _running.TryRemove(KeyValuePair.Create(context.TaskId, running));
        }
    }

    private async Task RunAsync(
        RequestContext context,
        AgentEventQueue queue,
        IServiceProvider sp,
        RunningTask running,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken)
    {
        var userText = context.UserText
            ?? context.Message?.Parts.Select(p => p.Text).FirstOrDefault(t => t is not null)
            ?? string.Empty;

        // Ids only, never the text: the context id is what ties a caller's turns together, and it is
        // the one thing an operator needs to tell two callers apart in this log.
        _logger.LogDebug("[MentorAgent:A2A] Task '{TaskId}' received (context '{ContextId}').",
            context.TaskId, context.ContextId);

        // Submit first: the task has to exist before its status can change. Without this the SDK
        // received a Working update for a task it had never been given and answered the client
        // "Agent handler did not produce any response events" — on every request (BUG-078).
        var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
        await updater.SubmitAsync(cancellationToken: cancellationToken);
        await updater.StartWorkAsync(
            new Message { Role = A2A.Role.Agent, Parts = [Part.FromText("Processing...")] },
            cancellationToken: cancellationToken);

        var orchestrator = running.Orchestrator;
        var stateService = sp.GetRequiredService<IMentorStateService>();

        // The scope is ours, not the request's: say who is calling, as the request said when the task started, before
        // anything reads the user (BUG-131, BUG-226). Blazor's provider, asked here, threw — a peer lost its roles.
        sp.GetRequiredService<MentorTransportUser>().User = caller;
        sp.GetRequiredService<MentorTurnContext>().Surface = MentorActionSurface.A2A;   // story 180

        // The caller is a program: nobody will answer "would you also like…?" (see MentorTurnOrigin).
        var origin = sp.GetRequiredService<MentorTurnOrigin>();
        origin.FromAnotherAgent = true;

        // A MentorAgent caller says what IT calls us, because that name is in the request text and
        // means nothing here otherwise (BUG-081). It comes from another machine and goes into a
        // prompt: SanitizeAlias lets a name through and nothing else.
        origin.AddressedAs = MentorTurnOrigin.SanitizeAlias(ReadAddressedAs(context.Message));

        var responseBuilder = new System.Text.StringBuilder();
        void OnChunk(string chunk) => responseBuilder.Append(chunk);
        stateService.OnStreamingChunk += OnChunk;

        try
        {
            await orchestrator.SendMessageAsync(userText, cancellationToken);

            // A reply written without reaching for a single tool is, to a caller that is a program,
            // indistinguishable from a looked-up one — and on the published rc.9 it was usually
            // invented ("quanti clienti Premium?" → 18, 3, 4, 11, 16… for 2; BUG-081). The signal is
            // structural, not textual: no tool that returns data ran. The same request is put once more,
            // with the turn's context saying why; only the second reply is returned. One extra model
            // turn, paid only by such A2A requests; a request that needs no tool (a policy answered
            // from the documents) simply gets the same answer twice.
            //
            // BUG-106: a navigation, a memory write or a skill returns no record, and no longer counts — on
            // the published rc.13 a load_skill call carried "the catalogue does not report the price"
            // (false) past the retry. The judge below stays the net after the second attempt.
            if (orchestrator.GroundingActivityInLastTurn == 0 && responseBuilder.Length > 0 && !running.CancelledByCaller)
            {
                _logger.LogDebug(
                    "[MentorAgent:A2A] Task '{TaskId}': the reply was written without calling any tool that " +
                    "returns data — discarded, and the request put once more for a grounded answer.", context.TaskId);

                origin.GroundingRetry = true;
                orchestrator.RequireToolOnNextTurn = true;   // the second attempt cannot be tool-less by choice
                responseBuilder.Clear();
                await orchestrator.SendMessageAsync(userText, cancellationToken);

                // The third outcome: told exactly what was wrong, the model called nothing again.
                // A tool-less reply is not wrong in itself ("this application cannot provide it", a
                // policy from the documents), so a model is asked — not a pattern — whether this one
                // states a value of the application's records. If it does, nobody looked it up.
                if (orchestrator.GroundingActivityInLastTurn == 0 && responseBuilder.Length > 0 && !running.CancelledByCaller
                    && await orchestrator.ReplyStatesApplicationDataAsync(
                           userText, responseBuilder.ToString(), cancellationToken))
                {
                    _logger.LogInformation(
                        "[MentorAgent:A2A] Task '{TaskId}': two attempts ended without a tool and the reply " +
                        "states application data — withheld, the caller is told it is not grounded.",
                        context.TaskId);

                    responseBuilder.Clear();
                    responseBuilder.Append(MentorTurnOrigin.UngroundedReply);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MentorAgent:A2A] Error executing task '{TaskId}'.", context.TaskId);

            // Do NOT unsubscribe here — the finally block always runs and handles cleanup.
            await updater.FailAsync(
                new Message
                {
                    Role  = A2A.Role.Agent,
                    Parts = [Part.FromText($"Error: {ex.Message}")]
                },
                cancellationToken: cancellationToken);
            return;
        }
        finally
        {
            stateService.OnStreamingChunk -= OnChunk;
        }

        // The caller cancelled the task, and CancelAsync ended it Canceled: a stopped turn reports nothing more — not the
        // words it wrote before the cancel, not "no reply" as a failure — and asks nothing more: no second attempt for a
        // grounded answer, no judge (BUG-199).
        if (running.CancelledByCaller)
        {
            _logger.LogDebug("[MentorAgent:A2A] Task '{TaskId}': the caller cancelled it — the turn stopped, nothing more is reported.",
                context.TaskId);
            return;
        }

        var responseText = responseBuilder.ToString();

        // The orchestrator reports its own failures to the widget — and nobody is looking at one
        // here. Found live (BUG-083): the turn died in "Error in Coordinator", nothing was streamed,
        // and the task was closed COMPLETED with an empty message; the caller's workflow read that
        // as a peer with nothing to say and told its user the request had not been delegated. A
        // turn that produced no words did not complete.
        if (string.IsNullOrWhiteSpace(responseText))
        {
            _logger.LogWarning(
                "[MentorAgent:A2A] Task '{TaskId}' produced no reply — reported to the caller as failed. " +
                "The cause is in this log, above this line.", context.TaskId);

            await updater.FailAsync(
                new Message
                {
                    Role  = A2A.Role.Agent,
                    Parts = [Part.FromText("Error: this agent could not produce a reply to the request.")]
                },
                cancellationToken: cancellationToken);
            return;
        }

        await updater.CompleteAsync(
            new Message
            {
                Role     = A2A.Role.Agent,
                Parts    = [Part.FromText(responseText)],
                // EU AI Act, article 50 — the machine-readable marker of AI-generated text, on the reply only.
                Metadata = new() { [AiGeneratedMetadataKey] = AiGenerated },
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>The metadata key of the marker of AI-generated text on the reply a caller gets.</summary>
    internal const string AiGeneratedMetadataKey = "aiGenerated";

    private static readonly System.Text.Json.JsonElement AiGenerated = System.Text.Json.JsonSerializer.SerializeToElement(true);

    /// <summary>The caller's name for this application, if it sent one as a string. Not yet sanitised.</summary>
    private static string? ReadAddressedAs(Message? message)
        => message?.Metadata is { } metadata
           && metadata.TryGetValue(MentorTurnOrigin.AddressedAsMetadataKey, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The caller cancelled the task: its turn stops as with Stop, if it has not already, and the task ends
    /// <c>Canceled</c>. This replaced the SDK's default with a Debug line, so the task was answered <c>Working</c> and
    /// stayed so (BUG-199).
    /// </summary>
    public async Task CancelAsync(
        RequestContext context,
        AgentEventQueue queue,
        CancellationToken cancellationToken)
    {
        StopByCaller(context.TaskId);
        _logger.LogInformation("[MentorAgent:A2A] Task '{TaskId}' cancelled by the caller.", context.TaskId);
        await new TaskUpdater(queue, context.TaskId, context.ContextId).CancelAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Stops the turn of task <paramref name="taskId"/> as Stop does, when one runs here: the decisions of its calls read
    /// <c>stop</c>, and nothing of the task is reported after it. <see cref="MentorA2ARequestHandler"/> calls it before
    /// the SDK cancels the turn's token; <see cref="CancelAsync"/>, after.
    /// </summary>
    internal void StopByCaller(string taskId)
    {
        if (!_running.TryGetValue(taskId, out var running)) return;

        running.CancelledByCaller = true;
        try
        {
            running.Orchestrator.CancelCurrentRequest();
        }
        catch (ObjectDisposedException)
        {
            // The turn ended in between.
        }
    }

    /// <summary>How many tasks have a turn running here; each one leaves when its task ends, however it ends.</summary>
    internal int RunningTasks => _running.Count;
}
