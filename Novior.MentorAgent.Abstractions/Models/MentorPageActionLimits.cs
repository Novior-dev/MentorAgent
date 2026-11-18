namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// The limits of the trace of the pages' actions, the registrations of origin <see cref="MentorActionOrigins.Page"/> in
/// <see cref="Interfaces.IMentorActionCatalog.GetRegistrations"/>. A page declares its UI actions from the browser, so
/// what it declares is not trusted: the trace keeps only names, and only within these limits. What goes beyond them is
/// in the page's <see cref="MentorActionRegistration.Rejected"/>, by its rule in ACTION-RULES.md. The limits apply to
/// the trace only: the actions a page offers the model are not cut by them.
/// </summary>
public static class MentorPageActionLimits
{
    /// <summary>
    /// The most actions one page lists (<c>page.limit</c>). A page's 65th action and those after it are in its
    /// <see cref="MentorActionRegistration.Rejected"/>.
    /// </summary>
    public const int MaxActionsPerPage = 64;

    /// <summary>
    /// The most pages the trace lists (<c>page.limit</c>). A page that comes after the 200th is not in the trace, and a
    /// warning in the log says so once.
    /// </summary>
    public const int MaxPages = 200;

    /// <summary>
    /// The most things one page lists in its <see cref="MentorActionRegistration.Rejected"/>; past it, what a page
    /// declares beyond the limits is not written down.
    /// </summary>
    public const int MaxRejectedPerPage = 64;

    /// <summary>
    /// The longest action name the trace lists, as a model's tool name (<c>page.name-shape</c>): letters A-Z and a-z,
    /// digits, <c>_</c> and <c>-</c>, from 1 to 64 of them. A name rejected is written in
    /// <see cref="MentorActionRejection.Name"/> cut to this length.
    /// </summary>
    public const int MaxActionNameLength = 64;

    /// <summary>
    /// The longest page name the trace lists (<c>page.name-shape</c>). A page name has from 1 to 128 characters, spaces
    /// included, is not made of white space only, and has no control or format character (Unicode category C, so no
    /// line break or tab either). A page without a name, or with another one, is not in the trace.
    /// </summary>
    public const int MaxPageNameLength = 128;
}
