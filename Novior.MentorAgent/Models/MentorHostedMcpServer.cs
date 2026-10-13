namespace Novior.MentorAgent.Models;

/// <summary>
/// Configuration for a <b>provider-hosted</b> MCP server (roadmap #15 — Agent Framework
/// <i>Hosted MCP Tools</i>). The model provider connects to the server and calls its tools while
/// generating the answer.
///
/// <para>
/// <b>Not the same as <see cref="MentorMcpServer"/>.</b> There, <i>your</i> process is the MCP client:
/// the tools are fetched at startup, listed in the prompt, and invoked locally — so they pass through
/// MentorAgent's gate (roles, confirmation, metrics). Here the provider is the client: your server
/// never talks to the MCP server at all, it only tells the provider where it is. That means the MCP
/// server must be reachable <b>from the provider's network</b>, not from yours.
/// </para>
///
/// <para>
/// Approval is therefore the provider's job too, via <see cref="RequireApproval"/>. It maps to the
/// Agent Framework's <c>HostedMcpServerToolApprovalMode</c> and surfaces through the same confirmation
/// banner as everything else, in either <c>HitlMode</c> and whatever
/// <see cref="Options.MentorOptions.RequireConfirmation"/> says: that option is MentorAgent's own gate.
/// </para>
///
/// Usage:
/// <code>
/// options.HostedTools |= MentorHostedTools.HostedMcp;
/// options.HostedMcpServers = [
///     new MentorHostedMcpServer {
///         Name         = "microsoft_learn",
///         Url          = "https://learn.microsoft.com/api/mcp",
///         AllowedTools = ["microsoft_docs_search"],
///     }
/// ];
/// </code>
/// </summary>
public sealed class MentorHostedMcpServer
{
    /// <summary>
    /// Identifier the provider uses for this server. Keep it short and stable — the model sees it.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Address of the remote MCP server. Normally a URL; some providers also accept the well-known
    /// name of a built-in server. Must be reachable from the <b>provider</b>, so <c>localhost</c> and
    /// private addresses do not work.
    /// </summary>
    public required string Url { get; set; }

    /// <summary>Optional description passed to the provider to give the model more context.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// If not null, only these tool names may be used. Null (default) allows every tool the server
    /// exposes — prefer an explicit list for a server you do not control.
    /// </summary>
    public IList<string>? AllowedTools { get; set; }

    /// <summary>
    /// When true (default), every call to this server needs human approval. Set false only for
    /// read-only servers you trust: the provider will then call them with no confirmation at all.
    ///
    /// <para>
    /// Fine-grained control is available through <see cref="AlwaysRequireApprovalTools"/> /
    /// <see cref="NeverRequireApprovalTools"/>, which take precedence over this flag.
    /// </para>
    /// </summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>Tool names that always require approval. Takes precedence over <see cref="RequireApproval"/>.</summary>
    public IList<string>? AlwaysRequireApprovalTools { get; set; }

    /// <summary>Tool names that never require approval. Takes precedence over <see cref="RequireApproval"/>.</summary>
    public IList<string>? NeverRequireApprovalTools { get; set; }

    /// <summary>
    /// Optional HTTP headers the provider sends to the MCP server (for example an authorization
    /// token). Not every provider honours them.
    ///
    /// <para>
    /// ⚠️ These values leave your process and are stored by the provider for the duration of the
    /// call. Use a scoped, short-lived token — never a long-lived master key.
    /// </para>
    /// </summary>
    public IDictionary<string, string>? Headers { get; set; }
}
