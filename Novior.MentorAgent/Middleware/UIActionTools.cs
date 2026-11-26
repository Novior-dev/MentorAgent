using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Components;
using Novior.MentorAgent.Core;
using System.Text.Json;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// The page's UI actions as tools of the turn (story 1288).
///
/// <para>
/// Each <see cref="UIActionRegistration"/> is a function of its own, with its name, its description and a
/// <c>parameter</c> argument, handed to the agent with the run's options. The agent's function-invoking client calls
/// it, so it goes through the gate like every other action: roles, the action policies, the before-action hook, the
/// decisions to the observers, the span, Stop. Up to 1.3.1 <c>UIActionsMiddleware</c> added schema-only functions below
/// the agent and ran the calls in a loop of its own, which the gate never saw.
/// </para>
///
/// <para>
/// The page can change during the turn: <c>navigate_to</c> waits for the new page's <c>SignalReady</c>, and the new
/// page registers its own actions. <see cref="Refresh"/> brings the run's tools up to the page now shown after every
/// call, so the next request of the same turn offers the actions on screen and not the ones of the page left.
/// </para>
///
/// <para>
/// Story 1638 — the recipes of the page shown are page actions too (<c>recipe.offer</c>), with their own roles and their
/// own confirmation, which the gate reads from the function (<see cref="RecipeOf"/>).
/// </para>
/// </summary>
internal sealed class UIActionTools
{
    private readonly IMentorPageContext _page;
    private readonly IMentorStateService _state;
    private readonly ILogger _logger;
    private readonly MentorRecipeRegistry? _recipes;
    private readonly IMentorRecipeRunner? _runner;
    private readonly NavigationManager? _navigation;
    private readonly Func<IReadOnlyList<MentorRecipe>>? _sessionRecipes;

    internal UIActionTools(IMentorPageContext page, IMentorStateService state, ILogger logger,
        MentorRecipeRegistry? recipes = null, IMentorRecipeRunner? runner = null, NavigationManager? navigation = null,
        Func<IReadOnlyList<MentorRecipe>>? sessionRecipes = null)
    {
        _page = page;
        _state = state;
        _logger = logger;
        _recipes = recipes;
        _runner = runner;
        _navigation = navigation;
        _sessionRecipes = sessionRecipes;
    }

    /// <summary>Story 1639 — the recipes the session's <see cref="IMentorRecipeSource"/>s gave when it began.</summary>
    private IReadOnlyList<MentorRecipe> SessionRecipes() => _sessionRecipes?.Invoke() ?? [];

    /// <summary>
    /// The path of the page the user is on: the one the runner knows (the WebAssembly client's, on the hub), else the
    /// scope's own; none without a browser behind the scope.
    /// </summary>
    private string? Path()
    {
        if (_runner?.PagePath is { } shown) return shown;
        if (_navigation is null) return null;
        try
        {
            return "/" + _navigation.ToBaseRelativePath(_navigation.Uri).Split('?', '#')[0];
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;   // a scope with no circuit: its NavigationManager was never initialised
        }
    }

    /// <summary>The recipes of the page shown, on, when there is somewhere to run them.</summary>
    private IReadOnlyList<MentorRecipe> Recipes() => _recipes is null || _runner is null ? [] : _recipes.For(Path(), SessionRecipes());

