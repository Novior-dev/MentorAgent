namespace Novior.MentorAgent.Core;

/// <summary>
/// Gives the application recipes that are not written in its code: sequences of steps on one page that the widget runs
/// in the user's browser, offered to the model as one of that page's actions — from a console that approves them, from
/// any store. Asked once for each session, when the session builds its tools: an open conversation keeps the recipes it
/// started with. Register it with <c>AddMentorRecipeSource&lt;T&gt;()</c>; every source registered is asked.
/// <para>
/// <b>A recipe clicks what the user could click, with the user's rights, in the user's browser.</b> What a source gives is
/// what the application runs: whoever implements a source decides which recipes are approved, and checks where they
/// come from — a signature, an approval. MentorAgent checks only that each recipe holds together, and leaves out with
/// its reason, never silently, one that does not (<see cref="MentorRecipeRejectionReasons"/>).
/// </para>
/// <para>
/// A recipe is offered on the page of its <see cref="MentorRecipeDefinition.Route"/> only, as a UI action of that page:
/// the action policies are asked about it with <c>Kind = UI</c> and the page's name, when it is offered and when it is
/// called, and it runs with its own roles and its own confirmation. Its selectors never reach the model, which gives the
/// values of its parameters only.
/// </para>
/// </summary>
public interface IMentorRecipeSource
{
    /// <summary>The source's recipes for one session, and what it left out itself.</summary>
    Task<MentorRecipeSourceResult> LoadAsync(MentorRecipeSourceContext context, CancellationToken ct = default);
}

/// <summary>What a <see cref="IMentorRecipeSource"/> is told when it is asked.</summary>
public sealed record MentorRecipeSourceContext
{
    /// <summary>The session the recipes are for; null when there is none.</summary>
    public string? SessionId { get; init; }
}

/// <summary>The recipes a <see cref="IMentorRecipeSource"/> gives, and those it left out with their reason.</summary>
/// <param name="Recipes">The definitions, in the order the source gives them.</param>
/// <param name="Rejected">What the source itself left out, with a <see cref="MentorRecipeRejectionReasons"/> code or one of its own.</param>
public sealed record MentorRecipeSourceResult(
    IReadOnlyList<MentorRecipeDefinition> Recipes,
    IReadOnlyList<MentorRecipeRejection> Rejected)
{
    /// <summary>
    /// Who wins when one of these recipes has the name of a UI action the page registers in code
    /// (<c>RegisterUIAction</c>): the code, by default, or the source. Any other tool — an action of the application, a
    /// tool of an MCP server, one of MentorAgent's — always wins over a recipe.
    /// </summary>
    public MentorRecipePrecedence Precedence { get; init; } = MentorRecipePrecedence.Code;
}

/// <summary>Who wins between a recipe of a source and a UI action of the page with the same name.</summary>
public enum MentorRecipePrecedence
{
    /// <summary>The page's UI action is offered, and the recipe is not: the log says so once for the session.</summary>
    Code,

    /// <summary>The recipe is offered, and the page's UI action of that name is not: the log says so once for the session.</summary>
    Source,
}

/// <summary>
/// A recipe as a source gives it. A recipe is known by its <see cref="Route"/> and its <see cref="Name"/>: two pages may
/// each have a recipe of the same name. The limits are those of <see cref="MentorRecipeLimits"/>.
/// </summary>
public sealed record MentorRecipeDefinition
{
    /// <summary>The name of the tool the model calls: 1 to 64 letters, digits, '_' and '-'. Required.</summary>
    public required string Name { get; init; }

    /// <summary>What the recipe does, for the model and for the user's confirmation. Required.</summary>
    public required string Description { get; init; }

    /// <summary>The route of the page it runs on, as the router writes it: <c>/orders/{id:int}</c>. Required.</summary>
    public required string Route { get; init; }

    /// <summary>The values the model gives, at most <see cref="MentorRecipeLimits.MaxParameters"/>.</summary>
    public IReadOnlyList<MentorRecipeParameterDefinition> Parameters { get; init; } = [];

    /// <summary>The steps, in order: 1 to <see cref="MentorRecipeLimits.MaxSteps"/>.</summary>
    public required IReadOnlyList<MentorRecipeStepDefinition> Steps { get; init; }

    /// <summary>The roles a user needs to run it, any one of them; empty, anybody may run it.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Whether the user confirms it before it runs, with the values of its parameters shown.</summary>
    public bool RequiresConfirmation { get; init; }
}

/// <summary>A value the model gives a recipe.</summary>
public sealed record MentorRecipeParameterDefinition
{
    /// <summary>Its name, as the steps write it between braces: 1 to 64 letters, digits, '_' and '-'. Required.</summary>
    public required string Name { get; init; }

    /// <summary>What the value is, for the model. Required.</summary>
    public required string Description { get; init; }

    /// <summary>The value's type: the model's value is checked against it before anything runs.</summary>
    public MentorRecipeParameterType Type { get; init; } = MentorRecipeParameterType.String;
}

/// <summary>
/// One step of a recipe. <see cref="Value"/> is a parameter, written <c>{name}</c> as the whole value, or a text, where
/// <c>{{</c> and <c>}}</c> are a brace: a single brace in a text is an error (<see cref="MentorRecipeRejectionReasons.InvalidValue"/>).
/// </summary>
public sealed record MentorRecipeStepDefinition
{
    /// <summary>What the step does.</summary>
    public required MentorRecipeStepKind Kind { get; init; }

