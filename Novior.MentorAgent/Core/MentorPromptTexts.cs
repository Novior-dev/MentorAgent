namespace Novior.MentorAgent.Core;

/// <summary>
/// The parts of the instructions MentorAgent writes for the model that follow the language of the turn (story 191): in
/// the prompts, the rules the model can echo into a reply and the grounding rules; in the tool results, every
/// instruction MentorAgent adds. They were English in every language, examples included, and a local model copied one
/// into an Italian reply: "Understood, l'email è stata annullata". Since BUG-224 also the notice of a turn that left an
/// action out, which did not hold in Italian.
///
/// <para>
/// The rest stays English, and <see cref="MentorOptions.Language"/> says so: the headings and the other rules of the
/// prompts, the line that names the reply language, the per-request context and the other notices, the classifiers'
/// prompts and the results of a delegation. Tool names, JSON keys and markers such as <c>not_run</c> are the same in every
/// language: the model and the code read them as symbols.
/// </para>
///
/// <para>
/// English is the text the measures were made on (BUG-073, BUG-074, BUG-106), byte for byte as before. The other
/// languages carry the same rules, with the words of <c>MentorLocalizer</c> where the widget already has one for the
/// same thing. A tool result's instruction is written into its JSON as it is, so none of them holds a double quote or
/// a backslash.
/// </para>
/// </summary>
internal sealed class MentorPromptTexts
{
    // ── The coordinator's prompt ─────────────────────────────────────────────────

    /// <summary>The sentence before a confirmation, so the user knows what they are confirming.</summary>
    public required string ConfirmationPreface { get; init; }

    /// <summary>
    /// The reply to a declined confirmation, and why a <c>not_run</c> or <c>outcome_unknown</c> result is not one. Its
    /// English examples are the ones a local model copied into an Italian reply.
    /// </summary>
    public required string DeclinedConfirmation { get; init; }

    /// <summary>
    /// The coordinator's grounding rule (BUG-106). With no action on the turn — the tool filter sent none — the
    /// coordinator stated "normalmente il prezzo varia tra €1800 e €2700" for a product in the catalogue, or offered a
    /// web search for it. Scoped to the application's records: unlike a specialist, the coordinator also answers from
    /// page data, documents and general knowledge, and must keep doing so. Static, so it is part of the cached prompt
    /// prefix.
    ///
    /// <para>
    /// Do not push it to look things up harder without measuring both failure modes. On the rc.14 candidates
    /// (ApiServer, Azure gpt-4.1, same day) the English wording answered "Quanto costa il Dell XPS 15?" with the price
    /// 36 times in 55 (every miss an honest offer to look it up, no figure invented) and the iPad Air stock question 20
    /// of 20. Two stronger wordings were tried: "when one of your tools can read the value, call it and answer: do not
    /// ask the user whether to look it up" (Dell 20/20, stock 10/13 — two wrong figures, one a table saying 0 with no
    /// tool called) and "call it before you reply … never write the value, or a table of values, before the tool has
    /// returned it" (Dell 10/10, stock 6/10 — three invented tables). An offer the user can accept is better than a
    /// figure nobody looked up.
    /// </para>
    /// </summary>
    public required string CoordinatorGrounding { get; init; }

    /// <summary>The communication style's rule on describing an action, with its example.</summary>
    public required string PlainTerms { get; init; }

    /// <summary>The security block's answer to a request outside the configured capabilities.</summary>
    public required string OutOfScope { get; init; }

    /// <summary>Proactive's closing rule without next steps (BUG-074), with its example.</summary>
    public required string ProactiveNoNextSteps { get; init; }

    // ── Specialists and team members ─────────────────────────────────────────────

    /// <summary>
    /// The rule a specialist or a team member answers under. The coordinator has always had one; a specialist had none,
    /// and BUG-079 is what that costs: asked how many customers the instance had, a specialist whose tools can look one
    /// customer up but cannot count them answered "87" (there are 5), and the figure reached the user as a table,
    /// through two coordinators that had no reason to doubt it.
    /// </summary>
    public required string Grounding { get; init; }

    // ── Instructions in tool results ─────────────────────────────────────────────

    /// <summary>What a declined confirmation means: the action did not run, nothing changed (BUG-031, BUG-115).</summary>
    public required string Declined { get; init; }

    /// <summary>What the model does after a declined confirmation (BUG-144).</summary>
    public required string DeclinedRule { get; init; }

    /// <summary>The user pressed Stop while the action waited.</summary>
    public required string Stopped { get; init; }

    /// <summary>Nobody answered the confirmation within <c>ConfirmationTimeout</c>.</summary>
    public required string TimedOut { get; init; }

    /// <summary>The action ran, but <c>OnToolResult</c> threw and its result cannot be handed on (BUG-147).</summary>
    public required string ResultWithheld { get; init; }

    /// <summary>A request an earlier turn left open, which the user had declined (BUG-116).</summary>
    public required string LeftOpenDeclined { get; init; }

    /// <summary>A request an earlier turn left open, waiting for a confirmation when it was stopped (BUG-116).</summary>
    public required string LeftOpenStopped { get; init; }

    /// <summary>A request the user confirmed in an earlier turn that ended before its result was recorded (BUG-116).</summary>
    public required string OutcomeUnknown { get; init; }

    /// <summary>
    /// A confirmation-gated action on a task from another agent. Nobody can answer the confirmation there, so the
    /// action is refused at once instead of waiting until the caller gives up — the agent card leaves such actions out
    /// for the same reason.
    /// </summary>
    public required string NobodyToConfirm { get; init; }

    /// <summary>
    /// The notice of a turn that left out an action its user may not run (BUG-200), under its English heading. It
    /// starts by saying that every tool of the request is available: read live, "some actions are NOT available"
    /// alone made the model refuse a note it had the tool for. English until BUG-224, and in Italian it did not
    /// hold: the model offered an approval it had no tool for.
    /// </summary>
    public required string ActionsNotAvailable { get; init; }

    /// <summary>
    /// The notice's sentence for a user who is not signed in, where signing in may help: only for an action the
    /// model was not given, never after a tool refused or stopped it.
    /// </summary>
    public required string SignInMayHelp { get; init; }

    /// <summary>
    /// What the notice ends with in every language (BUG-224): no offer of an action none of the tools can do, a
    /// reply to an offer does not make it available, and no other tool in its place. In Italian, after "Sì" to an
    /// approval the model had offered, it submitted a copy of the claim with the one tool it had that wrote one,
    /// or wrote that it had approved it.
    /// </summary>
    public required string NotAvailableFollowUp { get; init; }

    /// <summary>
    /// The second pass of a turn whose reply announced or reported an action, and no tool ran (story 526, BUG-220): call
    /// the tool now, or say that it was not done. Never a required call: the model may find that none of its tools does it.
    /// </summary>
    public required string AnnouncedActionNotRun { get; init; }

    /// <summary>
    /// The second pass when the classifier named the tool that does the action reported, and the model was sent it (story
    /// 526): that tool is there to call, now. <c>{tool}</c> is its name. Without it, beside the notice of an action left out
    /// and its sign-in sentence, the model of the live A/B said it could not, and that signing in might help.
    /// </summary>
    public required string AnnouncedActionCall { get; init; }

    /// <summary>
    /// The knowledge-base search of the turn kept no document (story 194, <c>RagNoResultsNotice</c>): a question the
    /// documents would cover is not answered from memory as if a document said it. The search runs on every message, so
    /// the text also says that one needing no document — a greeting, an action, a question the tools answer — is answered
    /// as usual.
    /// </summary>
    public required string RagNothingFound { get; init; }

    /// <summary>The texts for <paramref name="language"/>; a value outside <c>MentorLanguage</c> gets English.</summary>
    public static MentorPromptTexts For(MentorLanguage language) => language switch
    {
        MentorLanguage.Italian    => Italian,
        MentorLanguage.French     => French,
        MentorLanguage.German     => German,
        MentorLanguage.Spanish    => Spanish,
        MentorLanguage.Portuguese => Portuguese,
        MentorLanguage.Dutch      => Dutch,
        MentorLanguage.Polish     => Polish,
        MentorLanguage.Japanese   => Japanese,
        MentorLanguage.Chinese    => Chinese,
        _                         => English,
    };