    /// <summary>
    /// The actions of the page now shown, as functions. An action whose name one of <paramref name="others"/> already
    /// has is skipped and reported, as before: the model cannot be offered two tools of one name.
    /// </summary>
    internal List<AITool> Current(IEnumerable<AITool>? others)
    {
        _offeredPage = _page.PageName;
        var actions = _page.UIActions;
        var recipes = Recipes();
        if (actions.Count == 0 && recipes.Count == 0) return [];

        var taken = (others ?? [])
            .Where(t => !IsPageAction(t))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Story 1639 (recipe.precedence) — a recipe of a source that wins over the code takes its name from the page's
        // UI action; no recipe takes one from another tool.
        var sourceWins = recipes
            .Where(r => r.Precedence == MentorRecipePrecedence.Source && !taken.Contains(r.Name))
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tools = new List<AITool>(actions.Count);
        foreach (var (_, reg) in actions)
        {
            if (sourceWins.Contains(reg.ActionName))
            {
                Once($"source-wins:{_page.PageName}:{reg.ActionName}", () => _logger.LogWarning(
                    "[MentorAgent] recipe.precedence: the UI action '{Name}' of the page '{Page}' is not offered: a recipe of the same name wins over the code.",
                    reg.ActionName, _page.PageName));
                continue;
            }

            if (!taken.Add(reg.ActionName))
            {
                _logger.LogWarning(
                    "[MentorAgent] UI action '{Name}' conflicts with an existing tool — skipped.", reg.ActionName);
                continue;
            }

            tools.Add(new UIActionFunction(this, reg));
        }

        foreach (var recipe in recipes)
        {
            if (!taken.Add(recipe.Name))
            {
                if (recipe.Precedence != MentorRecipePrecedence.Source && actions.Values.Any(a =>
                        string.Equals(a.ActionName, recipe.Name, StringComparison.OrdinalIgnoreCase)))
                    Once($"code-wins:{recipe.Route}:{recipe.Name}", () => _logger.LogWarning(
                        "[MentorAgent] recipe.precedence: the recipe '{Name}' of '{Route}' is not offered: the page's UI action of the same name wins.",
                        recipe.Name, recipe.Route));
                else
                    Once($"taken:{recipe.Route}:{recipe.Name}", () => _logger.LogWarning(
                        "[MentorAgent] Recipe '{Name}' conflicts with an existing tool — skipped.", recipe.Name));
                continue;
            }

            tools.Add(new RecipeFunction(this, recipe));
        }

        return tools;
    }

    /// <summary>What the log has said for this session, so that it says each conflict of a recipe once.</summary>
    private readonly ConcurrentDictionary<string, bool> _said = new(StringComparer.Ordinal);

    private void Once(string key, Action say)
    {
        if (_said.TryAdd(key, true)) say();
    }

    /// <summary>The page whose actions were last offered, so that another page with the same names is offered anew.</summary>
    private string? _offeredPage;

    /// <summary>
    /// Replaces the page's actions in <paramref name="tools"/>, the options of the running request, with the actions
    /// of the page now shown. Nothing changes when the same page offers the same names: a handler is looked up when
    /// the action runs, never kept from the moment it was offered. Another page shown is a change even with the same
    /// names, so that the policies are asked about its actions (page.offered). True when the actions changed.
    /// </summary>
    internal bool Refresh(IList<AITool>? tools)
    {
        if (tools is null || tools.IsReadOnly) return false;

        var offered = tools.Where(IsPageAction).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var shown = _page.UIActions.Keys.Concat(Recipes().Select(r => r.Name)).ToHashSet(StringComparer.Ordinal);
        if (offered.SetEquals(shown) && string.Equals(_page.PageName, _offeredPage, StringComparison.Ordinal))
            return false;

        for (var i = tools.Count - 1; i >= 0; i--)
            if (IsPageAction(tools[i])) tools.RemoveAt(i);

        foreach (var tool in Current(tools))
            tools.Add(tool);
        return true;
    }

    /// <summary>Whether the tool is one of the page's actions, wrapped or not.</summary>
    internal static bool IsPageAction(AITool tool)
        => tool is UIActionFunction or RecipeFunction
           || tool.GetService(typeof(UIActionFunction)) is not null
           || tool.GetService(typeof(MentorRecipe)) is not null;

    /// <summary>The recipe a tool runs (story 1638), wrapped or not; null for any other tool.</summary>
    internal static MentorRecipe? RecipeOf(AITool? tool) => tool?.GetService(typeof(MentorRecipe)) as MentorRecipe;

