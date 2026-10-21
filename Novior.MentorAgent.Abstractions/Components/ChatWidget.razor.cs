using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Novior.MentorAgent.Abstractions.Components;

public partial class ChatWidget : IDisposable
{
    /// <summary>
    /// Optional custom renderer for generative cards (#14 L2). When set it replaces
    /// <c>MentorCardView</c> for <b>every</b> card, so switch on <c>card.Kind</c> inside it and fall
    /// back to <c>&lt;MentorCardView /&gt;</c> for the kinds you do not handle:
    /// <code>
    /// &lt;ChatWidget&gt;
    ///     &lt;CardTemplate Context="card"&gt;
    ///         @if (card.Kind == "order") { &lt;OrderCard Card="card" /&gt; }
    ///         else                       { &lt;MentorCardView Card="card" /&gt; }
    ///     &lt;/CardTemplate&gt;
    /// &lt;/ChatWidget&gt;
    /// </code>
    /// </summary>
    [Parameter] public RenderFragment<MentorCard>? CardTemplate { get; set; }

    [Inject] private IMentorOrchestrator        Orchestrator { get; set; } = default!;
    [Inject] private IMentorStateService        State        { get; set; } = default!;
    [Inject] private IJSRuntime                 JS           { get; set; } = default!;
    [Inject] private NavigationManager          Nav          { get; set; } = default!;
    [Inject] private ILogger<ChatWidget>        Logger       { get; set; } = default!;
    // #18 — optional: a host that never enabled the tour has no IMentorTour registered, and the
    // widget must still work. Resolved through the provider so a missing registration is a null,
    // not a startup crash.
    [Inject] private IServiceProvider           Services     { get; set; } = default!;

    // â”€â”€ State â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private bool _open            = false;
    private bool _busy            = false;
    private bool _mcpDetailOpen   = false;
    private bool _a2aDetailOpen   = false;
    private bool _hostedDetailOpen = false;
    private int  _unread          = 0;

    // ── Hosted tools (#15) — provider-side tools declared to the model ─────────
    // Static configuration, not a live connection: unlike MCP there is nothing to connect to, so
    // the badge simply lists what the server declared.

    /// <summary>
    /// What the server said it actually enabled, once it has said it. Hosted tools live entirely on
    /// the server, so on a detached client the local options are only a guess — and a guess that
    /// drifts the moment the server's configuration changes. <see langword="null"/> until the
    /// declaration arrives (and forever in-process, where the options already hold the truth).
    /// </summary>
    private MentorHostedTools? _declaredHostedTools;

    private IReadOnlyList<MentorHostedTools> ActiveHostedTools
    {
        get
        {
            var configured = _declaredHostedTools ?? Options.Value.HostedTools;
            var list = new List<MentorHostedTools>(5);
            if (configured.HasFlag(MentorHostedTools.WebSearch))       list.Add(MentorHostedTools.WebSearch);
            if (configured.HasFlag(MentorHostedTools.CodeInterpreter)) list.Add(MentorHostedTools.CodeInterpreter);
            if (configured.HasFlag(MentorHostedTools.FileSearch))      list.Add(MentorHostedTools.FileSearch);
            if (configured.HasFlag(MentorHostedTools.ImageGeneration)) list.Add(MentorHostedTools.ImageGeneration);
            if (configured.HasFlag(MentorHostedTools.HostedMcp))       list.Add(MentorHostedTools.HostedMcp);
            return list;
        }
    }

    private string HostedToolLabel(MentorHostedTools tool) => tool switch
    {
        MentorHostedTools.WebSearch       => L.Get("hosted.websearch"),
        MentorHostedTools.CodeInterpreter => L.Get("hosted.code"),
        MentorHostedTools.FileSearch      => L.Get("hosted.filesearch"),
        MentorHostedTools.ImageGeneration => L.Get("hosted.imagegen"),
        MentorHostedTools.HostedMcp       => L.Get("hosted.mcp"),
        _                                 => tool.ToString(),
    };

    // ── MCP pill helpers — computed for the inlined badge in ChatWidget.razor ──
    private string McpPillClass
    {
        get
        {
            var total  = _mcpServerStatus.Count;
            var online = _mcpServerStatus.Count(kv => kv.Value == true);
            var conn   = total == 0 || _mcpServerStatus.Any(kv => kv.Value == null);
            var allOk  = total > 0 && online == total;
            var anyOk  = online > 0;
            return conn  ? "bm-mcp-pill--connecting"
                 : allOk ? "bm-mcp-pill--online"
                 : anyOk ? "bm-mcp-pill--warn"
                         : "bm-mcp-pill--offline";
        }
    }

