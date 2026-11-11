namespace MentorAgent_Helpdesk.Tickets;

public enum TicketStatus
{
    Open,
    Closed,
}

/// <summary>A support ticket. <see cref="AssignedTo"/> is the support agent working on it, if any.</summary>
public sealed record Ticket(int Id, string Title, string Requester, DateOnly Opened)
{
    public TicketStatus Status { get; init; } = TicketStatus.Open;
    public string? AssignedTo { get; init; }
    public string? Resolution { get; init; }
}
