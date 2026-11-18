namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// One place the application declares actions in, from <see cref="Interfaces.IMentorActionCatalog.GetRegistrations"/>:
/// a class read for its attributes, the actions registered in code for one class, an endpoint declared as an action, a
/// page with the UI actions it declared, and in later versions other sources. Every action of <see cref="Interfaces.IMentorActionCatalog.GetActions"/>
/// names its registration with <see cref="MentorActionInfo.RegistrationId"/>; a page's UI actions are not in that list.
/// More properties may be added.
/// </summary>
public sealed record MentorActionRegistration
{
    /// <summary>
    /// The registration's id, the same from one start to the next: <see cref="Origin"/>, a colon and <see cref="Source"/>
    /// (for example <c>fluent:Shop.OrderActions</c>, <c>endpoint:POST /orders/{id}/cancel</c>, or <c>page:Orders</c>). A
    /// class with actions of both origins has two registrations.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>How the application declares these actions: one of <see cref="MentorActionOrigins"/>, an open code.</summary>
    public required string Origin { get; init; }

    /// <summary>
    /// What declares them: for attributes and for the actions registered in code, the class's full name; for an
    /// endpoint, its verb and its route as mapped (<c>POST /orders/{id}/cancel</c>), one registration per endpoint; for a
    /// command or a query, its type's full name, one registration per type; for a page, the page's name, as the page set
    /// it.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>The page these actions belong to, for a page's registration; <see langword="null"/> otherwise.</summary>
    public string? Page { get; init; }

    /// <summary>
    /// Whether the registration is on. A registration whose source the application switches off as a whole is in the
    /// list with <see langword="false"/>; one action shipped off is still listed in <see cref="Actions"/>, and
    /// <see cref="MentorActionInfo.EnabledByDefault"/> says it is off.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The tool names of the actions it gives, in the order discovery finds them, those shipped off included. For a
    /// page, the names of the UI actions it declared since the application started, in the order first seen, within
    /// <see cref="MentorPageActionLimits"/>: an action the page no longer declares stays in the list.
    /// </summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>What it left out, and by which rule. Empty when everything it declares is an action.</summary>
    public IReadOnlyList<MentorActionRejection> Rejected { get; init; } = [];

    /// <summary>
    /// For a page, the value one counter of the process had when this registration last changed: an action or a
    /// rejection it did not list before. The counter grows only at a change, and the same declaration made again
    /// does not move it, so the highest revision of the list says whether anything changed since it was last read,
    /// and each page's says which. Always 0 for the registrations of the code, which are fixed when the application
    /// starts.
    /// </summary>
    public long Revision { get; init; }
}

/// <summary>Something a <see cref="MentorActionRegistration"/> declares that is not an action, and why.</summary>
public sealed record MentorActionRejection
{
    /// <summary>The name of the method or action left out.</summary>
    public required string Name { get; init; }

    /// <summary>The rule that left it out, by its name in ACTION-RULES.md.</summary>
    public required string Rule { get; init; }
}

/// <summary>
/// How the application declares an action: the codes of <see cref="MentorActionRegistration.Origin"/> and
/// <see cref="MentorActionInfo.Origin"/>. A code stays the same from one version to the next. The list is open: a later
/// version adds codes for other ways of declaring actions, so read an unknown one as it is.
/// </summary>
public static class MentorActionOrigins
{
    /// <summary>A method found in <c>ScanAssemblies</c> by its <c>[MentorAction]</c> or <c>[Description]</c> attribute.</summary>
    public const string Attribute = "attribute";

    /// <summary>A method registered in code, with <c>MentorOptions.Actions</c>.</summary>
    public const string Fluent = "fluent";

    /// <summary>
    /// An endpoint of the application, declared with <c>WithMentorAction</c> on a minimal API or with
    /// <c>[MentorAction]</c> on a controller's action (Novior.MentorAgent.Server). The action runs the endpoint itself,
    /// with its authorization first.
    /// </summary>
    public const string Endpoint = "endpoint";

    /// <summary>
    /// The UI actions a page declared in its page context, in Blazor or from the client's snapshot on the hub. A page's
    /// registration lists their names only, and they are not in <c>IMentorActionCatalog.GetActions()</c>.
    /// </summary>
    public const string Page = "page";

    /// <summary>
    /// A candidate of the AutoMentor: a public method of a service in the perimeter the code declares
    /// (<c>MentorOptions.AutoMentor</c>), with no attribute and no registration. It starts off and asks for a
    /// confirmation; an <c>IMentorActionGovernor</c> may turn it on.
    /// </summary>
    public const string Candidate = "candidate";

    /// <summary>
    /// A command registered in code with <c>MentorOptions.Actions.AddCommand</c>: a type of its own, run by its
    /// <c>IMentorCommandHandler</c>. Its registration's <see cref="MentorActionRegistration.Source"/> is the command's type.
    /// </summary>
    public const string Command = "command";

    /// <summary>
    /// A query registered in code with <c>MentorOptions.Actions.AddQuery</c>: a type of its own, answered by its
    /// <c>IMentorQueryHandler</c>, always read-only. Its registration's <see cref="MentorActionRegistration.Source"/> is the
    /// query's type.
    /// </summary>
    public const string Query = "query";
}
