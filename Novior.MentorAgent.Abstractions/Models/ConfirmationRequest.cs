namespace Novior.MentorAgent.Abstractions.Models;

/// <summary>
/// User confirmation request for actions with RequiresConfirmation = true.
/// Displayed by <c>ConfirmationBanner</c> in the chat widget.
/// </summary>
public sealed record ConfirmationRequest
{
    /// <summary>Unique identifier for this confirmation request.</summary>
    public required Guid ActionId { get; init; }

    /// <summary>Internal tool name (used for lookup; not shown to user).</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Human-readable message shown in the confirmation dialog. Includes the concrete
    /// arguments the AI is about to pass (e.g. "Aggiorna il prezzo di un prodotto —
    /// Product Name: iPad Air · New Price: 800"), so the user can verify exactly what
    /// will change before approving. The full text is also exposed as a tooltip.
    /// </summary>
    public required string Message { get; init; }
}
