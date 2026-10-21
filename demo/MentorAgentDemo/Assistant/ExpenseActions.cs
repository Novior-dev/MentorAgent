using System.ComponentModel;
using System.Globalization;
using MentorAgentDemo.Domain;
using MentorAgentDemo.Identity;
using MentorAgentDemo.Localization;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Attributes;

namespace MentorAgentDemo.Assistant;

/// <summary>
/// What the assistant can do with the expense claims. Every <c>[MentorAction]</c> method is a tool the model can
/// call. MentorAgent checks <c>RequiredRoles</c> before running one, and asks the person to confirm the ones that
/// change data (<c>RequiresConfirmation</c>), with the text of <see cref="DemoConfirmation"/>.
/// </summary>
public sealed class ExpenseActions(ExpenseStore store, DemoAuthenticationStateProvider users, AssistantLanguage language)
{
    [MentorAction(
        Description = "Lists the expense claims, newest first. Filter by status (Submitted, Approved or Rejected) " +
                      "and by employee; leave a filter empty for all. Show the result as a table.",
        ReadOnly = true)]
    public IReadOnlyList<ExpenseRow> ListExpenses(
        [Description("Submitted, Approved or Rejected; empty for every status")] string? status = null,
        [Description("Part of the employee's name; empty for everybody")] string? employee = null)
        => [.. store.All()
            .Where(e => string.IsNullOrWhiteSpace(status) || e.Status.ToString().Equals(status.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(e => string.IsNullOrWhiteSpace(employee) || e.Employee.Contains(employee.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(e => ExpenseRow.From(e, language))];

    [MentorAction(
        Description = "Shows one expense claim as a card, with a button to open its page and, while it waits, one to approve it.",
        ReadOnly = true)]
    public object ShowExpense([Description("The claim number, for example 1002")] int expenseId)
    {
        if (store.Find(expenseId) is not { } e)
            return $"There is no expense claim {expenseId}.";

        var actions = new List<MentorCardAction>
        {
            new(language.Pick("Open", "Apri"), MentorCardActionKind.Navigate, $"/expenses/{e.Id}"),
        };
        if (e.Status == ExpenseStatus.Submitted)
            actions.Add(new(language.Pick("Approve", "Approva"), MentorCardActionKind.SendMessage,
                language.Pick($"Approve expense {e.Id}.", $"Approva la nota spese {e.Id}.")));

        return new MentorCard("expense")
        {
            Title = language.Pick($"Expense {e.Id}", $"Nota spese {e.Id}"),
            Subtitle = $"{e.Employee} · {DemoText.Category(e.Category, language.Italian)}",
            Fields =
            [
                new(language.Pick("Amount", "Importo"), e.Amount.ToString("C", language.Culture)),
                new(language.Pick("Date", "Data"), e.Date.ToString("d MMM yyyy", language.Culture)),
                new(language.Pick("Status", "Stato"), DemoText.Status(e.Status, language.Italian)),
                new(language.Pick("Description", "Descrizione"), e.Description(language.Italian)),
            ],
            Actions = actions,
            Accent = e.Status switch
            {
                ExpenseStatus.Approved => MentorCardAccent.Success,
                ExpenseStatus.Rejected => MentorCardAccent.Danger,
                _ => MentorCardAccent.Warning,
            },
        };
    }

    [MentorAction(
        Description = "Approves an expense claim that is waiting. Managers and administrators only, and never their own claim.",
        RequiresConfirmation = true,
        RequiredRoles = [DemoRoles.Manager, DemoRoles.Administrator],
        NavigateTo = "/expenses")]
    public string ApproveExpense([Description("The claim number")] int expenseId)
        => Decide(expenseId, approve: true, reason: null);

    [MentorAction(
        Description = "Rejects an expense claim that is waiting, with the reason. Managers and administrators only, and never their own claim.",
        RequiresConfirmation = true,
        RequiredRoles = [DemoRoles.Manager, DemoRoles.Administrator],
        NavigateTo = "/expenses")]
    public string RejectExpense(
        [Description("The claim number")] int expenseId,
        [Description("Why the claim is rejected, in a short sentence")] string reason)
        => Decide(expenseId, approve: false, reason);

    [MentorAction(
        Description = "Submits a new expense claim for the user, waiting for approval. Use it after the user has agreed " +
                      "with the category, the amount, the date and the description, for example ones read from a receipt.",
        RequiresConfirmation = true,
        NavigateTo = "/expenses")]
    public string SubmitExpense(
        [Description("Travel, Lodging, Meals, Supplies or Training")] string category,
        [Description("The total in euro, for example 18.70")] decimal amount,
        [Description("The date of the expense, as yyyy-MM-dd")] string date,
        [Description("What the expense was for, in a short sentence")] string description)
    {
        if (!Enum.TryParse<ExpenseCategory>(category, ignoreCase: true, out var parsed))
            return $"Unknown category '{category}': use Travel, Lodging, Meals, Supplies or Training.";
        if (amount <= 0)
            return "The amount must be more than zero.";
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return $"The date '{date}' is not a yyyy-MM-dd date.";
        if (day > DateOnly.FromDateTime(DateTime.Today))
            return "The date is in the future.";

        var e = store.Submit(users.Current.Name, parsed, amount, day, description.Trim());
        return $"Expense {e.Id} submitted for {e.Employee}: {e.Amount.ToString("C", language.Culture)}, waiting for approval.";
    }

    private string Decide(int expenseId, bool approve, string? reason)
    {
        if (store.Find(expenseId) is not { } e)
            return $"There is no expense claim {expenseId}.";
        if (e.Status != ExpenseStatus.Submitted)
            return $"Expense {expenseId} is already {e.Status}: nothing was changed.";
        if (e.Employee == users.Current.Name)
            return $"{users.Current.Name} cannot decide on their own claim: another manager has to.";

        var decided = approve ? store.Approve(expenseId, users.Current.Name) : store.Reject(expenseId, users.Current.Name);
        return decided is null
            ? $"Expense {expenseId} could not be changed."
            : approve
                ? $"Expense {decided.Id} approved by {decided.DecidedBy}."
                : $"Expense {decided.Id} rejected by {decided.DecidedBy}: {reason}";
    }
}

/// <summary>One claim as the model reads it. No <c>Kind</c> property: a result with one is drawn as a card.</summary>
public sealed record ExpenseRow(
    int Id, string Employee, string Category, decimal Amount, string Date, string Status, string Description, string? DecidedBy)
{
    public static ExpenseRow From(Expense e, AssistantLanguage language) => new(
        e.Id, e.Employee, e.Category.ToString(), e.Amount, e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        e.Status.ToString(), e.Description(language.Italian), e.DecidedBy);
}
