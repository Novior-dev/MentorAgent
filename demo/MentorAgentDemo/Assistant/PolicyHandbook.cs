using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Rag;

namespace MentorAgentDemo.Assistant;

/// <summary>One section of the expense policy, in English and Italian, with the anchor of its place on /policy.</summary>
public sealed record PolicySection(string Anchor, string TitleEn, string TitleIt, string TextEn, string TextIt)
{
    public string Title(bool italian) => italian ? TitleIt : TitleEn;

    public string Text(bool italian) => italian ? TextIt : TextEn;
}

/// <summary>
/// The demo's document source for MentorAgent's RAG (<c>UseRag</c>): the expense policy, a handbook of five short
/// sections. A handbook this small is sent whole, in the assistant's language, on every message; a real source
/// searches its documents and returns the few that match. The answer cites them as [1], [2], and each cited one
/// becomes a chip that opens its section on the policy page.
/// </summary>
public sealed class PolicyHandbook(AssistantLanguage language) : IMentorRagSource
{
    public static IReadOnlyList<PolicySection> Sections { get; } =
    [
        new("travel", "Travel", "Viaggi",
            "Train in second class and economy flights. A taxi is refunded only at night or when no public transport runs. Fuel is refunded at €0.35 per kilometre, tolls in full.",
            "Treno in seconda classe e voli in economy. Il taxi si rimborsa solo di notte o quando non c'è un mezzo pubblico. Il carburante si rimborsa a 0,35 € al chilometro, i pedaggi per intero."),
        new("lodging", "Lodging", "Alloggio",
            "Up to €150 a night, breakfast included. A night above the limit needs the manager's approval before the trip.",
            "Fino a 150 € a notte, colazione compresa. Una notte oltre il limite richiede l'approvazione del responsabile prima del viaggio."),
        new("meals", "Meals", "Pasti",
            "Up to €35 a person for a meal. A meal with a customer is refunded in full when the claim names the customer. Alcohol is never refunded.",
            "Fino a 35 € a persona per un pasto. Un pasto con un cliente si rimborsa per intero se la nota indica il cliente. Gli alcolici non si rimborsano mai."),
        new("supplies", "Supplies and training", "Materiale e formazione",
            "Supplies up to €100 without approval in advance. A course or a certification needs the manager's approval before the purchase.",
            "Materiale fino a 100 € senza approvazione preventiva. Un corso o una certificazione richiedono l'approvazione del responsabile prima dell'acquisto."),
        new("receipts", "Receipts and approval", "Ricevute e approvazione",
            "Every claim needs a receipt and is submitted within 30 days of the expense. A manager approves or rejects it; nobody decides on their own claim.",
            "Ogni nota spese richiede la ricevuta e si invia entro 30 giorni dalla spesa. Un responsabile la approva o la rifiuta; nessuno decide sulla propria nota."),
    ];

    /// <summary>Every section passes: the handbook is sent whole, not searched.</summary>
    public float? DefaultMinScore => 0f;

    public Task<IReadOnlyList<MentorRagResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        IReadOnlyList<MentorRagResult> results =
        [
            .. Sections.Take(maxResults).Select(s => new MentorRagResult(
                Content: s.Text(language.Italian),
                SourceUrl: $"/policy#{s.Anchor}",
                Title: s.Title(language.Italian),
                Score: 1f)),
        ];
        return Task.FromResult(results);
    }

    /// <summary>The whole policy as one text, for the Auditor specialist, which does not see RAG documents.</summary>
    public string AsText()
        => string.Join(Environment.NewLine, Sections.Select(s => $"{s.Title(language.Italian)}: {s.Text(language.Italian)}"));
}
