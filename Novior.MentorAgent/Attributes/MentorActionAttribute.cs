namespace Novior.MentorAgent.Attributes;

/// <summary>
/// Advanced Level 1 — Decorates a method to expose it as an AI action with additional metadata.
///
/// For simple cases, the native .NET <c>[Description("...")]</c> is sufficient — zero dependencies.
/// Use <c>[MentorAction]</c> when you need confirmation dialogs, categories, or proactive hints.
///
/// <code>
/// // Simple
/// [MentorAction(Description = "Cancels an existing order")]
/// public async Task&lt;bool&gt; CancelOrder(int orderId, string reason) { }
///
/// // Advanced
/// [MentorAction(
///     Description = "Cancels an existing order",
///     Category = "Orders",
///     RequiresConfirmation = true,
///     ProactiveHint = "Suggest checking active orders first")]
/// public async Task&lt;bool&gt; CancelOrder(int orderId, string reason) { }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class MentorActionAttribute : Attribute
{
    /// <summary>Natural language description used as the AI tool description.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Free-form category, published as the skill's tag on the A2A agent card. It does not change what
    /// the assistant sees or does. Example: "Orders", "Customers", "Reports"
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// If true, the widget shows a confirmation dialog before executing the action.
    /// Use for destructive or irreversible operations.
    /// <para>
    /// Left out, the action follows <see cref="Options.MentorOptions.RequireConfirmationForWrites"/>: on by
    /// default, so an action that is not <see cref="ReadOnly"/> asks for confirmation. Write
    /// <c>RequiresConfirmation = false</c> to opt one action out — the declaration itself is what counts,
    /// which is why leaving it out and writing <c>false</c> are not the same.
    /// </para>
    /// </summary>
    public bool RequiresConfirmation
    {
        get => _requiresConfirmation;
        init
        {
            _requiresConfirmation = value;
            _requiresConfirmationDeclared = true;
        }
    }

    private readonly bool _requiresConfirmation;
    private readonly bool _requiresConfirmationDeclared;

    /// <summary>
    /// Whether <see cref="RequiresConfirmation"/> was written in the attribute, true or false. An attribute
    /// argument cannot be a <c>bool?</c>, so the setter records it.
    /// </summary>
    internal bool RequiresConfirmationDeclared => _requiresConfirmationDeclared;

    /// <summary>
    /// The action only reads: it changes no data. MentorAgent does not check this; it takes your word,
    /// the way an MCP client takes a server's <c>readOnlyHint</c>. It is published to MCP clients as
    /// <c>readOnlyHint</c>, and with <see cref="Options.MentorOptions.RequireLookupForRecordQuestions"/> on
    /// such an action is one the assistant may be required to call before it answers a question about a
    /// value of your records. Ignored when the action also needs confirmation: confirmation is the mark of
    /// an action that changes data, and it wins. To declare actions by name instead — for example those
    /// marked only with <c>[Description]</c> — use <see cref="Options.MentorOptions.IsReadOnlyAction"/>.
    /// Default: false
    /// </summary>
    public bool ReadOnly { get; init; } = false;

    /// <summary>
    /// Guidance for the assistant about what to suggest to the user before or after this action.
    /// Appended to the tool's description as "Guidance: …" — so the model reads it when it considers
    /// the tool — without changing how the tool is matched by semantic filtering.
    /// Example: "Suggest checking active orders first"
    /// </summary>
    public string? ProactiveHint { get; init; }

    /// <summary>
    /// Roles allowed to run the action: the user needs <b>any one</b> of them and must be authenticated. The
    /// user is the caller of the hub, of <c>/mentor/chat</c> or of A2A (Novior.MentorAgent.Server), and in a Blazor
    /// circuit the host's AuthenticationStateProvider's; with neither, nobody can run it. Tested with
    /// <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole"/> — the identity's own
    /// <c>RoleClaimType</c>, so roles in a <c>roles</c> claim (Entra ID) work too; names are
    /// case-sensitive. The same test decides the MCP surface when <c>McpCallerPrincipal</c> is set.
    /// Empty (default) = no role check at all, for signed-in and anonymous users alike.
    /// </summary>
    public string[] RequiredRoles { get; init; } = [];
    /// <summary>
    /// Automatic navigation URL after tool execution (only on success).
    /// MentorAgent navigates automatically if the user is on a different page.
    /// Example: NavigateTo = "/products"
    /// </summary>
    public string? NavigateTo { get; init; }
}