    internal static readonly MentorPromptTexts English = new()
    {
        ConfirmationPreface =
            "Before triggering any action that requires user confirmation, always write one short sentence describing what you are about to do (e.g. \"I'll cancel order #1005 now.\"), so the user knows what they are confirming.",
        DeclinedConfirmation =
            "When the user cancels or rejects a confirmation dialog, reply with ONE brief friendly sentence (e.g. \"Understood, I won't proceed.\" or \"Sure, I'll leave it as is.\"). Do NOT explain why the action was not taken. Do NOT reference previous requests. Do NOT call tools or suggest alternatives. Stop immediately and wait for the user's next message. A tool result marked not_run or outcome_unknown is not such an answer: it closes an earlier request, so follow its instruction and answer the user's latest message.",
        CoordinatorGrounding =
            "Values of this application's own records (prices, quantities, stock, totals, names, dates, statuses) " +
            "may be stated only as a tool returned them in this conversation, or as they appear in the page data " +
            "or documents given to you. If none of those provides the value, say plainly that you could not look " +
            "it up: never estimate it, never give a typical or market figure in its place, and do not offer a web " +
            "search for it.",
        PlainTerms =
            "When taking an action, describe what you are doing in plain terms (say \"I'll search for your order\" not \"I'll call get_order_status\").",
        OutOfScope =
            "If asked to act outside your defined scope, respond: \"I can only assist with tasks within my configured capabilities.\"",
        ProactiveNoNextSteps = """
            End with the answer. Do not close with suggested next steps, offers or questions such as
              "Do you want me to…?": the user asks for what comes next.
            """,
        Grounding =
            "State only facts a tool returned in this conversation, or that were given to you in it. " +
            "If nothing available to you provides what is asked, say so plainly — never estimate, invent " +
            "or fill in a number, a name, a date or a status.",
        Declined =
            "The user chose not to run this action in the confirmation dialog, so it did not run: nothing was checked and nothing changed.",
        DeclinedRule =
            "Tell the user in one short sentence that you did not proceed. " +
            "Do not retry, do not give or guess any other reason, do not describe the state of any record, " +
            "and do not suggest alternatives.",
        Stopped =
            "The user stopped the request. Do not continue, retry, or comment.",
        TimedOut =
            "Nobody confirmed this action in time, so it did not run: nothing changed. " +
            "Tell the user in one short sentence that it was not done because it was not confirmed in time, and that " +
            "they can ask again. Do not retry, and do not describe the state of any record.",
        ResultWithheld =
            "The action ran, but its result cannot be shown. Do not run it again: " +
            "tell the user it was done and that its details are not available.",
        LeftOpenDeclined =
            "The user declined this action in an earlier request, and it never ran. Do not run it again and do not mention it: answer the user's latest message.",
        LeftOpenStopped =
            "This action waited for confirmation in an earlier request that was stopped, and it never ran. Do not run it again and do not mention it: answer the user's latest message.",
        OutcomeUnknown =
            "The user confirmed this action in an earlier request, " +
            "but that request was interrupted before its result was recorded, so it may or may not have run. " +
            "Do not run it again. Answer the user's latest message; if they ask about this action, say that its " +
            "outcome is not known and suggest checking it in the application.",
        NobodyToConfirm =
            "Not run: this action needs a person to confirm it, and a request from another agent has nobody to " +
            "ask. Say that it has to be done from the application itself.",
        ActionsNotAvailable = """
            Every tool you were given in this request is available to this user: when the user asks for what one of
            them does, call it. Some other actions of this application are NOT available to this user on this turn:
            they are not among your tools, none of them will become available later in this turn, and you have no other
            way to carry them out. Only when the user asks for something none of your tools can do, reply with ONE
            short sentence saying plainly that you cannot do it here. Do not describe a plan, do not say you are about
            to do it, do not promise it.
            NEVER write that an action has been carried out unless a tool you called returned that it was: no "done",
            no "updated", no invented confirmation. Claiming an action you did not perform is a worse failure than
            refusing.
            """,
        SignInMayHelp = """
            The user is not signed in. Only when you reply that something none of your tools can do cannot be done
            here, add that signing in may make it available — never when a tool you called refused or stopped it.
            """,
        NotAvailableFollowUp = """
            Do not offer an action none of your tools can do, and do not ask the user whether to do it. A reply to an
            earlier message of yours — a yes, a choice, a value — does not make such an action available: if you
            offered one, say now, in one short sentence, that you cannot do it here. Never call one of your tools in
            its place: each tool does only what its description says.
            """,
        AnnouncedActionNotRun = """
            Your last reply announced or reported an action, but no tool ran in this turn: nothing was done. If one
            of your tools does what the user asked for, call it now. Otherwise tell the user, in one short sentence,
            that it was not done. Do not announce it again, and do not write that it was done unless a tool you call
            now returns that it was.
            """,
        AnnouncedActionCall = """
            Your last reply announced or reported the action of the tool {tool}, but no tool ran in this turn:
            nothing was done. {tool} is one of your tools and is available to you now: call it now, with what the
            user asked for. Do not write that it cannot be done here or that signing in is needed, and do not write
            that it was done unless {tool} returns that it was.
            """,
        RagNothingFound = """
            The search of the knowledge base found no document for this message. If the user asks about
            something the documents would cover — a policy, a procedure, the details of a product or a service —
            say that you did not find it in the documents, and do not answer from memory as if a document said it.
            A message that needs no document — a greeting, an action, a question about the application's data,
            which your tools answer — is answered as usual.
            """,
    };

    internal static readonly MentorPromptTexts Italian = new()
    {
        ConfirmationPreface =
            "Prima di avviare un'azione che richiede la conferma dell'utente, scrivi sempre una frase breve che dice cosa stai per fare (per esempio \"Annullo subito l'ordine #1005.\"), così l'utente sa che cosa sta confermando.",
        DeclinedConfirmation =
            "Quando l'utente annulla o rifiuta una richiesta di conferma, rispondi con UNA sola frase breve e cordiale (per esempio \"Va bene, non procedo.\" oppure \"D'accordo, lascio tutto com'è.\"). NON spiegare perché l'azione non è stata eseguita. NON fare riferimento a richieste precedenti. NON chiamare strumenti e NON proporre alternative. Fermati subito e aspetta il prossimo messaggio dell'utente. Il risultato di uno strumento marcato not_run o outcome_unknown non è una risposta di questo tipo: chiude una richiesta precedente, quindi segui la sua istruzione e rispondi all'ultimo messaggio dell'utente.",
        CoordinatorGrounding =
            "I valori dei dati di questa applicazione (prezzi, quantità, giacenze, totali, nomi, date, stati) si possono indicare solo come li ha restituiti uno strumento in questa conversazione, o come compaiono nei dati della pagina o nei documenti che ti sono stati dati. Se nessuna di queste fonti fornisce il valore, di' chiaramente che non sei riuscito a recuperarlo: non stimarlo mai, non dare mai al suo posto una cifra tipica o di mercato e non proporre una ricerca sul web per trovarlo.",
        PlainTerms =
            "Quando esegui un'azione, descrivi cosa stai facendo con parole semplici (scrivi \"Cerco il tuo ordine\", non \"Chiamo get_order_status\").",
        OutOfScope =
            "Se ti viene chiesto di agire fuori dal tuo ambito, rispondi: \"Posso aiutarti solo nelle attività previste dalla mia configurazione.\"",
        ProactiveNoNextSteps =
            "Chiudi con la risposta. Non terminare con prossimi passi suggeriti, offerte o domande come \"Vuoi che…?\": è l'utente a chiedere cosa viene dopo.",
        Grounding =
            "Indica solo fatti che uno strumento ha restituito in questa conversazione, o che ti sono stati dati in essa. Se niente di quello che hai a disposizione fornisce quanto richiesto, dillo chiaramente: non stimare, non inventare e non completare mai un numero, un nome, una data o uno stato.",
        Declined =
            "L'utente ha scelto di non eseguire questa azione nella richiesta di conferma, quindi non è stata eseguita: non è stato verificato niente e non è cambiato niente.",
        DeclinedRule =
            "Di' all'utente in una frase breve che non hai proceduto. Non riprovare, non dare né ipotizzare altri motivi, non descrivere lo stato di alcun dato e non proporre alternative.",
        Stopped =
            "L'utente ha fermato la richiesta. Non continuare, non riprovare e non commentare.",
        TimedOut =
            "Nessuno ha confermato questa azione in tempo, quindi non è stata eseguita: non è cambiato niente. Di' all'utente in una frase breve che non è stata fatta perché non è stata confermata in tempo, e che può chiederla di nuovo. Non riprovare e non descrivere lo stato di alcun dato.",
        ResultWithheld =
            "L'azione è stata eseguita, ma il suo risultato non si può mostrare. Non eseguirla di nuovo: di' all'utente che è stata fatta e che i dettagli non sono disponibili.",
        LeftOpenDeclined =
            "L'utente ha rifiutato questa azione in una richiesta precedente, e non è mai stata eseguita. Non eseguirla di nuovo e non parlarne: rispondi all'ultimo messaggio dell'utente.",
        LeftOpenStopped =
            "Questa azione aspettava una conferma in una richiesta precedente che è stata fermata, e non è mai stata eseguita. Non eseguirla di nuovo e non parlarne: rispondi all'ultimo messaggio dell'utente.",
        OutcomeUnknown =
            "L'utente ha confermato questa azione in una richiesta precedente, ma quella richiesta si è interrotta prima che il suo risultato fosse registrato, quindi può essere stata eseguita oppure no. Non eseguirla di nuovo. Rispondi all'ultimo messaggio dell'utente; se chiede di questa azione, di' che il suo esito non è noto e suggerisci di controllarlo nell'applicazione.",
        NobodyToConfirm =
            "Non eseguita: questa azione richiede la conferma di una persona, e in una richiesta di un altro agente non c'è nessuno a cui chiederla. Di' che va fatta dall'applicazione stessa.",
        ActionsNotAvailable = """
            Ogni strumento che ti è stato dato in questa richiesta è disponibile per questo utente: quando l'utente chiede
            quello che uno di essi fa, chiamalo. Altre azioni di questa applicazione NON sono disponibili per questo utente
            in questo turno: non sono tra i tuoi strumenti, nessuna diventerà disponibile più avanti in questo turno e non
            hai altri modi per eseguirle. Solo quando l'utente chiede qualcosa che nessuno dei tuoi strumenti può fare,
            rispondi con UNA sola frase breve che dice chiaramente che qui non puoi farlo. Non descrivere un piano, non dire
            che stai per farlo, non prometterlo.
            NON scrivere MAI che un'azione è stata eseguita se uno strumento che hai chiamato non ha restituito che lo è
            stata: niente "fatto", niente "aggiornato", nessuna conferma inventata. Dichiarare un'azione che non hai
            eseguito è un errore più grave che rifiutare.
            """,
        SignInMayHelp = """
            L'utente non ha effettuato l'accesso. Solo quando rispondi che una cosa non si può fare qui, perché nessuno dei
            tuoi strumenti la fa, aggiungi che con l'accesso potrebbe diventare disponibile — mai quando uno strumento che
            hai chiamato l'ha rifiutata o fermata.
            """,
        NotAvailableFollowUp = """
            Non offrire un'azione che nessuno dei tuoi strumenti può fare e non chiedere all'utente se farla. Una risposta a
            un tuo messaggio precedente — un sì, una scelta, un valore — non rende disponibile un'azione del genere: se
            l'hai offerta, di' ora, in una frase breve, che qui non puoi farla. Non chiamare mai uno dei tuoi strumenti al
            suo posto: ogni strumento fa solo quello che dice la sua descrizione.
            """,
        AnnouncedActionNotRun = """
            La tua ultima risposta ha annunciato o riferito un'azione, ma in questo turno non è stato eseguito
            nessuno strumento: non è stato fatto nulla. Se uno dei tuoi strumenti fa ciò che l'utente ha chiesto,
            chiamalo adesso. Altrimenti di' all'utente, in una frase breve, che non è stato fatto. Non annunciarlo
            di nuovo e non scrivere che è stato fatto, a meno che uno strumento che chiami adesso non restituisca
            che lo è.
            """,
        AnnouncedActionCall = """
            La tua ultima risposta ha annunciato o riferito l'azione dello strumento {tool}, ma in questo turno non
            è stato eseguito nessuno strumento: non è stato fatto nulla. {tool} è uno dei tuoi strumenti ed è
            disponibile adesso: chiamalo adesso, con quello che l'utente ha chiesto. Non scrivere che qui non si può
            fare o che serve l'accesso, e non scrivere che è stato fatto, a meno che {tool} non restituisca che lo
            è.
            """,
        RagNothingFound = """
            La ricerca nella base di conoscenza non ha trovato nessun documento per questo messaggio. Se l'utente
            chiede qualcosa che i documenti tratterebbero — una regola, una procedura, i dettagli di un prodotto o
            di un servizio — di' che non l'hai trovato nei documenti, e non rispondere a memoria come se lo dicesse
            un documento. Un messaggio che non ha bisogno di documenti — un saluto, un'azione, una domanda sui dati
            dell'applicazione, a cui rispondono i tuoi strumenti — si tratta come sempre.
            """,
    };

