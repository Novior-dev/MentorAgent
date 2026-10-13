using Novior.MentorAgent.Core;
using Novior.MentorAgent.Memory;

using Novior.MentorAgent.Options;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;
using System.Text.Json;

namespace Novior.MentorAgent.Providers;

/// <summary>
/// Injects dynamic context into the system prompt before each LLM call:
/// - App identity (name, description)
/// - Current date/time
/// - Current page (NavigationManager)
/// - User and roles (AuthenticationStateProvider)
/// - Page context (IMentorPageContext) � visible data + available UI actions
/// </summary>
internal sealed class AppContextProvider : AIContextProvider
{
    private readonly NavigationManager? _nav;
    private readonly AuthenticationStateProvider? _auth;
    private readonly IMentorPageContext _pageContext;
    private readonly MentorMemoryService _memory;
    private readonly MentorOptions _options;

    /// <summary>Who is on the other end of this scope's turns; null in hosts that do not register it.</summary>
    internal MentorTurnOrigin? Origin { get; init; }

    public AppContextProvider(
        IOptions<MentorOptions> options,
        IMentorPageContext pageContext,
        MentorMemoryService memory,
        NavigationManager? nav = null,
        AuthenticationStateProvider? auth = null)
    {
        _options = options.Value;
        _pageContext = pageContext;
        _memory = memory;
        _nav = nav;
        _auth = auth;
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();

        // Per-call (volatile) context only. App identity and the page list live in the
        // static system prompt (MentorPromptBuilder) so the provider can cache that stable
        // prefix; here we keep only what changes per request, with the most volatile data
        // (URL, date/time) emitted last. (Wave 1 #2/#3)
        sb.AppendLine("## SESSION CONTEXT (changes per request)");

        // -- Who is asking -----------------------------------------------------
        // Only an A2A request sets this. Here, in the per-request block, rather than in the static
        // prompt: the same coordinator prompt is shared by every caller and cached as a prefix.
        if (Origin?.FromAnotherAgent == true)
            sb.AppendLine(Origin.Notice);

        // -- Page context (name + visible data) --------------------------------
        if (!string.IsNullOrWhiteSpace(_pageContext.PageName))
            sb.AppendLine($"- Active page: {_pageContext.PageName}");

        var contextData = _pageContext.ContextData;
        if (contextData.Count > 0)
        {
            foreach (var (key, value) in contextData)
            {
                var serialized = value is null
                    ? "null"
                    : value is string s
                        ? s
                        : JsonSerializer.Serialize(value);
                sb.AppendLine($"- {key}: {serialized}");
            }
        }

        // Note: UI actions are not listed here.
        // Path A (IChatClient): UIActionsMiddleware injects each action as its own named
        // AI tool before every LLM call — the LLM discovers them via the tool list.
        // Path B (pre-built AIAgent): the legacy invoke_ui_action dispatcher is still active.

        // -- Auth --------------------------------------------------------------
        if (_auth is not null)
        {
            try
            {
                var state = await _auth.GetAuthenticationStateAsync()
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                var user = state.User;

                if (user.Identity?.IsAuthenticated == true)
                {
                    sb.AppendLine($"- User: {user.Identity.Name ?? "authenticated"}");
                    var roles = user.Claims
                        .Where(c => c.Type == ClaimTypes.Role)
                        .Select(c => c.Value)
                        .ToList();
                    if (roles.Count > 0)
                        sb.AppendLine($"- Roles: {string.Join(", ", roles)}");
                }
                // When the user is NOT authenticated we deliberately emit nothing.
                // A bare "User: not authenticated" line makes the model invent
                // authorization rules and refuse tools that carry no role requirement
                // (e.g. "you must be an admin to change prices"). Role-gated tools are
                // still enforced server-side by the middleware (CheckRolesAsync), which
                // returns a proper error the model relays — so the hint is both
                // unnecessary and actively harmful.
            }
            catch { /* ignore if unavailable */ }
        }

        // -- Contextual memory -------------------------------------------------
        if (_options.UseMemoryContext)
        {
            try
            {
                var memoryContext = await _memory
                    .GetMemoryContextAsync(_options.MemoryContextCount, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(memoryContext))
                    sb.AppendLine(memoryContext);
            }
            catch { /* ignore if memory store unavailable */ }
        }

        // -- Most volatile data last (URL, date/time) --------------------------
        // A turn served outside a circuit has no current URL, and on a Blazor Server host asking
        // for one throws: "'RemoteNavigationManager' has not been initialized". Found live on the
        // source sample as an A2A peer (BUG-083) — the one unguarded read in this method turned
        // every request from another agent into "Error in Coordinator" and an empty reply.
        if (_nav is not null)
        {
            try { sb.AppendLine($"- Current URL: {_nav.Uri}"); }
            catch (InvalidOperationException) { /* no circuit (A2A, MCP): there is no page to report */ }
        }
        sb.AppendLine($"- Current date/time: {DateTime.Now:dddd, MMMM dd yyyy, HH:mm}");

        return new AIContext { Instructions = sb.ToString() };
    }

    /// <summary>
    /// Post-response hook � called after each agent execution.
    /// Saves the timestamp of the last interaction to persistent memory.
    /// Documented in Agent Framework Apr 2026: AIContextProvider.StoreAIContextAsync.
    /// </summary>
    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.UseMemoryContext) return;
        if (context.InvokeException is not null) return;

        try
        {
            await _memory.RememberAsync(
                "last_session",
                DateTime.UtcNow.ToString("O"),
                cancellationToken).ConfigureAwait(false);
        }
        catch { /* ignore if memory store unavailable */ }
    }
}