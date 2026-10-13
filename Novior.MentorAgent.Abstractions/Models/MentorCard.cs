namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// A structured card rendered in the chat instead of being described in prose — Level 2 of
/// generative UI (roadmap #14). Level 1 is Markdown tables and lists; this is real UI with fields,
/// an accent and buttons the user can press.
///
/// <para>
/// <b>A card is built by your tool, never by the model.</b> An <c>[MentorAction]</c> method returns
/// a <see cref="MentorCard"/> (or several) and MentorAgent renders it; the model only decides
/// <i>when</i> to call that tool. This is what makes cards safe to give buttons: every label, URL
/// and action name is written by your code, so a prompt injection cannot make the assistant render
/// a link to somewhere you never approved. It also means a card cannot hallucinate a field.
/// </para>
///
/// <code>
/// [MentorAction("get_order", "Shows an order")]
/// public MentorCard GetOrder(int id)
/// {
///     var o = _db.Orders.Find(id);
///     return new MentorCard("order")
///     {
///         Title    = $"Order #{o.Id}",
///         Subtitle = o.CustomerName,
///         Accent   = o.Status == "Shipped" ? MentorCardAccent.Success : MentorCardAccent.Warning,
///         Fields   = [ new("Total", o.Total.ToString("C")), new("Status", o.Status) ],
///         Actions  = [ new("Open", MentorCardActionKind.Navigate, $"/orders/{o.Id}") ],
///     };
/// }
/// </code>
/// </summary>
/// <param name="Kind">
/// Your own name for this card type — <c>"order"</c>, <c>"product"</c>. The built-in renderer
/// ignores it; a host that supplies a custom template uses it to choose one. Keep it stable, it is
/// effectively the card's contract with the UI.
/// </param>
public sealed record MentorCard(string Kind)
{
    /// <summary>Heading. Rendered as text — never as markup.</summary>
    public string? Title { get; init; }

    /// <summary>Secondary line under the title.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Label/value rows, in display order.</summary>
    public IReadOnlyList<MentorCardField>? Fields { get; init; }

    /// <summary>Buttons. Keep it to two or three — a card is a summary, not a form.</summary>
    public IReadOnlyList<MentorCardAction>? Actions { get; init; }

    /// <summary>Optional image (absolute URL or <c>data:</c> URI).</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Colour accent conveying state at a glance.</summary>
    public MentorCardAccent Accent { get; init; } = MentorCardAccent.Default;
}

/// <summary>One label/value row of a <see cref="MentorCard"/>.</summary>
public sealed record MentorCardField(string Label, string? Value);

/// <summary>A button on a <see cref="MentorCard"/>.</summary>
/// <param name="Label">Button text.</param>
/// <param name="Kind">What pressing it does.</param>
/// <param name="Value">
/// The message to send, the URL to open, or the UI action to run — depending on
/// <paramref name="Kind"/>.
/// </param>
public sealed record MentorCardAction(string Label, MentorCardActionKind Kind, string Value);

/// <summary>What a <see cref="MentorCardAction"/> does when pressed.</summary>
public enum MentorCardActionKind
{
    /// <summary>Sends <c>Value</c> to the assistant as if the user had typed it.</summary>
    SendMessage,

    /// <summary>
    /// Navigates to <c>Value</c>. Because the URL comes from your tool and not from the model, this
    /// is an application route you chose — not somewhere the conversation talked the assistant into.
    /// </summary>
    Navigate,

    /// <summary>Runs the page-registered UI action named <c>Value</c> (see <c>RegisterUIAction</c>).</summary>
    UIAction,
}

/// <summary>Colour accent of a <see cref="MentorCard"/>.</summary>
public enum MentorCardAccent
{
    Default,
    Success,
    Warning,
    Danger,
    Info,
}