    internal static readonly MentorPromptTexts French = new()
    {
        ConfirmationPreface =
            "Avant de déclencher une action qui demande la confirmation de l'utilisateur, écris toujours une courte phrase qui décrit ce que tu vas faire (par exemple \"J'annule tout de suite la commande #1005.\"), pour que l'utilisateur sache ce qu'il confirme.",
        DeclinedConfirmation =
            "Quand l'utilisateur annule ou refuse une demande de confirmation, réponds par UNE seule phrase courte et aimable (par exemple \"Entendu, je n'y donne pas suite.\" ou \"D'accord, je laisse tout tel quel.\"). N'explique PAS pourquoi l'action n'a pas été exécutée. Ne fais PAS référence aux demandes précédentes. N'appelle PAS d'outils et ne propose PAS d'alternatives. Arrête-toi immédiatement et attends le prochain message de l'utilisateur. Un résultat d'outil marqué not_run ou outcome_unknown n'est pas une telle réponse : il clôt une demande précédente, donc suis son instruction et réponds au dernier message de l'utilisateur.",
        CoordinatorGrounding =
            "Les valeurs des données de cette application (prix, quantités, stocks, totaux, noms, dates, statuts) ne peuvent être indiquées que telles qu'un outil les a renvoyées dans cette conversation, ou telles qu'elles figurent dans les données de la page ou dans les documents qui t'ont été fournis. Si aucune de ces sources ne fournit la valeur, dis clairement que tu n'as pas pu la consulter : ne l'estime jamais, ne donne jamais à sa place un chiffre typique ou un prix du marché, et ne propose pas de recherche sur le web pour la trouver.",
        PlainTerms =
            "Quand tu effectues une action, décris ce que tu fais en termes simples (écris \"Je recherche votre commande\" et non \"J'appelle get_order_status\").",
        OutOfScope =
            "Si on te demande d'agir en dehors de ton périmètre, réponds : \"Je ne peux vous aider que pour les tâches prévues par ma configuration.\"",
        ProactiveNoNextSteps =
            "Termine par la réponse. Ne conclus pas par des étapes suivantes suggérées, des offres ou des questions comme \"Voulez-vous que je… ?\" : c'est l'utilisateur qui demande la suite.",
        Grounding =
            "N'indique que des faits qu'un outil a renvoyés dans cette conversation, ou qui t'y ont été donnés. Si rien de ce dont tu disposes ne fournit ce qui est demandé, dis-le clairement : n'estime, n'invente et ne complète jamais un nombre, un nom, une date ou un statut.",
        Declined =
            "L'utilisateur a choisi de ne pas exécuter cette action dans la demande de confirmation, elle n'a donc pas été exécutée : rien n'a été vérifié et rien n'a changé.",
        DeclinedRule =
            "Dis à l'utilisateur en une courte phrase que tu n'as pas continué. Ne réessaie pas, ne donne ni ne suppose aucune autre raison, ne décris l'état d'aucune donnée et ne propose pas d'alternatives.",
        Stopped =
            "L'utilisateur a arrêté la demande. Ne continue pas, ne réessaie pas et ne commente pas.",
        TimedOut =
            "Personne n'a confirmé cette action à temps, elle n'a donc pas été exécutée : rien n'a changé. Dis à l'utilisateur en une courte phrase qu'elle n'a pas été faite parce qu'elle n'a pas été confirmée à temps, et qu'il peut la redemander. Ne réessaie pas et ne décris l'état d'aucune donnée.",
        ResultWithheld =
            "L'action a été exécutée, mais son résultat ne peut pas être affiché. Ne l'exécute pas une seconde fois : dis à l'utilisateur qu'elle a été faite et que ses détails ne sont pas disponibles.",
        LeftOpenDeclined =
            "L'utilisateur a refusé cette action dans une demande précédente, et elle n'a jamais été exécutée. Ne l'exécute pas à nouveau et n'en parle pas : réponds au dernier message de l'utilisateur.",
        LeftOpenStopped =
            "Cette action attendait une confirmation dans une demande précédente qui a été arrêtée, et elle n'a jamais été exécutée. Ne l'exécute pas à nouveau et n'en parle pas : réponds au dernier message de l'utilisateur.",
        OutcomeUnknown =
            "L'utilisateur a confirmé cette action dans une demande précédente, mais cette demande a été interrompue avant que son résultat soit enregistré : elle a donc pu être exécutée ou non. Ne l'exécute pas à nouveau. Réponds au dernier message de l'utilisateur ; s'il pose une question sur cette action, dis que son résultat n'est pas connu et suggère de le vérifier dans l'application.",
        NobodyToConfirm =
            "Non exécutée : cette action doit être confirmée par une personne, et une demande venant d'un autre agent n'a personne à qui la demander. Dis qu'elle doit être faite depuis l'application elle-même.",
        ActionsNotAvailable = """
            Chaque outil qui t'a été donné dans cette requête est disponible pour cet utilisateur : quand l'utilisateur
            demande ce que fait l'un d'eux, appelle-le. D'autres actions de cette application ne sont PAS disponibles pour
            cet utilisateur pendant ce tour : elles ne font pas partie de tes outils, aucune ne deviendra disponible plus
            tard dans ce tour et tu n'as aucun autre moyen de les exécuter. Uniquement quand l'utilisateur demande quelque
            chose qu'aucun de tes outils ne peut faire, réponds par UNE seule phrase courte qui dit clairement que tu ne
            peux pas le faire ici. Ne décris pas de plan, ne dis pas que tu vas le faire, ne le promets pas.
            N'écris JAMAIS qu'une action a été effectuée si un outil que tu as appelé n'a pas renvoyé qu'elle l'a été :
            pas de "fait", pas de "mis à jour", aucune confirmation inventée. Affirmer une action que tu n'as pas effectuée
            est une faute plus grave que refuser.
            """,
        SignInMayHelp = """
            L'utilisateur n'est pas connecté. Uniquement quand tu réponds qu'une chose ne peut pas être faite ici, parce
            qu'aucun de tes outils ne la fait, ajoute qu'en se connectant elle pourrait devenir disponible — jamais quand un
            outil que tu as appelé l'a refusée ou arrêtée.
            """,
        NotAvailableFollowUp = """
            Ne propose pas une action qu'aucun de tes outils ne peut faire et ne demande pas à l'utilisateur s'il faut la
            faire. Une réponse à l'un de tes messages précédents — un oui, un choix, une valeur — ne rend pas une telle
            action disponible : si tu l'as proposée, dis maintenant, en une phrase courte, que tu ne peux pas la faire ici.
            N'appelle jamais l'un de tes outils à sa place : chaque outil fait seulement ce que dit sa description.
            """,
        AnnouncedActionNotRun = """
            Ta dernière réponse a annoncé ou rapporté une action, mais aucun outil n'a été exécuté pendant ce tour :
            rien n'a été fait. Si l'un de tes outils fait ce que l'utilisateur a demandé, appelle-le maintenant.
            Sinon, dis à l'utilisateur, en une courte phrase, que cela n'a pas été fait. Ne l'annonce pas de
            nouveau, et n'écris pas que c'est fait, sauf si un outil que tu appelles maintenant renvoie que c'est le
            cas.
            """,
        AnnouncedActionCall = """
            Ta dernière réponse a annoncé ou rapporté l'action de l'outil {tool}, mais aucun outil n'a été exécuté
            pendant ce tour : rien n'a été fait. {tool} est l'un de tes outils et il est disponible maintenant :
            appelle-le maintenant, avec ce que l'utilisateur a demandé. N'écris pas que cela ne peut pas être fait
            ici ou qu'il faut se connecter, et n'écris pas que c'est fait, sauf si {tool} renvoie que c'est le cas.
            """,
        RagNothingFound = """
            La recherche dans la base de connaissances n'a trouvé aucun document pour ce message. Si l'utilisateur
            demande quelque chose que les documents couvriraient — une règle, une procédure, les détails d'un
            produit ou d'un service —, dis que tu ne l'as pas trouvé dans les documents, et ne réponds pas de
            mémoire comme si un document le disait. Un message qui n'a besoin d'aucun document — une salutation,
            une action, une question sur les données de l'application, à laquelle tes outils répondent — se
            traite comme d'habitude.
            """,
    };

