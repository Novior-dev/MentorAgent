using System.ComponentModel;
using MentorAgent_Helpdesk.Identity;
using Novior.MentorAgent.Attributes;

namespace MentorAgent_Helpdesk.Tickets;

/// <summary>
/// What the assistant can do with the tickets. Every <c>[MentorAction]</c> method is a tool the model can call, named
/// in snake_case: <c>list_open_tickets</c>, <c>assign_ticket</c>, <c>close_ticket</c>. MentorAgent checks
/// <c>RequiredRoles</c> before it offers one, and asks the person to confirm the ones with
/// <c>RequiresConfirmation</c> before they run.
/// </summary>
public sealed class TicketActions(TicketStore store)
{
    [MentorAction(
        Description = "Lists the open tickets, oldest first, with who opened them and who they are assigned to. " +
                      "Show the result as a table.",
        ReadOnly = true)]
    public IReadOnlyList<TicketRow> ListOpenTickets()
        => [.. store.All()
            .Where(t => t.Status == TicketStatus.Open)
            .OrderBy(t => t.Opened)
            .Select(t => new TicketRow(t.Id, t.Title, t.Requester, t.AssignedTo ?? "nobody", t.Opened))];

    [MentorAction(
        Description = "Assigns an open ticket to a support agent.",
        RequiresConfirmation = true)]
    public string AssignTicket(
        [Description("The ticket number, for example 2")] int ticketId,
        [Description("The support agent's name, for example Alex")] string agent)
    {
        if (store.Find(ticketId) is not { } ticket) return $"There is no ticket {ticketId}.";
        if (ticket.Status == TicketStatus.Closed) return $"Ticket {ticketId} is closed: it cannot be assigned.";

        store.Assign(ticketId, agent.Trim());
        return $"Ticket {ticketId} is assigned to {agent.Trim()}.";
    }

    [MentorAction(
        Description = "Closes an open ticket, with a short resolution. Support agents only.",
        RequiresConfirmation = true,
        RequiredRoles = [HelpdeskRoles.Agent])]
    public string CloseTicket(
        [Description("The ticket number, for example 3")] int ticketId,
        [Description("What solved the problem, in one sentence")] string resolution)
    {
        if (store.Find(ticketId) is not { } ticket) return $"There is no ticket {ticketId}.";
        if (ticket.Status == TicketStatus.Closed) return $"Ticket {ticketId} was already closed.";

        store.Close(ticketId, resolution.Trim());
        return $"Ticket {ticketId} is closed.";
    }
}

/// <summary>One row of <see cref="TicketActions.ListOpenTickets"/>.</summary>
public sealed record TicketRow(int Id, string Title, string Requester, string AssignedTo, DateOnly Opened);
