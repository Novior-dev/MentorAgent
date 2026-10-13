namespace Novior.MentorAgent.Core;

/// <summary>
/// Decides whether a message is within the application's scope before an expensive provider-hosted
/// tool is declared for it (roadmap #15).
///
/// <para>
/// <b>Why a classifier and not embeddings.</b> This was first built as a cosine threshold against the
/// application's own metadata, and it did not work: raw cosine has no stable zero point, so two short
/// unrelated Italian phrases sat around 0.35–0.40 just for being short and Italian. Measured on a real
/// turn, <i>"genera immagine di un cane"</i> scored 0.398 against a shop's page names, and the same
/// request flipped from below to above the tool threshold on a one-word rewording. A single absolute
/// threshold cannot express the question being asked, which is comparative: <i>is this more like my
/// domain than like everything else?</i> A model reading the scope answers that directly.
/// </para>
///
/// <para>
/// <b>Why the cost is acceptable.</b> The gate runs only once per turn, and only on the turns where a
/// hosted tool has already passed relevance filtering — i.e. where real money is about to be spent
/// anyway. A couple of hundred tokens against a per-call web search or a generated image is three
/// orders of magnitude in the right direction.
/// </para>
/// </summary>
internal sealed class HostedToolDomainGate
{
    private readonly IChatClient _classifier;
    private readonly string      _scope;
    private readonly Func<string, CancellationToken, Task<bool>>? _custom;
    private readonly ILogger     _logger;
    private readonly Func<string, bool?>? _cachedVerdict;

    public HostedToolDomainGate(
        IChatClient classifier,
        string scope,
        Func<string, CancellationToken, Task<bool>>? custom,
        ILogger logger,
        Func<string, bool?>? cachedVerdict = null)
    {
        _classifier = classifier;
        _scope      = scope;
        _custom     = custom;
        _logger     = logger;
        _cachedVerdict = cachedVerdict;
    }

    /// <summary>
    /// True when the hosted tools may be declared for this message. <b>Fails open</b>: a classifier
    /// that errors, times out or answers something unexpected lets the turn through, because losing a
    /// capability to a transient outage is worse than paying for one call that should not have run.
    /// </summary>
    public async Task<bool> IsInScopeAsync(string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        try
        {
            if (_custom is not null)
                return await _custom(message, ct);

            // The input safety check reads the same message and can carry this question with it —
            // two labels, one call. When it has, the verdict is already here and the gate costs
            // nothing at all; when it has not (safety check off), the gate asks on its own, exactly
            // as before.
            if (_cachedVerdict?.Invoke(message) is { } known)
            {
                _logger.LogDebug("[MentorAgent] Hosted-tool domain check → {Verdict} (from the turn's classifier call).",
                    known ? "IN scope" : "OUT of scope");
                return known;
            }

            var response = await _classifier.GetResponseAsync(
                $"""
                You decide whether an assistant should spend an expensive capability on a request.
                SCOPE OF THE ASSISTANT: {_scope}
                Answer with ONE word only: IN if the USER MESSAGE is something this assistant should
                handle within that scope, OUT if it is unrelated to it.
                USER MESSAGE: {message}
                """,
                // 16 is the minimum the Responses API accepts — anything lower is rejected with
                // "integer below minimum value" and, thanks to the fail-open, would silently disable
                // this gate on every turn. One word needs far less, but the floor is the floor.
                new ChatOptions { MaxOutputTokens = 16, Temperature = 0f },
                ct);

            // Parsing the classifier's own constrained label, not user text. Only an explicit OUT
            // blocks: anything unexpected is treated as in scope, so the gate stays fail-open.
            var verdict  = (response.Text ?? string.Empty).TrimStart();
            var inScope  = !verdict.StartsWith("OUT", StringComparison.OrdinalIgnoreCase);

            _logger.LogDebug("[MentorAgent] Hosted-tool domain check → {Verdict} (classifier said '{Raw}').",
                inScope ? "IN scope" : "OUT of scope", verdict.Trim());

            return inScope;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[MentorAgent] Hosted-tool domain classifier failed — allowing the hosted tools for this turn.");
            return true;
        }
    }
}
