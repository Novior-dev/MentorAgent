using MentorAgentDemo.Domain;
using MentorAgentDemo.Localization;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Options;

namespace MentorAgentDemo.Assistant;

/// <summary>
/// The text of the confirmation banner (<c>MentorOptions.ConfirmationMessage</c>): one sentence that says what will
/// change, in the assistant's language, with the claim read from the app rather than from the model's arguments.
/// Any other tool keeps MentorAgent's default text (null).
/// </summary>
public static class DemoConfirmation
{
    public static string? Write(MentorConfirmationContext context)
    {
        var italian = context.Language == MentorLanguage.Italian;
        var culture = System.Globalization.CultureInfo.GetCultureInfo(italian ? "it-IT" : "en-IE");
        var store = context.Services.GetService<ExpenseStore>();

        string? Arg(string name) => context.Arguments.FirstOrDefault(a => a.Name == name)?.Value;

        string Claim(int id)
            => store?.Find(id) is { } e
                ? $"{id} — {e.Employee}, {DemoText.Category(e.Category, italian)}, {e.Amount.ToString("C", culture)}"
                : $"{id}";

        switch (context.ToolName)
        {
            case "approve_expense" when int.TryParse(Arg("expenseId"), out var id):
                return italian ? $"Approvare la nota spese {Claim(id)}?" : $"Approve expense {Claim(id)}?";

            case "reject_expense" when int.TryParse(Arg("expenseId"), out var id):
                var reason = Arg("reason");
                return italian
                    ? $"Rifiutare la nota spese {Claim(id)}? Motivo: {reason}"
                    : $"Reject expense {Claim(id)}? Reason: {reason}";

            case "submit_expense":
                var category = Enum.TryParse<ExpenseCategory>(Arg("category"), true, out var c)
                    ? DemoText.Category(c, italian) : Arg("category");
                var amount = decimal.TryParse(Arg("amount"), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var a) ? a.ToString("C", culture) : Arg("amount");
                return italian
                    ? $"Inviare una nuova nota spese? {category}, {amount}, {Arg("date")} — {Arg("description")}"
                    : $"Submit a new expense claim? {category}, {amount}, {Arg("date")} — {Arg("description")}";

            default:
                return null;   // MentorAgent's own text
        }
    }
}
