namespace MentorAgentDemo.Domain;

/// <summary>
/// The demo's data: expense claims kept in memory and seeded at startup, shared by every visitor. A restart
/// brings the seed back, so whatever a visitor approves lasts until then.
/// </summary>
public sealed class ExpenseStore
{
    private readonly Lock _gate = new();
    private readonly List<Expense> _expenses = Seed();

    /// <summary>Raised after a claim changes, so an open page can show it.</summary>
    public event Action? Changed;

    public IReadOnlyList<Expense> All()
    {
        lock (_gate) return [.. _expenses.OrderByDescending(e => e.Date).ThenBy(e => e.Id)];
    }

    public Expense? Find(int id)
    {
        lock (_gate) return _expenses.FirstOrDefault(e => e.Id == id);
    }

    /// <summary>Adds a claim for <paramref name="employee"/>, waiting for approval, and returns it.</summary>
    public Expense Submit(string employee, ExpenseCategory category, decimal amount, DateOnly date, string description)
    {
        Expense submitted;
        lock (_gate)
        {
            var id = _expenses.Count == 0 ? 1001 : _expenses.Max(e => e.Id) + 1;
            submitted = new Expense(id, employee, category, decimal.Round(amount, 2), date, description, description);
            _expenses.Add(submitted);
        }

        Changed?.Invoke();
        return submitted;
    }

    /// <summary>Approves a submitted claim; null when there is no such claim or it is no longer submitted.</summary>
    public Expense? Approve(int id, string approver) => Decide(id, approver, ExpenseStatus.Approved);

    /// <summary>Rejects a submitted claim; null when there is no such claim or it is no longer submitted.</summary>
    public Expense? Reject(int id, string approver) => Decide(id, approver, ExpenseStatus.Rejected);

    private Expense? Decide(int id, string approver, ExpenseStatus status)
    {
        Expense decided;
        lock (_gate)
        {
            var index = _expenses.FindIndex(e => e.Id == id);
            if (index < 0 || _expenses[index].Status != ExpenseStatus.Submitted) return null;
            decided = _expenses[index] with { Status = status, DecidedBy = approver };
            _expenses[index] = decided;
        }

        Changed?.Invoke();
        return decided;
    }

    private static List<Expense> Seed()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly Ago(int days) => today.AddDays(-days);

        return
        [
            new(1001, "Giulia Rossi", ExpenseCategory.Travel, 184.60m, Ago(2),
                "Train to Milan for the customer workshop", "Treno per Milano per il workshop con il cliente"),
            new(1002, "Giulia Rossi", ExpenseCategory.Meals, 42.50m, Ago(2),
                "Lunch with the customer's project team", "Pranzo con il team di progetto del cliente"),
            new(1003, "Luca Ferri", ExpenseCategory.Lodging, 268.00m, Ago(5),
                "Two nights in Turin for the trade fair", "Due notti a Torino per la fiera"),
            new(1004, "Luca Ferri", ExpenseCategory.Travel, 96.40m, Ago(5),
                "Fuel and motorway tolls, Turin and back", "Carburante e pedaggi, andata e ritorno da Torino"),
            new(1005, "Anna Greco", ExpenseCategory.Supplies, 59.90m, Ago(8),
                "Headset for remote meetings", "Cuffie per le riunioni da remoto"),
            new(1006, "Anna Greco", ExpenseCategory.Training, 390.00m, Ago(12),
                "Online course on cloud security", "Corso online sulla sicurezza del cloud", ExpenseStatus.Approved, "Marco Bianchi"),
            new(1007, "Paolo Marino", ExpenseCategory.Meals, 128.00m, Ago(15),
                "Team dinner after the release", "Cena del team dopo il rilascio", ExpenseStatus.Rejected, "Marco Bianchi"),
            new(1008, "Paolo Marino", ExpenseCategory.Travel, 412.30m, Ago(1),
                "Flight to Rome for the partner meeting", "Volo per Roma per l'incontro con il partner"),
            new(1009, "Giulia Rossi", ExpenseCategory.Supplies, 24.99m, Ago(20),
                "Notebooks and pens for the workshop", "Quaderni e penne per il workshop", ExpenseStatus.Approved, "Marco Bianchi"),
            new(1010, "Luca Ferri", ExpenseCategory.Meals, 18.70m, Ago(3),
                "Breakfast on the way to the fair", "Colazione andando alla fiera"),
        ];
    }
}