    /// <summary>
    /// Runs a recipe in the user's browser (story 1638, <c>recipe.run</c>). The recipe is looked up when it runs, as a UI
    /// action is: one turned off since it was offered, or one of a page the user left, does not run. A step that cannot
    /// be done turns the recipe off and throws <see cref="MentorRecipeNotDoneException"/>, which the gate decides as
    /// <c>Failed</c> with <see cref="MentorActionReasons.ElementMissing"/>.
    /// </summary>
    private async ValueTask<object?> RunRecipeAsync(MentorRecipe offered, AIFunctionArguments arguments, CancellationToken ct)
    {
        if (_recipes is null || !_recipes.IsOn(offered, SessionRecipes()) || _runner is null)
            return MentorUtils.ToolError($"The recipe '{offered.Name}' is off.");
        var recipe = offered;
        if (MentorPageRoutes.Match(Path(), [recipe.Route]) is null)
            return MentorUtils.ToolError($"The page that offers '{recipe.Name}' is no longer open.");

        IReadOnlyList<MentorRecipeStepCall> steps;
        try
        {
            steps = MentorRecipeRegistry.Steps(recipe, arguments);
        }
        catch (ArgumentException ex)
        {
            return MentorUtils.ToolError(ex.Message);
        }

        _state.NotifyUIActionExecuting(recipe.Name);
        MentorRecipeOutcome outcome;
        try
        {
            outcome = await _runner.RunAsync(steps, ct);
        }
        finally
        {
            _state.NotifyUIActionCompleted(recipe.Name);
        }

        if (outcome.Done)
        {
            _logger.LogDebug("[MentorAgent] Recipe '{Name}' done.", recipe.Name);
            return MentorUtils.ToolSuccessAction(recipe.Name);
        }

        var reason = string.IsNullOrWhiteSpace(outcome.Reason) ? "missing" : outcome.Reason;
        _recipes.TurnOff(recipe, reason);
        _logger.LogWarning("[MentorAgent] recipe.element-missing: the recipe '{Name}' could not be done at step {Step} ({Reason}) and is now off.",
            recipe.Name, outcome.Step + 1, reason);
        throw new MentorRecipeNotDoneException(recipe.Name, outcome.Step, reason);
    }

    /// <summary>
    /// Runs one UI action, telling the state service for the feedback in the widget.
    ///
    /// <para>
    /// The registration is resolved <b>when the action runs</b>, not taken from the moment it was offered. Seconds
    /// pass between the two, and a user navigating in that window is entirely ordinary — <c>Clear()</c> runs in the
    /// page's <c>Dispose</c>. Running the old handler would fire a delegate closing over a component that no longer
    /// exists: at best an exception from inside disposed UI, at worst a state change applied to a page the user has
    /// left. A re-render that registers the same name again is the same problem in miniature.
    /// </para>
    /// </summary>
    private async ValueTask<object?> RunAsync(string actionName, AIFunctionArguments arguments)
    {
        if (!_page.UIActions.TryGetValue(actionName, out var reg))
        {
            _logger.LogWarning(
                "[MentorAgent] UI action '{Name}' was requested after the page that registered it " +
                "went away — skipped rather than run against a disposed component.", actionName);
            return MentorUtils.ToolError($"The page that offers '{actionName}' is no longer open.");
        }

        try
        {
            var param = ExtractParam(arguments);

            _state.NotifyUIActionExecuting(reg.ActionName);

            if (reg.HandlerAsync is not null)
                await reg.HandlerAsync(param);
            else
                reg.Handler(param);

            _state.NotifyUIActionCompleted(reg.ActionName);

            _logger.LogDebug("[MentorAgent] UI action '{Name}' executed successfully.", reg.ActionName);
            return MentorUtils.ToolSuccessAction(reg.ActionName);
        }
        catch (Exception ex)
        {
            // The type only: the handler is the host's page code, and its message can carry personal data.
            _logger.LogWarning("[MentorAgent] UI action '{Name}' threw an exception ({Type}).",
                reg.ActionName, ex.GetType().Name);
            _logger.LogDebug(ex, "[MentorAgent] UI action '{Name}' threw an exception.", reg.ActionName);
            // BUG-241: the gate decides the action failed. A result here was an action executed for the observers.
            throw new MentorUIActionFailedException(reg.ActionName, ex);
        }
    }