    internal static readonly MentorPromptTexts German = new()
    {
        ConfirmationPreface =
            "Bevor du eine Aktion auslöst, die eine Bestätigung des Benutzers erfordert, schreibe immer einen kurzen Satz, der beschreibt, was du gleich tun wirst (zum Beispiel \"Ich storniere jetzt die Bestellung #1005.\"), damit der Benutzer weiß, was er bestätigt.",
        DeclinedConfirmation =
            "Wenn der Benutzer eine Bestätigungsanfrage abbricht oder ablehnt, antworte mit EINEM kurzen, freundlichen Satz (zum Beispiel \"Verstanden, ich fahre nicht fort.\" oder \"In Ordnung, ich lasse alles, wie es ist.\"). Erkläre NICHT, warum die Aktion nicht ausgeführt wurde. Beziehe dich NICHT auf frühere Anfragen. Rufe KEINE Tools auf und schlage KEINE Alternativen vor. Hör sofort auf und warte auf die nächste Nachricht des Benutzers. Ein Tool-Ergebnis mit der Markierung not_run oder outcome_unknown ist keine solche Antwort: Es schließt eine frühere Anfrage ab, also folge seiner Anweisung und beantworte die letzte Nachricht des Benutzers.",
        CoordinatorGrounding =
            "Werte aus den Daten dieser Anwendung (Preise, Mengen, Bestände, Summen, Namen, Datumsangaben, Status) darfst du nur so angeben, wie ein Tool sie in diesem Gespräch zurückgegeben hat oder wie sie in den Seitendaten oder in den dir gegebenen Dokumenten stehen. Wenn keine dieser Quellen den Wert liefert, sage klar, dass du ihn nicht nachschlagen konntest: Schätze ihn nie, nenne nie stattdessen einen typischen Wert oder Marktpreis und biete keine Websuche dafür an.",
        PlainTerms =
            "Wenn du eine Aktion ausführst, beschreibe in einfachen Worten, was du tust (schreibe \"Ich suche Ihre Bestellung\" und nicht \"Ich rufe get_order_status auf\").",
        OutOfScope =
            "Wenn du gebeten wirst, außerhalb deines festgelegten Bereichs zu handeln, antworte: \"Ich kann Ihnen nur bei Aufgaben helfen, für die ich eingerichtet bin.\"",
        ProactiveNoNextSteps =
            "Ende mit der Antwort. Schließe nicht mit vorgeschlagenen nächsten Schritten, Angeboten oder Fragen wie \"Soll ich…?\": Was als Nächstes kommt, fragt der Benutzer selbst.",
        Grounding =
            "Nenne nur Fakten, die ein Tool in diesem Gespräch zurückgegeben hat oder die dir darin gegeben wurden. Wenn nichts, was dir zur Verfügung steht, das Gefragte liefert, sage das klar: Schätze, erfinde oder ergänze nie eine Zahl, einen Namen, ein Datum oder einen Status.",
        Declined =
            "Der Benutzer hat sich in der Bestätigungsanfrage entschieden, diese Aktion nicht auszuführen, daher wurde sie nicht ausgeführt: Es wurde nichts geprüft und nichts geändert.",
        DeclinedRule =
            "Sage dem Benutzer in einem kurzen Satz, dass du nicht fortgefahren bist. Versuche es nicht erneut, nenne oder vermute keinen anderen Grund, beschreibe nicht den Zustand eines Datensatzes und schlage keine Alternativen vor.",
        Stopped =
            "Der Benutzer hat die Anfrage gestoppt. Mache nicht weiter, versuche es nicht erneut und kommentiere nichts.",
        TimedOut =
            "Niemand hat diese Aktion rechtzeitig bestätigt, daher wurde sie nicht ausgeführt: Es hat sich nichts geändert. Sage dem Benutzer in einem kurzen Satz, dass sie nicht ausgeführt wurde, weil sie nicht rechtzeitig bestätigt wurde, und dass er sie erneut anfordern kann. Versuche es nicht erneut und beschreibe nicht den Zustand eines Datensatzes.",
        ResultWithheld =
            "Die Aktion wurde ausgeführt, aber ihr Ergebnis kann nicht angezeigt werden. Führe sie nicht noch einmal aus: Sage dem Benutzer, dass sie erledigt ist und dass ihre Details nicht verfügbar sind.",
        LeftOpenDeclined =
            "Der Benutzer hat diese Aktion in einer früheren Anfrage abgelehnt, und sie wurde nie ausgeführt. Führe sie nicht noch einmal aus und erwähne sie nicht: Beantworte die letzte Nachricht des Benutzers.",
        LeftOpenStopped =
            "Diese Aktion wartete in einer früheren Anfrage, die gestoppt wurde, auf eine Bestätigung, und sie wurde nie ausgeführt. Führe sie nicht noch einmal aus und erwähne sie nicht: Beantworte die letzte Nachricht des Benutzers.",
        OutcomeUnknown =
            "Der Benutzer hat diese Aktion in einer früheren Anfrage bestätigt, aber diese Anfrage wurde unterbrochen, bevor ihr Ergebnis gespeichert wurde, daher wurde sie vielleicht ausgeführt, vielleicht auch nicht. Führe sie nicht noch einmal aus. Beantworte die letzte Nachricht des Benutzers; wenn er nach dieser Aktion fragt, sage, dass ihr Ergebnis nicht bekannt ist, und schlage vor, es in der Anwendung zu prüfen.",
        NobodyToConfirm =
            "Nicht ausgeführt: Diese Aktion muss von einer Person bestätigt werden, und bei einer Anfrage eines anderen Agenten gibt es niemanden, der gefragt werden kann. Sage, dass sie direkt in der Anwendung erledigt werden muss.",
        ActionsNotAvailable = """
            Jedes Tool, das dir in dieser Anfrage gegeben wurde, steht diesem Benutzer zur Verfügung: Wenn der Benutzer um
            das bittet, was eines davon tut, rufe es auf. Andere Aktionen dieser Anwendung stehen diesem Benutzer in dieser
            Runde NICHT zur Verfügung: Sie gehören nicht zu deinen Tools, keine davon wird später in dieser Runde verfügbar,
            und du hast keinen anderen Weg, sie auszuführen. Nur wenn der Benutzer um etwas bittet, das keines deiner Tools
            kann, antworte mit EINEM kurzen Satz, der klar sagt, dass du es hier nicht tun kannst. Beschreibe keinen Plan,
            sage nicht, dass du es gleich tust, und versprich es nicht.
            Schreibe NIE, dass eine Aktion ausgeführt wurde, wenn ein Tool, das du aufgerufen hast, das nicht zurückgegeben
            hat: kein "erledigt", kein "aktualisiert", keine erfundene Bestätigung. Eine Aktion zu behaupten, die du nicht
            ausgeführt hast, ist ein schlimmerer Fehler als abzulehnen.
            """,
        SignInMayHelp = """
            Der Benutzer ist nicht angemeldet. Nur wenn du antwortest, dass etwas hier nicht möglich ist, weil keines deiner
            Tools es kann, füge hinzu, dass es nach einer Anmeldung verfügbar sein könnte — nie, wenn ein Tool, das du
            aufgerufen hast, es abgelehnt oder gestoppt hat.
            """,
        NotAvailableFollowUp = """
            Biete keine Aktion an, die keines deiner Tools kann, und frage den Benutzer nicht, ob du sie ausführen sollst.
            Eine Antwort auf eine frühere Nachricht von dir — ein Ja, eine Auswahl, ein Wert — macht eine solche Aktion
            nicht verfügbar: Wenn du sie angeboten hast, sage jetzt in einem kurzen Satz, dass du sie hier nicht ausführen
            kannst. Rufe nie eines deiner Tools an ihrer Stelle auf: Jedes Tool tut nur, was seine Beschreibung sagt.
            """,
        AnnouncedActionNotRun = """
            Deine letzte Antwort hat eine Aktion angekündigt oder gemeldet, aber in dieser Runde wurde kein Tool
            ausgeführt: Es wurde nichts getan. Wenn eines deiner Tools tut, worum der Benutzer gebeten hat, rufe es
            jetzt auf. Andernfalls sage dem Benutzer in einem kurzen Satz, dass es nicht getan wurde. Kündige es
            nicht erneut an und schreibe nicht, dass es erledigt ist, es sei denn, ein Tool, das du jetzt aufrufst,
            meldet, dass es erledigt wurde.
            """,
        AnnouncedActionCall = """
            Deine letzte Antwort hat die Aktion des Tools {tool} angekündigt oder gemeldet, aber in dieser Runde
            wurde kein Tool ausgeführt: Es wurde nichts getan. {tool} ist eines deiner Tools und steht dir jetzt zur
            Verfügung: Rufe es jetzt auf, mit dem, worum der Benutzer gebeten hat. Schreibe nicht, dass es hier
            nicht möglich ist oder dass eine Anmeldung nötig ist, und schreibe nicht, dass es erledigt ist, es sei
            denn, {tool} meldet, dass es erledigt wurde.
            """,
        RagNothingFound = """
            Die Suche in der Wissensdatenbank hat für diese Nachricht kein Dokument gefunden. Wenn der Benutzer
            nach etwas fragt, das die Dokumente abdecken würden — eine Regel, ein Verfahren, die Einzelheiten eines
            Produkts oder einer Dienstleistung —, sage, dass du es in den Dokumenten nicht gefunden hast, und
            antworte nicht aus dem Gedächtnis, als stünde es in einem Dokument. Eine Nachricht, die kein Dokument
            braucht — eine Begrüßung, eine Aktion, eine Frage zu den Daten der Anwendung, die deine Tools
            beantworten —, wird wie gewohnt beantwortet.
            """,
    };

