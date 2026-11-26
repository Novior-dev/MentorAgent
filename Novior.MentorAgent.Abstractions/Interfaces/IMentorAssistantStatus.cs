namespace Novior.MentorAgent.Abstractions.Interfaces;

/// <summary>
/// Whether the application's assistant is on: the chat widget reads it, and while it is off the input and the send
/// button are disabled and a sentence says the assistant was turned off, before anyone writes. An application that
/// turns its assistant off from somewhere else — a console, a switch of its own — registers one; without one the
/// assistant is on.
/// </summary>
/// <remarks>
/// <para>
/// Only on or off: no reason, no author. Off means that the switch refuses the requests to the model, not that a
/// budget is used up: MentorAgent does not refuse them because of this service.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised only when <see cref="IsOn"/> changes, with the service as the sender and
/// <see cref="EventArgs.Empty"/>, and may be raised on a background thread: a Blazor component re-renders through
/// <c>InvokeAsync(StateHasChanged)</c>. A handler reads <see cref="IsOn"/> again and returns at once; one that throws
/// must not stop the service or the other handlers.
/// </para>
/// <para>Register it as a singleton.</para>
/// </remarks>
public interface IMentorAssistantStatus
{
    /// <summary>Whether the assistant is on. <see langword="true"/> unless the application turned it off.</summary>
    bool IsOn { get; }

    /// <summary>Raised when <see cref="IsOn"/> changes, possibly on a background thread.</summary>
    event EventHandler? Changed;
}