    /// <summary>
    /// The value of the action's argument. Three conventions, all seen from models:
    ///   1. <c>{"parameter": {...}}</c>         → the canonical key;
    ///   2. <c>{"someKey": value}</c>           → a single argument under any name;
    ///   3. <c>{"customerId":3,"items":"x"}</c> → several flat fields (the model unpacked the DTO), re-serialised as one
    ///      JSON object so <c>MentorPageContext.DeserializeParam&lt;T&gt;</c> can read it as the DTO.
    /// </summary>
    private static object? ExtractParam(AIFunctionArguments args)
    {
        if (args.Count == 0) return null;

        if (args.TryGetValue("parameter", out var p)) return p;

        if (args.Count == 1) return args.Values.First();

        try
        {
            var json = JsonSerializer.Serialize(args.ToDictionary(a => a.Key, a => a.Value));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();   // Clone() owns its memory — safe after the document is disposed
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A recipe as a function (story 1638): its parameters are the arguments, each with its type and description; the
    /// selectors never reach the model.
    /// </summary>
    private sealed class RecipeFunction(UIActionTools owner, MentorRecipe recipe) : AIFunction
    {
        private readonly JsonElement _schema = Schema(recipe);

        public override string Name => recipe.Name;
        public override string Description => recipe.Description;
        public override JsonElement JsonSchema => _schema;

        public override object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType == typeof(MentorRecipe) ? recipe : base.GetService(serviceType, serviceKey);

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => owner.RunRecipeAsync(recipe, arguments, cancellationToken);

        private static JsonElement Schema(MentorRecipe recipe)
        {
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var p in recipe.Parameters)
                properties[p.Name] = new Dictionary<string, string>
                {
                    ["type"] = p.Type switch
                    {
                        MentorRecipeParameterType.Number => "number",
                        MentorRecipeParameterType.Boolean => "boolean",
                        _ => "string",
                    },
                    ["description"] = p.Description,
                };

            return JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = recipe.Parameters.Select(p => p.Name).ToArray(),
            });
        }
    }

    /// <summary>
    /// One UI action as a function. The schema comes from a function built for it alone (an optional
    /// <c>parameter</c>); the call runs the action through <see cref="RunAsync"/>, with the arguments as the model sent
    /// them, so every convention of <see cref="ExtractParam"/> keeps working.
    /// </summary>
    private sealed class UIActionFunction(UIActionTools owner, UIActionRegistration reg)
        : DelegatingAIFunction(Schema(reg))
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => owner.RunAsync(reg.ActionName, arguments);

        private static AIFunction Schema(UIActionRegistration reg)
        {
            var description = string.IsNullOrWhiteSpace(reg.ParameterHint)
                ? reg.Description
                : $"{reg.Description} (parameter: {reg.ParameterHint})";

            return AIFunctionFactory.Create(
#pragma warning disable CS1998
                async (
                    [Description("Optional parameter: string, number, or JSON object")]
                    JsonElement? parameter = null,
                    CancellationToken ct = default) => string.Empty,
#pragma warning restore CS1998
                name: reg.ActionName,
                description: description);
        }
    }
}

/// <summary>
/// BUG-241 — the handler of a UI action threw: the gate decides the action <see cref="Abstractions.Models.MentorActionOutcome.Failed"/>,
/// with the type of <see cref="Exception.InnerException"/>, the handler's own exception, and tells the model why. Up to
/// 1.4.0 the action's tool answered an error as its result, and the observers were told the action was executed.
/// </summary>
internal sealed class MentorUIActionFailedException(string actionName, Exception inner)
    : Exception(inner.Message, inner)
{
    internal string ActionName { get; } = actionName;

    /// <summary>The handler's own exception.</summary>
    internal Exception Handler => InnerException!;
}