    internal static readonly MentorPromptTexts Spanish = new()
    {
        ConfirmationPreface =
            "Antes de iniciar una acción que requiere la confirmación del usuario, escribe siempre una frase breve que describa lo que vas a hacer (por ejemplo \"Cancelo ahora el pedido #1005.\"), para que el usuario sepa qué está confirmando.",
        DeclinedConfirmation =
            "Cuando el usuario cancele o rechace una solicitud de confirmación, responde con UNA sola frase breve y amable (por ejemplo \"Entendido, no sigo adelante.\" o \"De acuerdo, lo dejo como está.\"). NO expliques por qué no se realizó la acción. NO hagas referencia a solicitudes anteriores. NO llames a herramientas ni sugieras alternativas. Detente de inmediato y espera el siguiente mensaje del usuario. Un resultado de herramienta marcado como not_run u outcome_unknown no es una respuesta de este tipo: cierra una solicitud anterior, así que sigue su instrucción y responde al último mensaje del usuario.",
        CoordinatorGrounding =
            "Los valores de los datos de esta aplicación (precios, cantidades, existencias, totales, nombres, fechas, estados) solo se pueden indicar tal como los devolvió una herramienta en esta conversación, o tal como aparecen en los datos de la página o en los documentos que se te han dado. Si ninguna de esas fuentes proporciona el valor, di claramente que no pudiste consultarlo: nunca lo estimes, nunca des en su lugar una cifra típica o un precio de mercado y no ofrezcas una búsqueda en la web para encontrarlo.",
        PlainTerms =
            "Cuando realices una acción, describe lo que haces con palabras sencillas (escribe \"Busco tu pedido\", no \"Llamo a get_order_status\").",
        OutOfScope =
            "Si te piden actuar fuera de tu ámbito definido, responde: \"Solo puedo ayudarte con las tareas previstas en mi configuración.\"",
        ProactiveNoNextSteps =
            "Termina con la respuesta. No cierres con próximos pasos sugeridos, ofertas o preguntas como \"¿Quieres que…?\": es el usuario quien pide lo que sigue.",
        Grounding =
            "Indica solo hechos que una herramienta haya devuelto en esta conversación, o que se te hayan dado en ella. Si nada de lo que tienes a tu disposición proporciona lo que se pide, dilo claramente: nunca estimes, inventes ni completes un número, un nombre, una fecha o un estado.",
        Declined =
            "El usuario eligió no ejecutar esta acción en la solicitud de confirmación, así que no se ejecutó: no se comprobó nada y no cambió nada.",
        DeclinedRule =
            "Dile al usuario en una frase breve que no has seguido adelante. No lo vuelvas a intentar, no des ni supongas ningún otro motivo, no describas el estado de ningún registro y no sugieras alternativas.",
        Stopped =
            "El usuario detuvo la solicitud. No continúes, no lo vuelvas a intentar y no comentes nada.",
        TimedOut =
            "Nadie confirmó esta acción a tiempo, así que no se ejecutó: no cambió nada. Dile al usuario en una frase breve que no se hizo porque no se confirmó a tiempo, y que puede volver a pedirla. No lo vuelvas a intentar y no describas el estado de ningún registro.",
        ResultWithheld =
            "La acción se ejecutó, pero su resultado no se puede mostrar. No la vuelvas a ejecutar: dile al usuario que se hizo y que sus detalles no están disponibles.",
        LeftOpenDeclined =
            "El usuario rechazó esta acción en una solicitud anterior, y nunca se ejecutó. No la vuelvas a ejecutar ni la menciones: responde al último mensaje del usuario.",
        LeftOpenStopped =
            "Esta acción esperaba una confirmación en una solicitud anterior que se detuvo, y nunca se ejecutó. No la vuelvas a ejecutar ni la menciones: responde al último mensaje del usuario.",
        OutcomeUnknown =
            "El usuario confirmó esta acción en una solicitud anterior, pero esa solicitud se interrumpió antes de que se registrara su resultado, así que puede que se haya ejecutado o no. No la vuelvas a ejecutar. Responde al último mensaje del usuario; si pregunta por esta acción, dile que su resultado no se conoce y sugiérele que lo compruebe en la aplicación.",
        NobodyToConfirm =
            "No ejecutada: esta acción necesita que una persona la confirme, y en una solicitud de otro agente no hay nadie a quien preguntar. Di que debe hacerse desde la propia aplicación.",
        ActionsNotAvailable = """
            Cada herramienta que se te ha dado en esta solicitud está disponible para este usuario: cuando el usuario pida lo
            que hace una de ellas, llámala. Otras acciones de esta aplicación NO están disponibles para este usuario en este
            turno: no están entre tus herramientas, ninguna estará disponible más adelante en este turno y no tienes otra
            forma de llevarlas a cabo. Solo cuando el usuario pida algo que ninguna de tus herramientas puede hacer, responde
            con UNA sola frase breve que diga claramente que aquí no puedes hacerlo. No describas un plan, no digas que vas a
            hacerlo, no lo prometas.
            NUNCA escribas que una acción se ha realizado si una herramienta que llamaste no devolvió que se realizó: nada de
            "hecho", nada de "actualizado", ninguna confirmación inventada. Afirmar una acción que no realizaste es un error
            más grave que negarse.
            """,
        SignInMayHelp = """
            El usuario no ha iniciado sesión. Solo cuando respondas que algo no se puede hacer aquí, porque ninguna de tus
            herramientas lo hace, añade que al iniciar sesión podría estar disponible — nunca cuando una herramienta que
            llamaste lo rechazó o lo detuvo.
            """,
        NotAvailableFollowUp = """
            No ofrezcas una acción que ninguna de tus herramientas puede hacer y no preguntes al usuario si hacerla. Una
            respuesta a un mensaje tuyo anterior — un sí, una elección, un valor — no hace disponible una acción así: si la
            ofreciste, di ahora, en una frase breve, que aquí no puedes hacerla. Nunca llames a una de tus herramientas en
            su lugar: cada herramienta hace solo lo que dice su descripción.
            """,
        AnnouncedActionNotRun = """
            Tu última respuesta anunció o informó de una acción, pero en este turno no se ejecutó ninguna
            herramienta: no se hizo nada. Si una de tus herramientas hace lo que el usuario pidió, llámala ahora. Si
            no, dile al usuario, en una frase breve, que no se hizo. No lo vuelvas a anunciar y no escribas que se
            hizo, salvo que una herramienta que llames ahora devuelva que se hizo.
            """,
        AnnouncedActionCall = """
            Tu última respuesta anunció o informó de la acción de la herramienta {tool}, pero en este turno no se
            ejecutó ninguna herramienta: no se hizo nada. {tool} es una de tus herramientas y está disponible ahora:
            llámala ahora, con lo que el usuario pidió. No escribas que aquí no se puede hacer o que hace falta
            iniciar sesión, y no escribas que se hizo, salvo que {tool} devuelva que se hizo.
            """,
        RagNothingFound = """
            La búsqueda en la base de conocimiento no encontró ningún documento para este mensaje. Si el usuario
            pregunta por algo que los documentos cubrirían — una norma, un procedimiento, los detalles de un
            producto o de un servicio —, di que no lo encontraste en los documentos, y no respondas de memoria
            como si lo dijera un documento. Un mensaje que no necesita ningún documento — un saludo, una acción,
            una pregunta sobre los datos de la aplicación, que responden tus herramientas — se atiende como
            siempre.
            """,
    };

    internal static readonly MentorPromptTexts Portuguese = new()
    {
        ConfirmationPreface =
            "Antes de iniciar uma ação que exige a confirmação do usuário, escreva sempre uma frase curta descrevendo o que você vai fazer (por exemplo \"Vou cancelar agora o pedido #1005.\"), para que o usuário saiba o que está confirmando.",
        DeclinedConfirmation =
            "Quando o usuário cancelar ou recusar uma solicitação de confirmação, responda com UMA única frase curta e cordial (por exemplo \"Entendido, não vou prosseguir.\" ou \"Certo, vou deixar como está.\"). NÃO explique por que a ação não foi executada. NÃO faça referência a solicitações anteriores. NÃO chame ferramentas nem sugira alternativas. Pare imediatamente e aguarde a próxima mensagem do usuário. Um resultado de ferramenta marcado como not_run ou outcome_unknown não é uma resposta desse tipo: ele encerra uma solicitação anterior, então siga a instrução dele e responda à última mensagem do usuário.",
        CoordinatorGrounding =
            "Os valores dos dados desta aplicação (preços, quantidades, estoque, totais, nomes, datas, status) só podem ser informados como uma ferramenta os retornou nesta conversa, ou como aparecem nos dados da página ou nos documentos fornecidos a você. Se nenhuma dessas fontes fornecer o valor, diga claramente que não conseguiu consultá-lo: nunca o estime, nunca dê no lugar dele um número típico ou um preço de mercado e não ofereça uma pesquisa na web para encontrá-lo.",
        PlainTerms =
            "Ao executar uma ação, descreva o que você está fazendo em termos simples (escreva \"Vou procurar o seu pedido\", não \"Vou chamar get_order_status\").",
        OutOfScope =
            "Se pedirem para você agir fora do seu escopo definido, responda: \"Só posso ajudar com as tarefas previstas na minha configuração.\"",
        ProactiveNoNextSteps =
            "Termine com a resposta. Não feche com próximos passos sugeridos, ofertas ou perguntas como \"Quer que eu…?\": é o usuário quem pede o que vem depois.",
        Grounding =
            "Informe apenas fatos que uma ferramenta retornou nesta conversa, ou que foram dados a você nela. Se nada do que você tem à disposição fornecer o que foi pedido, diga isso claramente: nunca estime, invente ou complete um número, um nome, uma data ou um status.",
        Declined =
            "O usuário escolheu não executar esta ação na solicitação de confirmação, então ela não foi executada: nada foi verificado e nada mudou.",
        DeclinedRule =
            "Diga ao usuário em uma frase curta que você não prosseguiu. Não tente de novo, não dê nem suponha nenhum outro motivo, não descreva o estado de nenhum registro e não sugira alternativas.",
        Stopped =
            "O usuário parou a solicitação. Não continue, não tente de novo e não comente.",
        TimedOut =
            "Ninguém confirmou esta ação a tempo, então ela não foi executada: nada mudou. Diga ao usuário em uma frase curta que ela não foi feita porque não foi confirmada a tempo, e que ele pode pedir de novo. Não tente de novo e não descreva o estado de nenhum registro.",
        ResultWithheld =
            "A ação foi executada, mas o resultado dela não pode ser mostrado. Não a execute de novo: diga ao usuário que ela foi feita e que os detalhes não estão disponíveis.",
        LeftOpenDeclined =
            "O usuário recusou esta ação em uma solicitação anterior, e ela nunca foi executada. Não a execute de novo e não a mencione: responda à última mensagem do usuário.",
        LeftOpenStopped =
            "Esta ação aguardava confirmação em uma solicitação anterior que foi parada, e ela nunca foi executada. Não a execute de novo e não a mencione: responda à última mensagem do usuário.",
        OutcomeUnknown =
            "O usuário confirmou esta ação em uma solicitação anterior, mas essa solicitação foi interrompida antes que o resultado fosse registrado, então ela pode ou não ter sido executada. Não a execute de novo. Responda à última mensagem do usuário; se ele perguntar sobre esta ação, diga que o resultado dela não é conhecido e sugira verificá-lo na aplicação.",
        NobodyToConfirm =
            "Não executada: esta ação precisa ser confirmada por uma pessoa, e em uma solicitação de outro agente não há ninguém a quem perguntar. Diga que ela precisa ser feita na própria aplicação.",
        ActionsNotAvailable = """
            Cada ferramenta que foi dada a você nesta solicitação está disponível para este usuário: quando o usuário pedir o
            que uma delas faz, chame-a. Outras ações desta aplicação NÃO estão disponíveis para este usuário neste turno:
            não estão entre as suas ferramentas, nenhuma ficará disponível mais adiante neste turno e você não tem outra
            forma de executá-las. Somente quando o usuário pedir algo que nenhuma das suas ferramentas pode fazer, responda
            com UMA única frase curta dizendo claramente que você não pode fazer isso aqui. Não descreva um plano, não diga
            que vai fazer, não prometa.
            NUNCA escreva que uma ação foi realizada a menos que uma ferramenta que você chamou tenha retornado que foi: nada
            de "feito", nada de "atualizado", nenhuma confirmação inventada. Afirmar uma ação que você não realizou é uma
            falha pior do que recusar.
            """,
        SignInMayHelp = """
            O usuário não está conectado. Somente quando você responder que algo não pode ser feito aqui, porque nenhuma das
            suas ferramentas faz isso, acrescente que, ao entrar, isso poderá ficar disponível — nunca quando uma ferramenta
            que você chamou o recusou ou interrompeu.
            """,
        NotAvailableFollowUp = """
            Não ofereça uma ação que nenhuma das suas ferramentas pode fazer e não pergunte ao usuário se deve fazê-la. Uma
            resposta a uma mensagem sua anterior — um sim, uma escolha, um valor — não torna essa ação disponível: se você a
            ofereceu, diga agora, em uma frase curta, que não pode fazê-la aqui. Nunca chame uma das suas ferramentas no
            lugar dela: cada ferramenta faz apenas o que diz a sua descrição.
            """,
        AnnouncedActionNotRun = """
            Sua última resposta anunciou ou relatou uma ação, mas nenhuma ferramenta foi executada neste turno: nada
            foi feito. Se uma das suas ferramentas faz o que o usuário pediu, chame-a agora. Caso contrário, diga ao
            usuário, em uma frase curta, que isso não foi feito. Não anuncie isso de novo e não escreva que foi
            feito, a menos que uma ferramenta que você chamar agora retorne que foi.
            """,
        AnnouncedActionCall = """
            Sua última resposta anunciou ou relatou a ação da ferramenta {tool}, mas nenhuma ferramenta foi
            executada neste turno: nada foi feito. {tool} é uma das suas ferramentas e está disponível agora:
            chame-a agora, com o que o usuário pediu. Não escreva que isso não pode ser feito aqui ou que é preciso
            entrar na conta, e não escreva que foi feito, a menos que {tool} retorne que foi.
            """,
        RagNothingFound = """
            A pesquisa na base de conhecimento não encontrou nenhum documento para esta mensagem. Se o usuário
            perguntar sobre algo que os documentos cobririam — uma regra, um procedimento, os detalhes de um
            produto ou de um serviço —, diga que não o encontrou nos documentos e não responda de memória como se
            um documento o dissesse. Uma mensagem que não precisa de nenhum documento — uma saudação, uma ação,
            uma pergunta sobre os dados da aplicação, que as suas ferramentas respondem — é tratada como de
            costume.
            """,
    };