    /// <summary>The CSS selector of the element; for <see cref="MentorRecipeStepKind.Row"/>, of the rows. Required.</summary>
    public required string Selector { get; init; }

    /// <summary>The value written, chosen or checked; for <see cref="MentorRecipeStepKind.Row"/>, the row's key.</summary>
    public string? Value { get; init; }

    /// <summary>A row step: the cell, inside a row, whose text is the row's key (<c>td:nth-child(1)</c>). Required for a row.</summary>
    public string? Cell { get; init; }

    /// <summary>A row step: what to click inside the row found; none, the row is only brought into view.</summary>
    public string? Target { get; init; }

    /// <summary>A row step: the page's own filter field, given the key before the rows are scrolled.</summary>
    public string? Filter { get; init; }
}

/// <summary>What one <see cref="IMentorRecipeSource"/> gave a session, and what was left out.</summary>
public sealed record MentorRecipeSourceReport
{
    /// <summary>The full type name of the source.</summary>
    public required string Source { get; init; }

    /// <summary>The session the recipes are for, as <see cref="MentorRecipeSourceContext.SessionId"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>The recipes the session offers on their pages, as the source gave them, in its order.</summary>
    public required IReadOnlyList<MentorRecipeDefinition> Loaded { get; init; }

    /// <summary>What was left out, by the source or by MentorAgent, and why. Empty when everything loaded as given.</summary>
    public required IReadOnlyList<MentorRecipeRejection> Rejected { get; init; }

    /// <summary>When the source answered.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>A recipe left out, and why.</summary>
/// <param name="Route">The recipe's route, as the source gave it; empty when it had none.</param>
/// <param name="Name">The recipe's name, as the source gave it; empty when it had none.</param>
/// <param name="ReasonCode">A <see cref="MentorRecipeRejectionReasons"/> code.</param>
/// <param name="Detail">What to correct. Never an exception's message: for <see cref="MentorRecipeRejectionReasons.SourceFailed"/>, only its type.</param>
public sealed record MentorRecipeRejection(string Route, string Name, string ReasonCode, string? Detail);

/// <summary>
/// The limits of a recipe, the same for every source, so that whoever approves recipes can refuse at once what MentorAgent
/// would leave out.
/// </summary>
public static class MentorRecipeLimits
{
    /// <summary>The longest name of a recipe or of a parameter.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The most steps of a recipe.</summary>
    public const int MaxSteps = 50;

    /// <summary>The most parameters of a recipe.</summary>
    public const int MaxParameters = 10;

    /// <summary>The longest description, of a recipe or of a parameter.</summary>
    public const int MaxDescriptionLength = 1024;

    /// <summary>The longest selector, cell, target or filter of a step.</summary>
    public const int MaxSelectorLength = 1024;

    /// <summary>The longest value of a step, as written in the recipe.</summary>
    public const int MaxValueLength = 1024;

    /// <summary>The longest route.</summary>
    public const int MaxRouteLength = 256;

    /// <summary>The most roles of a recipe.</summary>
    public const int MaxRoles = 20;

    /// <summary>The longest role.</summary>
    public const int MaxRoleLength = 256;
}

/// <summary>
/// The reason codes of <see cref="MentorRecipeRejection.ReasonCode"/>, a closed list. They stay the same from one version
/// to the next; a new one comes only in a minor version, and the CHANGELOG says so.
/// </summary>
public static class MentorRecipeRejectionReasons
{
    /// <summary>A field that is required is missing or empty: the description, a step's selector, a row step's cell or key, a parameter's description.</summary>
    public const string InvalidDefinition = "invalid-definition";

    /// <summary>The name is not 1 to 64 letters, digits, '_' and '-'.</summary>
    public const string InvalidName = "invalid-name";

    /// <summary>The route is not the route of a page: it does not start with '/', or has a query or a fragment.</summary>
    public const string InvalidRoute = "invalid-route";

    /// <summary>No step, more than <see cref="MentorRecipeLimits.MaxSteps"/>, or a step of a kind MentorAgent does not know.</summary>
    public const string InvalidSteps = "invalid-steps";

    /// <summary>More than <see cref="MentorRecipeLimits.MaxParameters"/> parameters, two of one name, or a parameter's name that is not valid.</summary>
    public const string InvalidParameters = "invalid-parameters";

    /// <summary>A step's value names, between braces, something that is not one of the recipe's parameters.</summary>
    public const string UnknownParameter = "unknown-parameter";

    /// <summary>A step's text has a single brace: a brace in a text is written <c>{{</c> or <c>}}</c>.</summary>
    public const string InvalidValue = "invalid-value";

    /// <summary>A field is longer than its limit (<see cref="MentorRecipeLimits"/>), or there are too many roles.</summary>
    public const string TooLong = "too-long";

    /// <summary>A recipe of the same route and name was given before, by this source or by another: the first stays.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>
    /// The whole source threw. Added by MentorAgent, with the source's type as the name and the exception's type as the
    /// detail; the other sources are still asked.
    /// </summary>
    public const string SourceFailed = "source-failed";
}