    private string McpPillLabel
    {
        get
        {
            var total  = _mcpServerStatus.Count;
            var online = _mcpServerStatus.Count(kv => kv.Value == true);
            return total == 0 ? "MCP"
                 : total == 1 ? _mcpServerStatus.Keys.First()
                              : $"{online}/{total}";
        }
    }

    private string McpPillTitle
    {
        get
        {
            var total  = _mcpServerStatus.Count;
            var online = _mcpServerStatus.Count(kv => kv.Value == true);
            var conn   = total == 0 || _mcpServerStatus.Any(kv => kv.Value == null);
            var allOk  = total > 0 && online == total;
            var anyOk  = online > 0;
            // BUG-049 — these four were Italian literals in a widget that ships ten languages,
            // and McpStatusBadge rendered the same four in English. Both now read the table.
            return conn  ? L.Get("mcp.connecting")
                 : allOk ? L.Get("mcp.allOnline")
                 : anyOk ? string.Format(L.Get("mcp.someOnline"), online, total)
                         : L.Get("mcp.offline");
        }
    }

    /// <summary>Tooltip and accessible name for the A2A pill (BUG-048, BUG-049).</summary>
    private string A2APillTitle => L.Get("a2a.pill");
    private string _currentAction = string.Empty;

    private readonly List<ChatMessage>  _messages           = [];
    private readonly List<DateTime>     _messageTimestamps  = [];
    private readonly System.Text.StringBuilder _streamingText = new();

    private ConfirmationRequest? _confirmRequest;
    private ElementReference     _scrollEl;
    private bool _voiceOutputEnabled  = false;
    private bool _voiceSynthSupported = false;
    // #17 — streaming TTS. Chunks are accumulated here and handed to JS a sentence at a time:
    // one interop call per token would put a SignalR round-trip on every chunk of every answer,
    // and a sentence is the unit the synthesis queue works in anyway.
    private readonly System.Text.StringBuilder _speechBuffer = new();
    private bool _speechOpen;

    // #18 — onboarding tour. Loaded lazily, the first time the widget is opened.
    private IReadOnlyList<MentorTourStep> _tourSteps = [];
    private bool _tourOpen;
    private bool _tourChecked;
    private int  _tourIndex;
    private const string TourSeenKey = "mentoragent.tour.seen";
    // Where the user got to, so acting on a step resumes the tour instead of ending it. Separate
    // from the seen flag because they answer different questions: "is this user done with the
    // tour" and "how far in were they". Cleared when the tour is genuinely finished.
    private const string TourStepKey = "mentoragent.tour.step";

    private readonly Dictionary<int, IReadOnlyList<MentorRagResult>> _messageSources  = new();
    private IReadOnlyList<MentorRagResult>? _pendingRagSources;
    // Images produced by the hosted image-generation tool (#15), attached to the message being built.
    private IReadOnlyList<string>? _pendingImages;
    // #14 L2 — cards a tool returned during this turn, attached to the message on completion.
    private readonly Dictionary<int, IReadOnlyList<MentorCard>> _messageCards = new();
    private List<MentorCard>? _pendingCards;
    // null = connecting (pre-populated before lazy-init), true = online, false = offline
    private readonly Dictionary<string, bool?> _mcpServerStatus = new(StringComparer.OrdinalIgnoreCase);

