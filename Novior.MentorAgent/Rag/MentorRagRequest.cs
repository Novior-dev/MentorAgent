using System.Security.Claims;

namespace Novior.MentorAgent.Rag;

/// <summary>
/// What a search is for: the user's message and the context it was asked in. Passed to
/// <see cref="IMentorRagSource.SearchAsync(MentorRagRequest, CancellationToken)"/> before every turn
/// when <c>UseRag</c> is on.
///
/// <para>
/// A source that only needs the text keeps implementing
/// <see cref="IMentorRagSource.SearchAsync(string, int, CancellationToken)"/>: the default
/// implementation of the request overload calls it with <see cref="Query"/> and
/// <see cref="MaxResults"/>. A source that filters by user, page or conversation implements this one.
/// </para>
///
/// <para>
/// A class with init-only properties rather than a parameter list, so that what a search can know can
/// grow — a tenant, for one — without breaking a source that already implements it.
/// </para>
/// </summary>
public sealed class MentorRagRequest
{
    /// <summary>The user's message for this turn.</summary>
    public required string Query { get; init; }

    /// <summary>Maximum number of documents to return (<c>RagResultCount</c>).</summary>
    public int MaxResults { get; init; } = 5;

    /// <summary>
    /// The conversation before this message, oldest first: the user's messages and the assistant's
    /// replies of the last turns in this session, text only (up to six messages). Empty on the first
    /// turn, after a reset, and after a session is restored. Lets a source resolve "and the second one?"
    /// against what was said before.
    /// </summary>
    public IReadOnlyList<ChatMessage> RecentMessages { get; init; } = [];

    /// <summary>
    /// Who is asking: the caller of the hub, of <c>/mentor/chat</c> or of an A2A request, and in a Blazor
    /// circuit the user the host's <c>AuthenticationStateProvider</c> reports. <see langword="null"/> when the
    /// host has no authentication at all. An anonymous visitor, or a peer agent that did not authenticate, has
    /// an unauthenticated principal. A source that applies per-user permissions should treat both as "no access
    /// to anything restricted".
    /// </summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>The current page's name, as the page set it with <c>IMentorPageContext.SetPageName</c>.</summary>
    public string? PageName { get; init; }

    /// <summary>
    /// The data the current page put in context with <c>IMentorPageContext.Set</c> — the same entries the
    /// model is shown. Empty when no page has set any.
    /// </summary>
    public IReadOnlyDictionary<string, object?> PageData { get; init; } = new Dictionary<string, object?>();
}
