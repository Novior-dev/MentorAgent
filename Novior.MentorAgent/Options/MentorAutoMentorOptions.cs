namespace Novior.MentorAgent.Options;

/// <summary>
/// The AutoMentor's side in the application (<see cref="MentorOptions.AutoMentor"/>): whether MentorAgent looks for
/// methods that could become actions, its candidates, and the perimeter it looks in. The perimeter is the code's
/// only: nothing outside it becomes an action, whatever a service or a document says. A candidate is an action of the
/// coordinator that starts off and asks for a confirmation; an <c>IMentorActionGovernor</c> may turn it on. Read once,
/// at startup. The rules are the <c>candidate</c> area of ACTION-RULES.md.
/// <code>
/// options.AutoMentor.Enabled = true;
/// options.AutoMentor.Namespaces = ["Shop.Services"];
/// </code>
/// </summary>
public sealed class MentorAutoMentorOptions
{
    /// <summary>
    /// Whether MentorAgent looks for candidates. Off by default: without it nothing is a candidate
    /// (<c>candidate.opt-in</c>).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The assemblies the candidates are taken from. Empty, the default, takes <see cref="MentorOptions.ScanAssemblies"/>.
    /// </summary>
    public Assembly[] Scan { get; set; } = [];

    /// <summary>
    /// The perimeter: the namespaces a candidate's class must be in, each with the namespaces under it
    /// (<c>Shop.Services</c> covers <c>Shop.Services.Orders</c>, not <c>Shop.ServicesLegacy</c>). Empty, the default,
    /// gives no candidate, and a warning in the log says so when <see cref="Enabled"/> is on.
    /// </summary>
    public string[] Namespaces { get; set; } = [];

    /// <summary>
    /// The assemblies the pages are read from besides the usual ones. With <see cref="Enabled"/> on, MentorAgent reads
    /// the pages of the application once, from the components that carry <c>@page</c>: those of the entry assembly and
    /// of the assemblies it references that use the components, such as the client project of an Auto application or a
    /// Razor class library given to <c>AddAdditionalAssemblies</c>. Add here an assembly with pages the entry assembly
    /// does not reference. Empty by default.
    /// </summary>
    public Assembly[] PageAssemblies { get; set; } = [];

    /// <summary>
    /// Whether the widget observes the pages the users open, with <see cref="Enabled"/> on: on by default. It sends the
    /// structure of what each page shows, dialogs, tabs and panels included, never a value, never the text of a
    /// structure that repeats, and an element reaches the pages only once more than one user saw it the same. The
    /// observed elements are kept in memory. Off: nothing is observed (<c>page.observe-off</c>).
    /// </summary>
    public bool ObservePages { get; set; } = true;
}