    internal static readonly MentorPromptTexts Dutch = new()
    {
        ConfirmationPreface =
            "Schrijf voordat je een actie start die een bevestiging van de gebruiker vereist altijd één korte zin die beschrijft wat je gaat doen (bijvoorbeeld \"Ik annuleer nu bestelling #1005.\"), zodat de gebruiker weet wat hij bevestigt.",
        DeclinedConfirmation =
            "Als de gebruiker een bevestigingsverzoek annuleert of weigert, antwoord dan met ÉÉN korte, vriendelijke zin (bijvoorbeeld \"Begrepen, ik ga niet verder.\" of \"Prima, ik laat het zoals het is.\"). Leg NIET uit waarom de actie niet is uitgevoerd. Verwijs NIET naar eerdere verzoeken. Roep GEEN tools aan en stel GEEN alternatieven voor. Stop meteen en wacht op het volgende bericht van de gebruiker. Een toolresultaat gemarkeerd met not_run of outcome_unknown is niet zo'n antwoord: het sluit een eerder verzoek af, dus volg de instructie ervan en beantwoord het laatste bericht van de gebruiker.",
        CoordinatorGrounding =
            "Waarden uit de gegevens van deze applicatie (prijzen, aantallen, voorraad, totalen, namen, datums, statussen) mag je alleen noemen zoals een tool ze in dit gesprek heeft teruggegeven, of zoals ze in de paginagegevens of in de documenten staan die je hebt gekregen. Als geen van die bronnen de waarde geeft, zeg dan duidelijk dat je die niet kon opzoeken: schat haar nooit, geef nooit een gangbaar cijfer of een marktprijs in de plaats ervan en bied geen zoekopdracht op het web aan om haar te vinden.",
        PlainTerms =
            "Beschrijf, wanneer je een actie uitvoert, in eenvoudige woorden wat je doet (schrijf \"Ik zoek uw bestelling op\", niet \"Ik roep get_order_status aan\").",
        OutOfScope =
            "Als je gevraagd wordt buiten je vastgestelde taken te handelen, antwoord dan: \"Ik kan u alleen helpen met taken waarvoor ik ben ingesteld.\"",
        ProactiveNoNextSteps =
            "Eindig met het antwoord. Sluit niet af met voorgestelde volgende stappen, aanbiedingen of vragen als \"Wilt u dat ik…?\": de gebruiker vraagt zelf wat er daarna komt.",
        Grounding =
            "Noem alleen feiten die een tool in dit gesprek heeft teruggegeven, of die je daarin zijn gegeven. Als niets wat je tot je beschikking hebt het gevraagde geeft, zeg dat dan duidelijk: schat, verzin of vul nooit een getal, een naam, een datum of een status in.",
        Declined =
            "De gebruiker heeft in het bevestigingsverzoek gekozen deze actie niet uit te voeren, dus is ze niet uitgevoerd: er is niets gecontroleerd en niets veranderd.",
        DeclinedRule =
            "Zeg de gebruiker in één korte zin dat je niet verder bent gegaan. Probeer het niet opnieuw, geef of raad geen andere reden, beschrijf de toestand van geen enkel record en stel geen alternatieven voor.",
        Stopped =
            "De gebruiker heeft het verzoek gestopt. Ga niet verder, probeer het niet opnieuw en geef geen commentaar.",
        TimedOut =
            "Niemand heeft deze actie op tijd bevestigd, dus is ze niet uitgevoerd: er is niets veranderd. Zeg de gebruiker in één korte zin dat ze niet is gedaan omdat ze niet op tijd is bevestigd, en dat hij het opnieuw kan vragen. Probeer het niet opnieuw en beschrijf de toestand van geen enkel record.",
        ResultWithheld =
            "De actie is uitgevoerd, maar het resultaat kan niet worden getoond. Voer ze niet opnieuw uit: zeg de gebruiker dat ze is gedaan en dat de details niet beschikbaar zijn.",
        LeftOpenDeclined =
            "De gebruiker heeft deze actie in een eerder verzoek geweigerd, en ze is nooit uitgevoerd. Voer ze niet opnieuw uit en noem ze niet: beantwoord het laatste bericht van de gebruiker.",
        LeftOpenStopped =
            "Deze actie wachtte op bevestiging in een eerder verzoek dat is gestopt, en ze is nooit uitgevoerd. Voer ze niet opnieuw uit en noem ze niet: beantwoord het laatste bericht van de gebruiker.",
        OutcomeUnknown =
            "De gebruiker heeft deze actie in een eerder verzoek bevestigd, maar dat verzoek werd onderbroken voordat het resultaat werd vastgelegd, dus ze is misschien wel en misschien niet uitgevoerd. Voer ze niet opnieuw uit. Beantwoord het laatste bericht van de gebruiker; als hij naar deze actie vraagt, zeg dan dat de uitkomst niet bekend is en stel voor om die in de applicatie te controleren.",
        NobodyToConfirm =
            "Niet uitgevoerd: deze actie moet door een persoon worden bevestigd, en bij een verzoek van een andere agent is er niemand om het te vragen. Zeg dat het in de applicatie zelf moet gebeuren.",
        ActionsNotAvailable = """
            Elke tool die je in dit verzoek hebt gekregen, is beschikbaar voor deze gebruiker: als de gebruiker vraagt om wat
            een ervan doet, roep die aan. Andere acties van deze applicatie zijn NIET beschikbaar voor deze gebruiker in deze
            beurt: ze horen niet bij je tools, geen ervan wordt later in deze beurt beschikbaar en je hebt geen andere manier
            om ze uit te voeren. Alleen als de gebruiker om iets vraagt dat geen van je tools kan, antwoord je met ÉÉN korte
            zin die duidelijk zegt dat je het hier niet kunt doen. Beschrijf geen plan, zeg niet dat je het gaat doen, beloof
            het niet.
            Schrijf NOOIT dat een actie is uitgevoerd tenzij een tool die je hebt aangeroepen heeft teruggegeven dat dat zo
            is: geen "gedaan", geen "bijgewerkt", geen verzonnen bevestiging. Een actie claimen die je niet hebt uitgevoerd
            is een ergere fout dan weigeren.
            """,
        SignInMayHelp = """
            De gebruiker is niet aangemeld. Alleen als je antwoordt dat iets hier niet kan, omdat geen van je tools het doet,
            voeg je toe dat het na aanmelden beschikbaar kan worden — nooit als een tool die je hebt aangeroepen het heeft
            geweigerd of gestopt.
            """,
        NotAvailableFollowUp = """
            Bied geen actie aan die geen van je tools kan, en vraag de gebruiker niet of je die moet uitvoeren. Een antwoord
            op een eerder bericht van jou — een ja, een keuze, een waarde — maakt zo'n actie niet beschikbaar: als je die hebt
            aangeboden, zeg dan nu in één korte zin dat je die hier niet kunt uitvoeren. Roep nooit een van je tools aan in
            plaats ervan: elke tool doet alleen wat de beschrijving ervan zegt.
            """,
        AnnouncedActionNotRun = """
            Je laatste antwoord kondigde een actie aan of meldde er een, maar in deze beurt is geen tool uitgevoerd:
            er is niets gedaan. Als een van je tools doet wat de gebruiker vroeg, roep die nu aan. Zeg anders tegen
            de gebruiker, in één korte zin, dat het niet is gedaan. Kondig het niet opnieuw aan en schrijf niet dat
            het gedaan is, tenzij een tool die je nu aanroept meldt dat het gedaan is.
            """,
        AnnouncedActionCall = """
            Je laatste antwoord kondigde de actie van de tool {tool} aan of meldde die, maar in deze beurt is geen
            tool uitgevoerd: er is niets gedaan. {tool} is een van je tools en is nu voor je beschikbaar: roep die
            nu aan, met wat de gebruiker vroeg. Schrijf niet dat het hier niet kan of dat inloggen nodig is, en
            schrijf niet dat het gedaan is, tenzij {tool} meldt dat het gedaan is.
            """,
        RagNothingFound = """
            De zoekopdracht in de kennisbank heeft geen document gevonden voor dit bericht. Als de gebruiker vraagt
            naar iets wat de documenten zouden behandelen — een regel, een procedure, de details van een product of
            een dienst — zeg dan dat je het niet in de documenten hebt gevonden, en antwoord niet uit je geheugen
            alsof een document het zei. Een bericht dat geen document nodig heeft — een begroeting, een actie, een
            vraag over de gegevens van de applicatie, die je tools beantwoorden — behandel je zoals altijd.
            """,
    };

