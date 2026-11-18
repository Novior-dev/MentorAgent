namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// What kind of action a call or an offer is about: <see cref="MentorActionRequest.Kind"/> and
/// <see cref="MentorActionDecision.Kind"/>. A value added in a later version is another kind: read an unknown one as it is.
/// </summary>
public enum MentorActionKind
{
    /// <summary>
    /// An action MentorAgent runs on the application's side: a method or an endpoint of the application, a tool of an
    /// MCP server it consumes, an agent source's.
    /// </summary>
    Server = 0,

    /// <summary>
    /// A UI action the page the user is on declared (<c>IMentorPageContext.RegisterUIAction</c>), which runs in the
    /// page: <see cref="MentorActionRequest.PageName"/> and <see cref="MentorActionDecision.PageName"/> name that page.
    /// </summary>
    UI = 1,
}
