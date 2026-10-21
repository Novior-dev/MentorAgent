using System.ComponentModel;
using System.Globalization;
using MentorAgentDemo.Domain;
using Novior.MentorAgent.Attributes;

namespace MentorAgentDemo.Assistant;

/// <summary>
/// A level-2 specialist: the coordinator hands it a claim to audit (<c>route_to_specialist</c>), and it answers with a
/// verdict. Its tools only read. Its instructions replace MentorAgent's default ones, so they carry the rule against
/// inventing facts themselves.
/// </summary>
[MentorAgent(
    Name = "Auditor",
    Description = "Audits one expense claim against the expense policy: the limits, the receipt, the 30 days, " +
                  "and the employee's other claims of the same days. Use it when the user asks to check or audit a claim.",
    Instructions =
        "You audit expense claims. Read the claim with get_claim_for_audit and the policy with get_expense_policy, " +
        "then answer with a verdict on the first line, \"OK\" or \"To check\", followed by one line per finding, each " +
        "naming the policy rule it applies. Use only what the tools return: never invent an amount, a date, a rule or " +
        "a claim. If the claim does not exist, say so. You do not approve or reject anything.")]
public sealed class AuditorAgent(ExpenseStore store, PolicyHandbook policy)
{
    [MentorAction(
        Description = "Returns one expense claim, and the same employee's other claims within three days of it.",
        ReadOnly = true)]
    public object GetClaimForAudit([Description("The claim number, for example 1003")] int expenseId)
    {
        if (store.Find(expenseId) is not { } claim)
            return $"There is no expense claim {expenseId}.";

        var nearby = store.All()
            .Where(e => e.Id != claim.Id && e.Employee == claim.Employee
                        && Math.Abs(e.Date.DayNumber - claim.Date.DayNumber) <= 3)
            .Select(Row)
            .ToList();

        return new { Claim = Row(claim), OtherClaimsWithinThreeDays = nearby, Today = Day(DateOnly.FromDateTime(DateTime.Today)) };
    }

    [MentorAction(Description = "Returns the expense policy the claims are audited against.", ReadOnly = true)]
    public string GetExpensePolicy() => policy.AsText();

    private static object Row(Expense e) => new
    {
        e.Id, e.Employee, Category = e.Category.ToString(), e.Amount, Date = Day(e.Date),
        Status = e.Status.ToString(), Description = e.DescriptionEn,
    };

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
