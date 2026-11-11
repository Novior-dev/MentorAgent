namespace MentorAgent_Helpdesk.Tickets;

/// <summary>
/// The tickets, in memory: they start again at every run. Replace it with your database; the actions and the pages
/// only use the methods below.
/// </summary>
public sealed class TicketStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Ticket> _tickets;

    public TicketStore()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        _tickets = new Ticket[]
        {
            new(1, "Printer on the second floor is offline", "Dana", today.AddDays(-6)) { AssignedTo = "Alex" },
            new(2, "Cannot sign in to the VPN", "Chris", today.AddDays(-4)),
            new(3, "New laptop for the new hire", "Dana", today.AddDays(-3)) { AssignedTo = "Alex" },
            new(4, "Shared drive is full", "Sam", today.AddDays(-1)),
            new(5, "Password reset for the payroll app", "Chris", today.AddDays(-9))
            {
                Status = TicketStatus.Closed, AssignedTo = "Alex", Resolution = "Reset and sent by phone.",
            },
        }.ToDictionary(t => t.Id);
    }

    /// <summary>Raised after a ticket changes, so the pages can show it.</summary>
    public event Action? Changed;

    public IReadOnlyList<Ticket> All()
    {
        lock (_gate) return [.. _tickets.Values.OrderBy(t => t.Id)];
    }

    public Ticket? Find(int id)
    {
        lock (_gate) return _tickets.GetValueOrDefault(id);
    }

    public Ticket Assign(int id, string agent) => Update(id, t => t with { AssignedTo = agent });

    public Ticket Close(int id, string resolution)
        => Update(id, t => t with { Status = TicketStatus.Closed, Resolution = resolution });

    private Ticket Update(int id, Func<Ticket, Ticket> change)
    {
        Ticket updated;
        lock (_gate)
        {
            if (!_tickets.TryGetValue(id, out var ticket))
                throw new KeyNotFoundException($"There is no ticket {id}.");
            _tickets[id] = updated = change(ticket);
        }

        Changed?.Invoke();
        return updated;
    }
}
