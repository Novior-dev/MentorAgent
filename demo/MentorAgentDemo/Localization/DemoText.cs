using MentorAgentDemo.Domain;

namespace MentorAgentDemo.Localization;

/// <summary>The texts of the demo's pages, in English and Italian.</summary>
public static class DemoText
{
    private static readonly Dictionary<string, (string En, string It)> Texts = new(StringComparer.Ordinal)
    {
        // Layout
        ["nav.home"] = ("Home", "Home"),
        ["nav.expenses"] = ("Expenses", "Note spese"),
        ["nav.new"] = ("New expense", "Nuova nota spese"),
        ["nav.policy"] = ("Policy", "Policy"),
        ["nav.usage"] = ("Usage", "Consumi"),
        ["header.actingAs"] = ("Acting as", "Stai usando l'app come"),
        ["header.theme"] = ("Switch between light and dark", "Passa dal tema chiaro a quello scuro"),
        ["header.language"] = ("Language of the pages", "Lingua delle pagine"),
        ["footer.note"] = ("A demo of MentorAgent. The people and the expenses are made up; the data resets when the app restarts.",
                           "Una demo di MentorAgent. Persone e spese sono inventate; i dati tornano come all'inizio quando l'app riparte."),
        ["footer.model"] = ("The assistant runs on", "L'assistente usa"),
        ["footer.license"] = ("Code under the Apache License 2.0. \"MentorAgent\" and its logo are trademarks.",
                              "Codice con licenza Apache 2.0. \"MentorAgent\" e il logo sono marchi."),

        // Roles
        ["role.Employee"] = ("Employee", "Dipendente"),
        ["role.Manager"] = ("Manager", "Responsabile"),
        ["role.Administrator"] = ("Administrator", "Amministratore"),

        // Home
        ["home.title"] = ("An assistant that knows your app", "Un assistente che conosce la tua app"),
        ["home.lead"] = ("This is a small expense-claims app with MentorAgent inside. Open the assistant in the corner and ask about the expenses: it reads the app's data, asks before it changes anything, and takes you to the right page.",
                         "Questa è una piccola app di note spese con dentro MentorAgent. Apri l'assistente nell'angolo e chiedi delle spese: legge i dati dell'app, chiede prima di cambiare qualcosa e ti porta alla pagina giusta."),
        ["home.open"] = ("See the expenses", "Vedi le note spese"),
        ["home.try"] = ("Questions to try", "Domande da provare"),
        ["home.q1"] = ("Which expenses are still waiting for approval? Show them in a table.", "Quali note spese aspettano ancora l'approvazione? Mostrale in una tabella."),
        ["home.q2"] = ("Show me expense 1003.", "Mostrami la nota spese 1003."),
        ["home.q3"] = ("Approve expense 1002.", "Approva la nota spese 1002."),
        ["home.q4"] = ("Ask the Auditor to check expense 1003.", "Chiedi all'Auditor di controllare la nota spese 1003."),
        ["home.q5"] = ("How much can I spend on a hotel night?", "Quanto posso spendere per una notte in albergo?"),
        ["home.q6"] = ("Highlight expense 1008 in the table.", "Evidenzia la nota spese 1008 nella tabella."),
        ["home.receipt"] = ("Try a receipt", "Prova una ricevuta"),
        ["home.receiptLead"] = ("Download this made-up receipt, attach it in the assistant with the paper clip and ask it to turn it into a claim.", "Scarica questa ricevuta inventata, allegala nell'assistente con la graffetta e chiedi di trasformarla in una nota spese."),
        ["home.receiptAlt"] = ("A made-up restaurant receipt: total 36.50 euro", "Una ricevuta di ristorante inventata: totale 36,50 euro"),
        ["home.roles"] = ("Switch the role at the top: an employee can look, a manager can approve, an administrator sees everything.",
                          "Cambia ruolo in alto: un dipendente può guardare, un responsabile può approvare, un amministratore vede tutto."),
        ["home.language"] = ("The EN/IT button changes these pages. The assistant answers in the language set in appsettings.json (MentorAgent:Language).",
                             "Il pulsante EN/IT cambia queste pagine. L'assistente risponde nella lingua impostata in appsettings.json (MentorAgent:Language)."),

        // What to try
        ["nav.try"] = ("What to try", "Cosa provare"),
        ["try.title"] = ("What to try", "Cosa provare"),
        ["try.lead"] = ("Every feature of the demo: what it does, a question to ask the assistant, and the lines of the demo's code that turn it on.",
                        "Tutte le funzioni della demo: cosa fanno, una domanda da fare all'assistente e le righe del codice della demo che le attivano."),
        ["try.language"] = ("The EN/IT button changes the demo's pages only. The widget and the assistant use MentorAgent:Language from the configuration, English by default: for an Italian assistant, start the demo with dotnet run -- --MentorAgent:Language=Italian.",
                            "Il pulsante EN/IT cambia solo le pagine della demo. Il widget e l'assistente usano MentorAgent:Language della configurazione, inglese se non è impostato: per un assistente in italiano, avvia la demo con dotnet run -- --MentorAgent:Language=Italian."),
        ["try.ask"] = ("Ask", "Chiedi"),
        ["try.copy"] = ("Copy", "Copia"),
        ["try.copied"] = ("Copied", "Copiata"),
        ["try.open"] = ("Open the page", "Apri la pagina"),

        // The tour
        ["tour.welcome.title"] = ("Welcome to the MentorAgent demo", "Benvenuto nella demo di MentorAgent"),
        ["tour.welcome.body"] = ("A small expense-claims app with an assistant inside. A few steps: what it can do here, with a question to try at each one.",
                                 "Una piccola app di note spese con dentro un assistente. Pochi passi: cosa sa fare qui, con una domanda da provare a ognuno."),
        ["tour.expenses.title"] = ("It reads the app's data", "Legge i dati dell'app"),
        ["tour.expenses.body"] = ("The claims of the team. The assistant reads them through an action of the app, and answers with a table.",
                                  "Le note spese del team. L'assistente le legge con un'azione dell'app e risponde con una tabella."),
        ["tour.approve.title"] = ("It asks before a change", "Chiede prima di cambiare"),
        ["tour.approve.body"] = ("Approving a claim takes a Manager or the Administrator, and runs only after you confirm it in the banner. Switch the role at the top to see both.",
                                 "Per approvare serve un Responsabile o l'Amministratore, e l'azione parte solo dopo la tua conferma nel riquadro. Cambia ruolo in alto per vedere i due casi."),
        ["tour.policy.title"] = ("It cites the expense policy", "Cita la policy delle spese"),
        ["tour.policy.body"] = ("Questions about the rules are answered from the policy, with the section as a chip that opens it.",
                                "Le domande sulle regole hanno la risposta dalla policy, con la sezione in un chip che la apre."),
        ["tour.receipt.title"] = ("It reads a receipt", "Legge una ricevuta"),
        ["tour.receipt.body"] = ("Download the made-up receipt from the home page, attach it with the paper clip on New expense, and ask the assistant to fill in the form.",
                                 "Scarica la ricevuta inventata dalla home, allegala con la graffetta in Nuova nota spese e chiedi all'assistente di compilare il modulo."),
        ["tour.try.title"] = ("Everything, with its code", "Tutto, con il suo codice"),
        ["tour.try.body"] = ("What to try lists every feature of the demo: what it does, a question to copy, and the lines of code that turn it on.",
                             "Cosa provare elenca tutte le funzioni della demo: cosa fanno, una domanda da copiare e le righe di codice che le attivano."),

        // Expenses
        ["expenses.title"] = ("Expense claims", "Note spese"),
        ["expenses.lead"] = ("Every claim of the team, the newest first.", "Tutte le note spese del team, dalla più recente."),
        ["expenses.id"] = ("No.", "N."),
        ["expenses.employee"] = ("Employee", "Dipendente"),
        ["expenses.category"] = ("Category", "Categoria"),
        ["expenses.date"] = ("Date", "Data"),
        ["expenses.amount"] = ("Amount", "Importo"),
        ["expenses.status"] = ("Status", "Stato"),
        ["expenses.description"] = ("Description", "Descrizione"),
        ["expenses.decidedBy"] = ("Decided by", "Decisa da"),
        ["expenses.back"] = ("All expenses", "Tutte le note spese"),
        ["expenses.notFound"] = ("There is no expense claim with this number.", "Non c'è nessuna nota spese con questo numero."),
        ["expenses.approve"] = ("Approve", "Approva"),
        ["expenses.reject"] = ("Reject", "Rifiuta"),
        ["expenses.onlyManagers"] = ("Only a manager can approve or reject a claim.", "Solo un responsabile può approvare o rifiutare una nota spese."),

        // Status and category
        ["status.Submitted"] = ("Waiting", "In attesa"),
        ["status.Approved"] = ("Approved", "Approvata"),
        ["status.Rejected"] = ("Rejected", "Rifiutata"),
        ["category.Travel"] = ("Travel", "Viaggio"),
        ["category.Lodging"] = ("Lodging", "Alloggio"),
        ["category.Meals"] = ("Meals", "Pasti"),
        ["category.Supplies"] = ("Supplies", "Materiale"),
        ["category.Training"] = ("Training", "Formazione"),

        // New expense
        ["new.lead"] = ("Fill in the claim, or ask the assistant to fill it from a receipt, then submit it.", "Compila la nota spese, o chiedi all'assistente di compilarla da una ricevuta, poi inviala."),
        ["new.filled"] = ("The assistant filled the form: check it, then submit.", "L'assistente ha compilato il modulo: controllalo, poi invialo."),
        ["new.submit"] = ("Submit", "Invia"),

        // Policy
        ["policy.title"] = ("Expense policy", "Policy delle spese"),
        ["policy.lead"] = ("The rules the assistant cites when it answers, as [1], [2].", "Le regole che l'assistente cita quando risponde, come [1], [2]."),

        // Usage
        ["usage.title"] = ("Assistant usage", "Consumi dell'assistente"),
        ["usage.lead"] = ("Tokens, cost and actions of this run of the demo, per model.", "Token, costi e azioni di questa esecuzione della demo, per modello."),
        ["usage.onlyAdmin"] = ("Only an administrator sees the usage. Switch the role at the top.", "Solo un amministratore vede i consumi. Cambia ruolo in alto."),
        ["usage.noModel"] = ("The assistant is off, so there is nothing to show yet.", "L'assistente è spento, quindi non c'è ancora niente da mostrare."),

        // Setup
        ["setup.title"] = ("The assistant needs a model", "All'assistente serve un modello"),
        ["setup.lead"] = ("The app runs, but the assistant is off until it has a model to talk to. Set the key in the user secrets of the project, then start the app again:",
                          "L'app gira, ma l'assistente resta spento finché non ha un modello. Imposta la chiave negli user-secrets del progetto e riavvia l'app:"),
        ["setup.missing"] = ("Missing:", "Manca:"),
        ["setup.azure"] = ("For Azure OpenAI, also set the endpoint and the deployment in appsettings.json (Demo:Endpoint, Demo:Model).",
                           "Per Azure OpenAI imposta anche l'endpoint e il deployment in appsettings.json (Demo:Endpoint, Demo:Model)."),
    };

    public static string Get(string key, bool italian)
        => Texts.TryGetValue(key, out var text) ? (italian ? text.It : text.En) : key;

    public static string Status(ExpenseStatus status, bool italian) => Get($"status.{status}", italian);

    public static string Category(ExpenseCategory category, bool italian) => Get($"category.{category}", italian);

    public static string Role(string role, bool italian) => Get($"role.{role}", italian);

    /// <summary>The keys defined in both languages, for the test that keeps them complete.</summary>
    public static IReadOnlyCollection<string> Keys => Texts.Keys;
}