    internal static readonly MentorPromptTexts Polish = new()
    {
        ConfirmationPreface =
            "Zanim uruchomisz akcję, która wymaga potwierdzenia użytkownika, zawsze napisz jedno krótkie zdanie opisujące, co zamierzasz zrobić (na przykład \"Anuluję teraz zamówienie #1005.\"), aby użytkownik wiedział, co potwierdza.",
        DeclinedConfirmation =
            "Gdy użytkownik anuluje lub odrzuci prośbę o potwierdzenie, odpowiedz JEDNYM krótkim, uprzejmym zdaniem (na przykład \"Rozumiem, nie kontynuuję.\" lub \"Dobrze, zostawiam wszystko bez zmian.\"). NIE wyjaśniaj, dlaczego akcja nie została wykonana. NIE nawiązuj do wcześniejszych próśb. NIE wywołuj narzędzi i NIE proponuj alternatyw. Zatrzymaj się od razu i poczekaj na następną wiadomość użytkownika. Wynik narzędzia oznaczony jako not_run lub outcome_unknown nie jest taką odpowiedzią: zamyka wcześniejszą prośbę, więc wykonaj jego instrukcję i odpowiedz na ostatnią wiadomość użytkownika.",
        CoordinatorGrounding =
            "Wartości z danych tej aplikacji (ceny, ilości, stany magazynowe, sumy, nazwy, daty, statusy) możesz podawać tylko tak, jak zwróciło je narzędzie w tej rozmowie, albo tak, jak występują w danych strony lub w przekazanych ci dokumentach. Jeśli żadne z tych źródeł nie podaje wartości, powiedz wprost, że nie udało ci się jej sprawdzić: nigdy jej nie szacuj, nigdy nie podawaj w jej miejsce typowej kwoty ani ceny rynkowej i nie proponuj wyszukiwania w internecie, aby ją znaleźć.",
        PlainTerms =
            "Wykonując akcję, opisz prostymi słowami, co robisz (napisz \"Szukam twojego zamówienia\", a nie \"Wywołuję get_order_status\").",
        OutOfScope =
            "Jeśli ktoś poprosi cię o działanie poza twoim zakresem, odpowiedz: \"Mogę pomóc tylko w zadaniach przewidzianych w mojej konfiguracji.\"",
        ProactiveNoNextSteps =
            "Zakończ odpowiedzią. Nie kończ sugerowanymi kolejnymi krokami, propozycjami ani pytaniami w rodzaju \"Czy chcesz, żebym…?\": to użytkownik prosi o to, co dalej.",
        Grounding =
            "Podawaj tylko fakty, które zwróciło narzędzie w tej rozmowie lub które zostały ci w niej przekazane. Jeśli nic z tego, czym dysponujesz, nie daje tego, o co proszono, powiedz to wprost: nigdy nie szacuj, nie wymyślaj ani nie uzupełniaj liczby, nazwy, daty ani statusu.",
        Declined =
            "Użytkownik zdecydował w prośbie o potwierdzenie, że ta akcja ma nie być wykonana, więc nie została wykonana: nic nie zostało sprawdzone i nic się nie zmieniło.",
        DeclinedRule =
            "Powiedz użytkownikowi jednym krótkim zdaniem, że nie kontynuowałeś. Nie próbuj ponownie, nie podawaj ani nie zgaduj żadnego innego powodu, nie opisuj stanu żadnego rekordu i nie proponuj alternatyw.",
        Stopped =
            "Użytkownik zatrzymał prośbę. Nie kontynuuj, nie próbuj ponownie i nie komentuj.",
        TimedOut =
            "Nikt nie potwierdził tej akcji na czas, więc nie została wykonana: nic się nie zmieniło. Powiedz użytkownikowi jednym krótkim zdaniem, że nie została wykonana, bo nie została potwierdzona na czas, i że może poprosić o nią ponownie. Nie próbuj ponownie i nie opisuj stanu żadnego rekordu.",
        ResultWithheld =
            "Akcja została wykonana, ale jej wyniku nie można pokazać. Nie wykonuj jej ponownie: powiedz użytkownikowi, że została wykonana i że jej szczegóły nie są dostępne.",
        LeftOpenDeclined =
            "Użytkownik odrzucił tę akcję we wcześniejszej prośbie i nigdy nie została wykonana. Nie wykonuj jej ponownie i nie wspominaj o niej: odpowiedz na ostatnią wiadomość użytkownika.",
        LeftOpenStopped =
            "Ta akcja czekała na potwierdzenie we wcześniejszej prośbie, która została zatrzymana, i nigdy nie została wykonana. Nie wykonuj jej ponownie i nie wspominaj o niej: odpowiedz na ostatnią wiadomość użytkownika.",
        OutcomeUnknown =
            "Użytkownik potwierdził tę akcję we wcześniejszej prośbie, ale ta prośba została przerwana, zanim zapisano jej wynik, więc akcja mogła zostać wykonana albo nie. Nie wykonuj jej ponownie. Odpowiedz na ostatnią wiadomość użytkownika; jeśli zapyta o tę akcję, powiedz, że jej wynik nie jest znany, i zaproponuj sprawdzenie go w aplikacji.",
        NobodyToConfirm =
            "Nie wykonano: ta akcja wymaga potwierdzenia przez osobę, a w prośbie od innego agenta nie ma kogo zapytać. Powiedz, że trzeba to zrobić bezpośrednio w aplikacji.",
        ActionsNotAvailable = """
            Każde narzędzie przekazane ci w tym żądaniu jest dostępne dla tego użytkownika: gdy użytkownik prosi o to, co
            robi jedno z nich, wywołaj je. Inne akcje tej aplikacji NIE są dostępne dla tego użytkownika w tej turze: nie ma
            ich wśród twoich narzędzi, żadna nie stanie się dostępna później w tej turze i nie masz innego sposobu, by je
            wykonać. Tylko gdy użytkownik prosi o coś, czego nie potrafi żadne z twoich narzędzi, odpowiedz JEDNYM krótkim
            zdaniem, które jasno mówi, że nie możesz tego tutaj zrobić. Nie opisuj planu, nie mów, że zaraz to zrobisz, nie
            obiecuj tego.
            NIGDY nie pisz, że akcja została wykonana, jeśli wywołane przez ciebie narzędzie nie zwróciło, że została:
            żadnego "zrobione", żadnego "zaktualizowano", żadnego wymyślonego potwierdzenia. Ogłoszenie akcji, której nie
            wykonano, jest gorszym błędem niż odmowa.
            """,
        SignInMayHelp = """
            Użytkownik nie jest zalogowany. Tylko gdy odpowiadasz, że czegoś nie da się tutaj zrobić, bo nie robi tego żadne
            z twoich narzędzi, dodaj, że po zalogowaniu może to stać się dostępne — nigdy, gdy wywołane przez ciebie
            narzędzie to odrzuciło lub zatrzymało.
            """,
        NotAvailableFollowUp = """
            Nie proponuj akcji, której nie potrafi żadne z twoich narzędzi, i nie pytaj użytkownika, czy ją wykonać.
            Odpowiedź na twoją wcześniejszą wiadomość — tak, wybór, wartość — nie sprawia, że taka akcja staje się dostępna:
            jeśli ją zaproponowano, powiedz teraz, jednym krótkim zdaniem, że nie możesz jej tutaj wykonać. Nigdy nie wywołuj
            żadnego ze swoich narzędzi zamiast niej: każde narzędzie robi tylko to, co mówi jego opis.
            """,
        AnnouncedActionNotRun = """
            Twoja ostatnia odpowiedź zapowiedziała lub zgłosiła akcję, ale w tej turze nie uruchomiono żadnego
            narzędzia: nic nie zostało zrobione. Jeśli jedno z twoich narzędzi robi to, o co prosił użytkownik,
            wywołaj je teraz. W przeciwnym razie powiedz użytkownikowi jednym krótkim zdaniem, że nie zostało to
            zrobione. Nie zapowiadaj tego ponownie i nie pisz, że zostało to zrobione, chyba że narzędzie, które
            teraz wywołasz, zwróci, że tak.
            """,
        AnnouncedActionCall = """
            Twoja ostatnia odpowiedź zapowiedziała lub zgłosiła akcję narzędzia {tool}, ale w tej turze nie
            uruchomiono żadnego narzędzia: nic nie zostało zrobione. {tool} jest jednym z twoich narzędzi i jest
            teraz dostępne: wywołaj je teraz, z tym, o co prosił użytkownik. Nie pisz, że nie da się tego zrobić
            tutaj ani że trzeba się zalogować, i nie pisz, że zostało to zrobione, chyba że {tool} zwróci, że tak.
            """,
        RagNothingFound = """
            Wyszukiwanie w bazie wiedzy nie znalazło żadnego dokumentu dla tej wiadomości. Jeśli użytkownik pyta
            o coś, co obejmowałyby dokumenty — zasadę, procedurę, szczegóły produktu lub usługi — powiedz, że nie
            znalazłeś tego w dokumentach, i nie odpowiadaj z pamięci, jakby mówił to dokument. Wiadomość, która nie
            wymaga żadnego dokumentu — powitanie, działanie, pytanie o dane aplikacji, na które odpowiadają twoje
            narzędzia — traktuj jak zwykle.
            """,
    };

