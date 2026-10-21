namespace MentorAgentDemo.Domain;

/// <summary>Where an expense claim stands.</summary>
public enum ExpenseStatus
{
    Submitted,
    Approved,
    Rejected,
}

/// <summary>What an expense was for.</summary>
public enum ExpenseCategory
{
    Travel,
    Lodging,
    Meals,
    Supplies,
    Training,
}

/// <summary>
/// One expense claim. The description comes in English and Italian, so the pages show it in the language the
/// visitor picked.
/// </summary>
public sealed record Expense(
    int Id,
    string Employee,
    ExpenseCategory Category,
    decimal Amount,
    DateOnly Date,
    string DescriptionEn,
    string DescriptionIt,
    ExpenseStatus Status = ExpenseStatus.Submitted,
    string? DecidedBy = null)
{
    public string Description(bool italian) => italian ? DescriptionIt : DescriptionEn;
}
