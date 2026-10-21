using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Options;

/// <summary>
/// What <see cref="MentorOptions.ConfirmationMessage"/> gets to write the text of one confirmation banner.
/// </summary>
public sealed class MentorConfirmationContext
{
    /// <summary>The tool about to run, as the model calls it (<c>cancel_order</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The action's description — its <c>[MentorAction]</c> or <c>[Description]</c> text, or the tool name made
    /// readable when it has none.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The arguments the model is about to pass, as the default text shows them: in the order of the method's
    /// parameters, then any it does not declare; each value cleaned of control characters and cut at 200
    /// characters; empty ones left out. An MCP tool's are in the order the model wrote them.
    /// </summary>
    public required IReadOnlyList<MentorConfirmationArgument> Arguments { get; init; }

    /// <summary><see cref="MentorOptions.Language"/>, the language of the widget and of the assistant.</summary>
    public required MentorLanguage Language { get; init; }

    /// <summary>The text MentorAgent shows when the function writes none: the description, then the arguments.</summary>
    public required string DefaultMessage { get; init; }

    /// <summary>
    /// The services of the conversation's scope, for what the text needs: an <c>IStringLocalizer</c>, the
    /// application's own data. Empty when there is no scope to give.
    /// </summary>
    public required IServiceProvider Services { get; init; }
}

/// <summary>One argument of a confirmation banner.</summary>
/// <param name="Name">The parameter's name, as the model sent it (<c>orderId</c>).</param>
/// <param name="Label">The name made readable, as the default text shows it ("Order Id").</param>
/// <param name="Value">The value, cleaned of control characters and cut at 200 characters.</param>
public sealed record MentorConfirmationArgument(string Name, string Label, string Value);