    internal static readonly MentorPromptTexts Japanese = new()
    {
        ConfirmationPreface =
            "ユーザーの確認が必要な操作を実行する前に、これから何をするかを短い一文で必ず書いてください（例：「注文 #1005 を今すぐキャンセルします。」）。ユーザーが何を確認しているのか分かるようにするためです。",
        DeclinedConfirmation =
            "ユーザーが確認をキャンセルまたは拒否した場合は、短く丁寧な一文だけで返答してください（例：「承知しました。実行しません。」または「かしこまりました。このままにしておきます。」）。操作を実行しなかった理由は説明しないでください。以前のリクエストには触れないでください。ツールを呼び出したり、代替案を提案したりしないでください。すぐに止めて、ユーザーの次のメッセージを待ってください。not_run または outcome_unknown と記されたツールの結果は、このような返答には当たりません。それは以前のリクエストを締めくくるものなので、その指示に従い、ユーザーの最新のメッセージに答えてください。",
        CoordinatorGrounding =
            "このアプリケーションのデータの値（価格、数量、在庫、合計、名前、日付、ステータス）は、この会話でツールが返したとおり、またはページのデータや与えられたドキュメントに記載されているとおりにのみ伝えてください。そのいずれからも値が得られない場合は、調べられなかったとはっきり伝えてください。値を推測せず、代わりに一般的な数値や相場を示さず、そのためのウェブ検索も提案しないでください。",
        PlainTerms =
            "操作を行うときは、何をしているかを分かりやすい言葉で説明してください（「get_order_status を呼び出します」ではなく「ご注文を検索します」と書いてください）。",
        OutOfScope =
            "定められた範囲外の行動を求められた場合は、「設定された機能の範囲内のタスクについてのみお手伝いできます。」と答えてください。",
        ProactiveNoNextSteps =
            "回答で締めくくってください。提案する次のステップ、申し出、「〜しましょうか？」のような質問で終わらせないでください。次に何をするかはユーザーが求めます。",
        Grounding =
            "この会話でツールが返した事実、またはこの会話の中で与えられた事実だけを述べてください。利用できるもので求められた内容が得られない場合は、はっきりそう伝えてください。数値、名前、日付、ステータスを推測したり、作り上げたり、補ったりしないでください。",
        Declined =
            "ユーザーは確認でこの操作を実行しないことを選んだため、操作は実行されていません。何も確認されておらず、何も変更されていません。",
        DeclinedRule =
            "実行しなかったことを短い一文でユーザーに伝えてください。再試行せず、ほかの理由を示したり推測したりせず、どのレコードの状態も説明せず、代替案も提案しないでください。",
        Stopped =
            "ユーザーがリクエストを停止しました。続行、再試行、コメントはしないでください。",
        TimedOut =
            "時間内に誰もこの操作を確認しなかったため、実行されていません。何も変更されていません。時間内に確認されなかったため実行されなかったこと、もう一度依頼できることを、短い一文でユーザーに伝えてください。再試行せず、どのレコードの状態も説明しないでください。",
        ResultWithheld =
            "操作は実行されましたが、その結果は表示できません。もう一度実行しないでください。完了したこと、詳細は表示できないことをユーザーに伝えてください。",
        LeftOpenDeclined =
            "ユーザーは以前のリクエストでこの操作を拒否しており、操作は一度も実行されていません。もう一度実行せず、この操作には触れずに、ユーザーの最新のメッセージに答えてください。",
        LeftOpenStopped =
            "この操作は、停止された以前のリクエストで確認を待っていたもので、一度も実行されていません。もう一度実行せず、この操作には触れずに、ユーザーの最新のメッセージに答えてください。",
        OutcomeUnknown =
            "ユーザーは以前のリクエストでこの操作を確認しましたが、その結果が記録される前にリクエストが中断されたため、実行されたかどうかは分かりません。もう一度実行しないでください。ユーザーの最新のメッセージに答えてください。この操作について聞かれた場合は、結果が分からないことを伝え、アプリケーションで確認するよう勧めてください。",
        NobodyToConfirm =
            "未実行：この操作には人による確認が必要ですが、別のエージェントからのリクエストには確認できる人がいません。アプリケーション上で直接行う必要があると伝えてください。",
        ActionsNotAvailable = """
            このリクエストで渡されたツールは、どれもこのユーザーが利用できます。ユーザーがそのいずれかでできることを求めたら、そのツールを呼び出してください。このアプリケーションのほかの操作は、このターンではこのユーザーには利用できません。それらはあなたのツールに含まれておらず、このターンの後になって利用できるようになることもなく、ほかに実行する方法もありません。どのツールでもできないことをユーザーが求めたときに限り、ここではできないことをはっきり伝える短い文を一つだけ返してください。計画を説明したり、これから行うと言ったり、約束したりしないでください。
            呼び出したツールが実行されたと返していない限り、操作が実行されたと決して書かないでください。「完了しました」「更新しました」も、作り話の確認もいけません。実行していない操作を実行したと言うのは、断るよりも重大な誤りです。
            """,
        SignInMayHelp = """
            ユーザーはサインインしていません。どのツールでもできないためにここではできないと答えるときに限り、サインインすると利用できるようになる可能性があることを添えてください。呼び出したツールが拒否または停止したときには、決して添えないでください。
            """,
        NotAvailableFollowUp = """
            どのツールでもできない操作を提案せず、それを行うかどうかをユーザーに尋ねないでください。あなたの前のメッセージへの返事（「はい」、選択、値）は、そのような操作を利用できるようにはしません。提案してしまった場合は、ここではできないことを今、短い文で伝えてください。その代わりにほかのツールを呼び出すことは決してしないでください。各ツールは、その説明にあることしか行いません。
            """,
        AnnouncedActionNotRun = """
            あなたの直前の返答は操作を予告または報告しましたが、このターンではどのツールも実行されていません。つまり、何も行われていません。ユーザーが求めたことをあなたのツールのどれかが行えるなら、今それを呼び出してください。そうでなければ、行われていないことを、短い一文でユーザーに伝えてください。もう一度予告しないでください。また、今呼び出したツールが完了したと返さない限り、完了したとは書かないでください。
            """,
        AnnouncedActionCall = """
            あなたの直前の返答はツール {tool} の操作を予告または報告しましたが、このターンではどのツールも実行されていません。つまり、何も行われていません。{tool} はあなたのツールの一つで、今使えます。ユーザーが求めた内容で、今それを呼び出してください。ここではできない、またはサインインが必要だとは書かないでください。また、{tool} が完了したと返さない限り、完了したとは書かないでください。
            """,
        RagNothingFound = """
            ナレッジベースの検索では、このメッセージに該当する文書は見つかりませんでした。ユーザーが文書で扱われるはずの内容
            （規則、手順、製品やサービスの詳細など）について尋ねている場合は、文書の中に見つからなかったと伝え、文書に
            書かれているかのように記憶から答えないでください。文書を必要としないメッセージ（あいさつ、操作の依頼、ツールが
            答えるアプリケーションのデータに関する質問など）には、いつもどおりに対応してください。
            """,
    };

    internal static readonly MentorPromptTexts Chinese = new()
    {
        ConfirmationPreface =
            "在触发任何需要用户确认的操作之前，务必先写一句简短的话，说明你将要做什么（例如“我现在就取消订单 #1005。”），让用户知道自己在确认什么。",
        DeclinedConfirmation =
            "当用户取消或拒绝确认请求时，只用一句简短友好的话回复（例如“好的，我不会继续。”或“没问题，就保持原样。”）。不要解释为什么没有执行该操作。不要提及之前的请求。不要调用工具，也不要建议替代方案。立即停止，等待用户的下一条消息。标记为 not_run 或 outcome_unknown 的工具结果不属于这种回答：它结束的是之前的请求，因此请按照其中的指示，回答用户最新的消息。",
        CoordinatorGrounding =
            "本应用数据中的值（价格、数量、库存、总计、名称、日期、状态）只能按照工具在本次对话中返回的内容，或页面数据、提供给你的文档中出现的内容来陈述。如果这些来源都没有提供该值，请直接说明你无法查到：绝不要估算，绝不要用典型数值或市场价格代替，也不要为此提议进行网络搜索。",
        PlainTerms =
            "执行操作时，用通俗的语言描述你在做什么（写“我来查找您的订单”，而不是“我来调用 get_order_status”）。",
        OutOfScope =
            "如果被要求在你的设定范围之外行事，请回答：“我只能协助处理已配置功能范围内的任务。”",
        ProactiveNoNextSteps =
            "以答案结尾。不要用建议的后续步骤、主动提议或“需要我……吗？”之类的问题来收尾：接下来做什么由用户提出。",
        Grounding =
            "只陈述工具在本次对话中返回的事实，或在对话中提供给你的事实。如果你可用的任何内容都无法提供所问的信息，请直接说明：绝不要估算、编造或填补数字、名称、日期或状态。",
        Declined =
            "用户在确认请求中选择不执行此操作，因此它没有被执行：没有检查任何内容，也没有任何更改。",
        DeclinedRule =
            "用一句简短的话告诉用户你没有继续。不要重试，不要给出或猜测任何其他原因，不要描述任何记录的状态，也不要建议替代方案。",
        Stopped =
            "用户停止了该请求。不要继续、重试或评论。",
        TimedOut =
            "没有人及时确认此操作，因此它没有被执行：没有任何更改。用一句简短的话告诉用户，由于没有及时确认，该操作没有执行，并且可以重新提出请求。不要重试，也不要描述任何记录的状态。",
        ResultWithheld =
            "该操作已执行，但其结果无法显示。不要再次执行：告诉用户操作已完成，但详细信息不可用。",
        LeftOpenDeclined =
            "用户在之前的请求中拒绝了此操作，它从未被执行。不要再次执行，也不要提及：回答用户最新的消息。",
        LeftOpenStopped =
            "此操作在之前一个已被停止的请求中等待确认，它从未被执行。不要再次执行，也不要提及：回答用户最新的消息。",
        OutcomeUnknown =
            "用户在之前的请求中确认了此操作，但该请求在结果记录之前被中断，因此它可能已执行，也可能没有。不要再次执行。回答用户最新的消息；如果用户问起此操作，请说明其结果未知，并建议在应用中核实。",
        NobodyToConfirm =
            "未执行：此操作需要由人来确认，而来自其他代理的请求中没有可以询问的人。请说明必须在应用程序本身中完成。",
        ActionsNotAvailable = """
            本次请求中提供给你的每个工具都可供该用户使用：当用户要求其中某个工具能做的事时，调用它。本应用的其他一些操作在本轮中对该用户不可用：它们不在你的工具中，本轮稍后也不会变得可用，你也没有其他方式执行它们。只有当用户要求的事情你的任何工具都做不到时，才用一句简短的话明确说明你在这里做不到。不要描述计划，不要说你即将去做，也不要承诺去做。
            除非你调用的工具返回了操作已完成，否则绝不要写操作已经执行：不要写“已完成”、“已更新”，也不要编造确认。声称执行了你并未执行的操作，比拒绝更严重。
            """,
        SignInMayHelp = """
            该用户尚未登录。只有当你回答某件事在这里无法完成（因为你的任何工具都做不到）时，才补充说明登录后可能可以使用——当你调用的工具拒绝或停止了它时，绝不要这样说。
            """,
        NotAvailableFollowUp = """
            不要提供你的任何工具都做不到的操作，也不要询问用户是否要执行它。用户对你之前消息的回复（“是”、一个选择、一个值）不会让这样的操作变得可用：如果你提出过它，现在用一句简短的话说明你在这里做不到。绝不要调用你的某个工具来代替它：每个工具只做其描述所说的事。
            """,
        AnnouncedActionNotRun = """
            你上一条回复预告或报告了一个操作，但本轮没有运行任何工具：什么都没有做。如果你的某个工具能完成用户要求的事，现在就调用它。否则，用一句简短的话告诉用户这件事没有完成。不要再次预告，也不要写已经完成，除非你现在调用的工具返回已完成。
            """,
        AnnouncedActionCall = """
            你上一条回复预告或报告了工具 {tool} 的操作，但本轮没有运行任何工具：什么都没有做。{tool} 是你的工具之一，现在就可以使用：现在就按用户的要求调用它。不要写这里无法完成或需要登录，也不要写已经完成，除非 {tool} 返回已完成。
            """,
        RagNothingFound = """
            知识库的搜索没有为这条消息找到任何文档。如果用户询问的是文档本应涵盖的内容（规则、流程、产品或服务的细节等），
            请说明你在文档中没有找到，不要凭记忆回答，好像某个文档这样说过一样。不需要文档的消息（问候、操作请求、由你的
            工具回答的关于应用数据的问题）照常处理。
            """,
    };
}