    // â”€â”€ Lifecycle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var opt = Options.Value;
            if (opt.EnableVoiceOutput)
            {
                try
                {
                    _voiceSynthSupported = await JS.InvokeAsync<bool>("MentorAgent.isSpeechSynthesisSupported");
                    _voiceOutputEnabled  = _voiceSynthSupported;
                    if (_voiceSynthSupported) StateHasChanged();
                }
                catch { _voiceSynthSupported = false; }
            }
        }
    }

    protected override void OnInitialized()
    {
        // Pre-populate MCP servers as null ("connecting") so the badge is visible
        // immediately at startup — before lazy initialisation fires on the first message.
        var opt = Options.Value;
        foreach (var name in opt.McpServerNames)
            _mcpServerStatus[name] = null;

        State.OnStreamingChunk        += OnChunk;
        State.OnStreamingCompleted    += OnCompleted;
        State.OnBusyChanged           += OnBusy;
        State.OnActionExecuting       += OnActionExecuting;
        State.OnActionCompleted       += OnActionCompleted;
        State.OnActionFailed          += OnActionFailed;
        State.OnError                 += OnError;
        State.OnConfirmationRequired  += OnConfirmationRequired;
        State.OnNavigationRequested   += OnNavigationRequested;
        State.OnRagSourcesReady       += OnRagSourcesReady;
        State.OnGeneratedImages       += OnGeneratedImages;
        State.OnCardsReady            += OnCardsReady;
        State.OnMcpServerStatusChanged += OnMcpServerStatusChanged;
        State.OnHostedToolsDeclared   += OnHostedToolsDeclared;
        State.OnTeamMemberSpeaking    += OnTeamMemberSpeaking;
    }

    public void Dispose()
    {
        State.OnStreamingChunk        -= OnChunk;
        State.OnStreamingCompleted    -= OnCompleted;
        State.OnBusyChanged           -= OnBusy;
        State.OnActionExecuting       -= OnActionExecuting;
        State.OnActionCompleted       -= OnActionCompleted;
        State.OnActionFailed          -= OnActionFailed;
        State.OnError                 -= OnError;
        State.OnConfirmationRequired  -= OnConfirmationRequired;
        State.OnNavigationRequested   -= OnNavigationRequested;
        State.OnRagSourcesReady       -= OnRagSourcesReady;
        State.OnGeneratedImages       -= OnGeneratedImages;
        State.OnCardsReady            -= OnCardsReady;
        State.OnMcpServerStatusChanged -= OnMcpServerStatusChanged;
        State.OnHostedToolsDeclared   -= OnHostedToolsDeclared;
        State.OnTeamMemberSpeaking    -= OnTeamMemberSpeaking;
    }

    // â”€â”€ UI Actions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private Task Toggle() { if (_open) { Close(); return Task.CompletedTask; } return Open(); }

    private async Task Open()
    {
        _open = true;
        _unread = 0;
        await MaybeStartTourAsync();
    }

    private void Close() => _open = false;

    // ── Onboarding tour (#18) ─────────────────────────────────────────────────

    /// <summary>
    /// Shows the tour the first time this user opens the widget. Everything here is deliberately
    /// lazy: an app with the tour disabled pays nothing, and an app with it enabled pays only when
    /// someone actually opens the chat — not on every page render.
    /// </summary>
    private async Task MaybeStartTourAsync()
    {
        if (_tourChecked || !Options.Value.EnableOnboardingTour) return;
        _tourChecked = true;

        if (Services.GetService(typeof(IMentorTour)) is not IMentorTour tour)
        {
            Logger.LogWarning(
                "[MentorAgent:UI] EnableOnboardingTour is on but no IMentorTour is registered — no tour will be shown.");
            return;
        }

        try
        {
            if (await JS.InvokeAsync<bool>("MentorAgent.flagGet", TourSeenKey)) return;

            _tourSteps = await tour.GetStepsAsync();
            // No steps is a legitimate answer (an app with no [MentorPage]); do not mark it seen,
            // so a later deployment that does have pages still gets its first run.
            if (_tourSteps.Count == 0) return;

            var saved = await LoadTourIndexAsync();
            // Past the end: a shorter tour shipped since, and there is nothing left to resume to.
            if (Core.TourProgress.IsExhausted(saved, _tourSteps.Count)) { await EndTourAsync(); return; }

            _tourIndex = Core.TourProgress.OpenAt(saved, _tourSteps.Count);
            _tourOpen = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[MentorAgent:UI] The onboarding tour could not be started.");
        }
    }

    /// <summary>Where this user left off, or 0. Never throws — a broken value just starts over.</summary>
    private async Task<int> LoadTourIndexAsync()
    {
        try
        {
            var raw = await JS.InvokeAsync<string?>("MentorAgent.valueGet", TourStepKey);
            return Core.TourProgress.ParseSavedIndex(raw);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Closes the tour for good: skipped or completed. Both clear the resume position — the point
    /// of keeping it is an *unfinished* tour, and leaving it behind would strand the replay button
    /// on the last step the user happened to reach.
    /// </summary>
    private async Task EndTourAsync()
    {
        _tourOpen = false;
        _tourIndex = 0;
        try
        {
            await JS.InvokeVoidAsync("MentorAgent.flagSet", TourSeenKey);
            await JS.InvokeVoidAsync("MentorAgent.valueClear", TourStepKey);
        }
        catch { }
        StateHasChanged();
    }

    private Task CloseTour() => EndTourAsync();

    /// <summary>
    /// Suspends the tour at <paramref name="resumeAt"/> without marking it seen. Acting on a step —
    /// tapping its question, opening its screen — is engagement, not dismissal, and treating it as
    /// "done" cost the user every remaining step for having used the tour as intended.
    /// </summary>
    private async Task SuspendTourAsync(int resumeAt)
    {
        _tourOpen = false;
        _tourIndex = resumeAt;
        try { await JS.InvokeVoidAsync("MentorAgent.valueSet", TourStepKey, resumeAt); } catch { }
        StateHasChanged();
    }

    /// <summary>Remembers the step the user is on, so a reload does not lose their place.</summary>
    private async Task OnTourIndexChanged(int index)
    {
        _tourIndex = index;
        try { await JS.InvokeVoidAsync("MentorAgent.valueSet", TourStepKey, index); } catch { }
    }

    /// <summary>
    /// Replays the tour on demand, ignoring the "already seen" flag. It resumes at the saved
    /// position rather than restarting: a user who left at step 2 wants step 3, not step 1 again.
    /// </summary>
    internal async Task ReplayTour()
    {
        // Every early exit below logs. The button is visible whenever EnableOnboardingTour is on,
        // so a silent return here is a button that does nothing with no way to find out why —
        // which is exactly how this was reported.
        if (Services.GetService(typeof(IMentorTour)) is not IMentorTour tour)
        {
            Logger.LogWarning(
                "[MentorAgent:UI] Replay tour: no IMentorTour is registered, so there is nothing to show.");
            return;
        }
        try
        {
            if (_tourSteps.Count == 0) _tourSteps = await tour.GetStepsAsync();
            if (_tourSteps.Count == 0)
            {
                Logger.LogWarning(
                    "[MentorAgent:UI] Replay tour: the tour has no steps. In a headless/WASM host check that "
                    + "GET /mentor/tour answers and that the server has [MentorPage] pages; the server "
                    + "serves the tour whether or not it sets EnableOnboardingTour.");
                return;
            }
            var saved = await LoadTourIndexAsync();
            // A finished tour has no saved position, so replay starts over — which is what the
            // button means once there is nothing left to resume.
            _tourIndex = Core.TourProgress.IsExhausted(saved, _tourSteps.Count)
                ? 0
                : Core.TourProgress.OpenAt(saved, _tourSteps.Count);

            _tourOpen = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[MentorAgent:UI] The onboarding tour could not be replayed.");
        }
    }

    private async Task TourNavigate(string url)
    {
        // Resume past this step: the user has taken its suggestion, so replaying it is a step back.
        await SuspendTourAsync(Core.TourProgress.ResumeAfter(_tourIndex));
        Nav.NavigateTo(url);
    }

    private async Task TourAsk(string question)
    {
        await SuspendTourAsync(Core.TourProgress.ResumeAfter(_tourIndex));
        await SendMessage(question);
    }

    /// <summary>
    /// Set from the moment a message is sent until the transport reports the turn — busy, a reply, an
    /// error — or the send ends. On WebAssembly the busy flag comes back over the hub a round trip later,
    /// and a reset in between let the reply land in the new conversation.
    /// </summary>
    private bool _awaitingReply;

    /// <summary>Whether "New conversation" is available: not while a reply or a confirmation is pending.</summary>
    private bool CanReset => !_busy && !_awaitingReply && _confirmRequest is null;

    private async Task ResetSession()
    {
        // A reset during a turn cleared the transcript and the old reply then streamed into the new
        // one; during a confirmation it left the banner hanging over it. Stop the turn or answer the
        // banner first — the button is disabled meanwhile, and this repeats the guard for a click that
        // raced the re-render.
        if (!CanReset) return;

        _messages.Clear();
        _messageTimestamps.Clear();
        _streamingText.Clear();
        _messageSources.Clear();
        _messageCards.Clear();
        _pendingRagSources = null;
        _pendingCards = null;
        _currentAction = string.Empty;
        await StopSpeakingAsync();

        try
        {
            await Orchestrator.ResetSessionAsync();
        }
        catch (Exception ex)
        {
            // BUG-109: a reset the transport could not deliver must not reach the page's error UI. The
            // transcript is already cleared, and a connection that failed to carry the reset is not
            // carrying the old conversation either — the next message opens a fresh one.
            Logger.LogWarning(ex, "[MentorAgent:UI] New conversation: the reset did not reach the server.");
        }

        StateHasChanged();
    }

    /// <summary>Text-only send — used by the welcome suggestion chips.</summary>
    private Task SendMessage(string text) => SendUserMessage(new MentorUserMessage(text));

    /// <summary>
    /// Sends a user turn, optionally carrying attached images (#13). The local echo is built from the
    /// same content parts the orchestrator sends, so the bubble shows exactly what the model receives.
    /// </summary>
    private async Task SendUserMessage(MentorUserMessage msg)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(msg.Text) && !msg.HasAttachments) return;

        // #17 barge-in — sending is taking the floor. Without this the previous answer keeps
        // playing over the new question, and in hands-free mode into the open microphone.
        if (_voiceOutputEnabled && Options.Value.VoiceBargeIn)
            await StopSpeakingAsync();

        _messages.Add(BuildEcho(msg));
        _messageTimestamps.Add(DateTime.Now);
        _streamingText.Clear();
        _awaitingReply = true;
        StateHasChanged();
        await ScrollToBottom();

        // Fire-and-forget, but never silently: if the send itself fails (e.g. the transport
        // rejected the payload) the user must see something rather than a chat that just sits there.
        // The send ends with the turn on every transport, so its end also releases the reset guard.
        _ = Orchestrator.SendMessageAsync(msg.Text, msg.Attachments)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted) ShowSendFailure(t.Exception!);
                            else _ = InvokeAsync(() => { _awaitingReply = false; StateHasChanged(); });
                        }, TaskScheduler.Default);
    }

    private void ShowSendFailure(AggregateException error)
        => InvokeAsync(() =>
        {
            var ex = error.GetBaseException();
            Logger.LogError(ex, "[MentorAgent:UI] Sending the message failed.");
            _busy = false;
            _awaitingReply = false;
            // The exception's text is for the log: it is a transport's or a server's internal message, and
            // the chat showed it as it was (BUG-147).
            _messages.Add(new ChatMessage(ChatRole.Assistant, $"⚠ {L.Get("error.send")}"));
            _messageTimestamps.Add(DateTime.Now);
            StateHasChanged();
        });

    private static ChatMessage BuildEcho(MentorUserMessage msg)
    {
        if (!msg.HasAttachments)
            return new ChatMessage(ChatRole.User, msg.Text);

        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(msg.Text))
            contents.Add(new TextContent(msg.Text));
        foreach (var a in msg.Attachments!)
            if (a.ToAIContent() is { } c)
                contents.Add(c);
        return new ChatMessage(ChatRole.User, contents);
    }

    private async Task HandleConfirm()
    {
        Logger.LogDebug("[MentorAgent:UI] HandleConfirm called — _confirmRequest={IsNull}", _confirmRequest is null ? "NULL" : _confirmRequest.ActionId.ToString());
        var req = _confirmRequest;
        _confirmRequest = null;
        StateHasChanged();
        if (req is not null)
            await State.ConfirmAsync(req.ActionId);
        else
            Logger.LogWarning("[MentorAgent:UI] HandleConfirm — _confirmRequest was null, ConfirmAsync NOT called");
    }

    private void HandleCancel()
    {
        Logger.LogDebug("[MentorAgent:UI] HandleCancel called — _confirmRequest={IsNull}", _confirmRequest is null ? "NULL" : _confirmRequest.ActionId.ToString());
        var req = _confirmRequest;
        _confirmRequest = null;
        StateHasChanged();
        if (req is not null)
            State.Cancel(req.ActionId);
        else
            Logger.LogWarning("[MentorAgent:UI] HandleCancel — _confirmRequest was null, Cancel NOT called");
    }

    private async Task CancelRequest()
    {
        Orchestrator.CancelCurrentRequest();
        // Stop means stop: text generation and the voice reading it out are one action to the user.
        await StopSpeakingAsync();
    }

    // â”€â”€ Event Handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnChunk(string chunk)
        => InvokeAsync(async () =>
        {
            _streamingText.Append(chunk);
            StateHasChanged();
            _ = ScrollToBottom();
            await PushSpeechAsync(chunk);
        });

    /// <summary>
    /// Feeds the streaming synthesis queue (#17). Held back until the pending text reaches a
    /// sentence boundary — or grows past a sentence's worth without one, which is what a list or a
    /// long clause looks like — so the interop traffic is per sentence, not per token.
    /// </summary>
    private async Task PushSpeechAsync(string chunk)
    {
        if (!_voiceOutputEnabled || !Options.Value.VoiceStreaming) return;

        _speechBuffer.Append(chunk);
        if (!HasSentenceBoundary(chunk) && _speechBuffer.Length < 240) return;

        var pending = _speechBuffer.ToString();
        _speechBuffer.Clear();
        try
        {
            if (!_speechOpen)
            {
                // Claim the session *before* awaiting. The await yields, and the next chunk's
                // continuation runs while this one is still in flight: with the flag set after
                // the call, both chunks passed the check and opened a session, and the second
                // beginSpeech reset the queue the first had just filled.
                _speechOpen = true;
                await JS.InvokeVoidAsync("MentorAgent.beginSpeech",
                                         Options.Value.Language.ToIsoCode(), Options.Value.VoiceRate);
            }
            await JS.InvokeVoidAsync("MentorAgent.pushSpeech", pending);
        }
        catch { /* JS gone (circuit closing) — the answer is still on screen */ }
    }

    private static bool HasSentenceBoundary(string s)
    {
        foreach (var c in s)
            if (c is '.' or '!' or '?' or '…' or '\n') return true;
        return false;
    }

    /// <summary>Silences playback and drops anything still buffered (#17).</summary>
    private async Task StopSpeakingAsync()
    {
        _speechBuffer.Clear();
        _speechOpen = false;
        try { await JS.InvokeVoidAsync("MentorAgent.stopSpeaking"); } catch { }
    }

    private void OnCompleted()
        => InvokeAsync(async () =>
        {
            // A hosted image-generation turn can answer with an image and no text at all, and a
            // card-only turn is the same shape (#14), so the message is committed when there is any
            // of the three.
            if (_streamingText.Length > 0
                || _pendingImages is { Count: > 0 }
                || _pendingCards is { Count: > 0 })
            {
                var text = _streamingText.ToString();
                _messages.Add(BuildAssistantMessage(text, _pendingImages));
                _messageTimestamps.Add(DateTime.Now);

                if (_pendingRagSources is { Count: > 0 })
                {
                    _messageSources[_messages.Count - 1] = _pendingRagSources;
                    _pendingRagSources = null;
                }

                if (_pendingCards is { Count: > 0 })
                {
                    _messageCards[_messages.Count - 1] = _pendingCards;
                    _pendingCards = null;
                }

                _pendingImages = null;
                _streamingText.Clear();
                if (!_open) _unread++;

                if (_voiceOutputEnabled && !string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        if (_speechOpen)
                        {
                            // Flush the tail — the last sentence usually has no trailing space,
                            // so the JS boundary scan deliberately left it in the buffer.
                            if (_speechBuffer.Length > 0)
                                await JS.InvokeVoidAsync("MentorAgent.pushSpeech", _speechBuffer.ToString());
                            await JS.InvokeVoidAsync("MentorAgent.endSpeech");
                        }
                        else
                        {
                            // Streaming off, or an answer too short to have crossed a boundary:
                            // one utterance for the whole thing.
                            await JS.InvokeVoidAsync("MentorAgent.speak", text,
                                                     Options.Value.Language.ToIsoCode(), Options.Value.VoiceRate);
                        }
                    }
                    catch { }
                    finally
                    {
                        _speechBuffer.Clear();
                        _speechOpen = false;
                    }
                }
            }
            StateHasChanged();
            _ = ScrollToBottom();
        });

    private void OnBusy(bool busy)
        => InvokeAsync(() =>
        {
            _busy = busy;
            _awaitingReply = false;

            // A confirmation is only ever raised while the turn is busy, so a banner still up when the
            // turn ends — Stop pressed while it was showing — has nobody left waiting for its answer.
            if (!busy) _confirmRequest = null;
            StateHasChanged();
        });

    private void OnActionExecuting(string action)
        => InvokeAsync(() => { _currentAction = action; StateHasChanged(); });

    private void OnActionCompleted(string _)
        => InvokeAsync(() => { _currentAction = string.Empty; StateHasChanged(); });

    private void OnActionFailed(string error)
        => InvokeAsync(() =>
        {
            _currentAction = string.Empty;
            _messages.Add(new ChatMessage(ChatRole.Assistant, $"⚠ Error: {error}"));
            _messageTimestamps.Add(DateTime.Now);
            StateHasChanged();
        });

    private void OnError(string error)
        => InvokeAsync(() =>
        {
            _awaitingReply = false;
            _messages.Add(new ChatMessage(ChatRole.Assistant, $"⚠ {error}"));
            _messageTimestamps.Add(DateTime.Now);
            StateHasChanged();
        });

    private void OnConfirmationRequired(ConfirmationRequest req)
        => InvokeAsync(() => { _confirmRequest = req; StateHasChanged(); });

    private void OnNavigationRequested(string url)
        => InvokeAsync(() => { Nav.NavigateTo(url); StateHasChanged(); });

    private void OnRagSourcesReady(IReadOnlyList<MentorRagResult> sources)
        => _pendingRagSources = sources.Count > 0 ? sources : null;

    private void OnGeneratedImages(IReadOnlyList<string> images)
        => _pendingImages = images.Count > 0 ? images : null;

    /// <summary>
    /// Builds the assistant message, attaching any provider-generated images (#15) as content parts.
    /// MessageBubble reads images off the message itself, so this is all the rendering needs.
    /// </summary>
    private static ChatMessage BuildAssistantMessage(string text, IReadOnlyList<string>? images)
    {
        if (images is not { Count: > 0 })
            return new ChatMessage(ChatRole.Assistant, text);

        var contents = new List<AIContent>(images.Count + 1);
        if (!string.IsNullOrWhiteSpace(text))
            contents.Add(new TextContent(text));

        foreach (var src in images)
        {
            // The reporter hands over ready-to-render sources: a data: URI when the provider returned
            // bytes, an absolute URL when it returned a link. Anything else is dropped rather than
            // interpolated into markup.
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                contents.Add(new DataContent(src));
            else if (Uri.TryCreate(src, UriKind.Absolute, out var uri)
                     && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                contents.Add(new UriContent(uri, "image/*"));
        }

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private void OnMcpServerStatusChanged(string serverName, bool connected)
        => InvokeAsync(() => { _mcpServerStatus[serverName] = connected; StateHasChanged(); });

    // ── Generative cards (#14 L2) ─────────────────────────────────────────────

    /// <summary>
    /// Collects the cards a tool returned. They are held until the turn ends and then attached to
    /// the message, so a card never appears above the sentence that introduces it.
    /// </summary>
    private void OnCardsReady(IReadOnlyList<MentorCard> cards)
        => InvokeAsync(() =>
        {
            (_pendingCards ??= []).AddRange(cards);
            StateHasChanged();
        });

    /// <summary>
    /// Runs a card button. Every branch acts on a value written by the tool that built the card, not
    /// by the model — which is why a card is allowed to navigate or to fire a UI action at all.
    /// </summary>
    private Func<MentorCardAction, Task>? _cardDispatch;

    /// <summary>
    /// <see cref="OnCardAction"/>, cascaded around the cards so a <c>CardTemplate</c>'s own
    /// <c>&lt;MentorCardView&gt;</c> reaches it. One instance for the widget's lifetime (IsFixed).
    /// </summary>
    private Func<MentorCardAction, Task> CardDispatch => _cardDispatch ??= OnCardAction;

    private async Task OnCardAction(MentorCardAction action)
    {
        switch (action.Kind)
        {
            case MentorCardActionKind.SendMessage:
                await SendMessage(action.Value);
                break;

            case MentorCardActionKind.Navigate:
                Nav.NavigateTo(action.Value);
                break;

            case MentorCardActionKind.UIAction:
                await RunUIActionAsync(action.Value);
                break;
        }
    }

    /// <summary>
    /// Runs a UI action the current page registered. Resolved at click time, not when the card was
    /// built: the user may have navigated since, and a handler from the previous page would either
    /// be gone or — worse — still hold a reference to a disposed component.
    /// </summary>
    private async Task RunUIActionAsync(string actionName)
    {
        if (Services.GetService(typeof(IMentorPageContext)) is not IMentorPageContext page) return;

        if (!page.UIActions.TryGetValue(actionName, out var reg))
        {
            Logger.LogWarning(
                "[MentorAgent:Cards] The card asked for UI action '{Action}', which the current page does not register.",
                actionName);
            return;
        }

        State.NotifyUIActionExecuting(actionName);
        try
        {
            if (reg.HandlerAsync is not null) await reg.HandlerAsync(null);
            else reg.Handler(null);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[MentorAgent:Cards] UI action '{Action}' failed.", actionName);
        }
        State.NotifyUIActionCompleted(actionName);
    }

    private void OnHostedToolsDeclared(MentorHostedTools tools)
        => InvokeAsync(() => { _declaredHostedTools = tools; StateHasChanged(); });

    private void OnTeamMemberSpeaking(string teamName, string memberName)
        => InvokeAsync(() =>
        {
            var prefix = L.Get("action.team.analyzing").TrimEnd('.').TrimEnd(' ');
            var friendly = ToFriendlyName(memberName);
            _currentAction = $"{prefix} · {friendly}";
            StateHasChanged();
        });

    // â”€â”€ Voice Output â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    internal async Task ToggleVoiceOutput()
    {
        _voiceOutputEnabled = !_voiceOutputEnabled;
        if (!_voiceOutputEnabled)
            await StopSpeakingAsync();
        StateHasChanged();
    }

    // â”€â”€ Theme builder â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static string BuildThemeVars(MentorWidgetOptions opt)
    {
        var sb = new System.Text.StringBuilder();
        switch (opt.Theme)
        {
            case MentorTheme.Dark:
                sb.Append("--bm-bg-base:#050810;--bm-bg-panel:rgba(5,8,16,0.85);--bm-bg-surface:#0d1117;--bm-bg-elevated:#161b25;--bm-bg-hover:#1c2230;--bm-text-primary:#f0f4ff;--bm-text-secondary:#8b9cc8;--bm-text-muted:#4a567a;");
                break;
            case MentorTheme.Minimal:
                sb.Append("--bm-bg-base:#ffffff;--bm-bg-panel:rgba(255,255,255,0.97);--bm-bg-surface:#f8fafc;--bm-bg-elevated:#f1f5f9;--bm-bg-hover:#e2e8f0;--bm-text-primary:#0f172a;--bm-text-secondary:#475569;--bm-text-muted:#94a3b8;--bm-border:rgba(0,0,0,.1);--bm-border-accent:rgba(0,0,0,.2);--bm-shadow-panel:0 8px 40px rgba(0,0,0,.15),0 0 0 1px rgba(0,0,0,.08),inset 0 1px 0 rgba(255,255,255,.8);");
                // The hosted-tools and A2A chips keep their amber/indigo tint on a white surface, so
                // their pale dark-theme text becomes unreadable. Deep amber-700 / indigo-700 keep the
                // same identity with enough contrast to actually read.
                sb.Append("--bm-hosted-text:#b45309;--bm-hosted-text-strong:#92400e;--bm-a2a-text:#4338ca;--bm-a2a-text-strong:#3730a3;");
                break;
        }
        if (!string.IsNullOrWhiteSpace(opt.PrimaryColor))
        {
            sb.Append($"--bm-accent:{opt.PrimaryColor};");
            if (TryParseHexColor(opt.PrimaryColor, out var r, out var g, out var b))
            {
                sb.Append($"--bm-accent-dark:rgb({(int)(r*.82)},{(int)(g*.82)},{(int)(b*.82)});");
                sb.Append($"--bm-accent-glow:rgba({r},{g},{b},.35);");
                sb.Append($"--bm-border-accent:rgba({r},{g},{b},.3);");
                sb.Append($"--bm-shadow-fab:0 8px 32px rgba({r},{g},{b},.4),0 2px 8px rgba(0,0,0,.5);");
            }
        }
        return sb.ToString();
    }

    private static bool TryParseHexColor(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        hex = hex.TrimStart('#');
        return hex.Length == 6
            && int.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out r)
            && int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out g)
            && int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out b);
    }

    private static string ToFriendlyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        name = name.Replace("Agent", "").Replace("Team", "").Trim();
        return System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
    }

    // â”€â”€ Scroll â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task ScrollToBottom()
    {
        try { await JS.InvokeVoidAsync("MentorAgent.scrollToBottom", _scrollEl); }
        catch { }
    }
}
