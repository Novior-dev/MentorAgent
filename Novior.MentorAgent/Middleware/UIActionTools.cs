using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Novior.MentorAgent.Middleware;

/// <summary>
/// The page's UI actions as tools of the turn (story 1288).
///
/// <para>
/// Each <see cref="UIActionRegistration"/> is a function of its own, with its name, its description and a
/// <c>parameter</c> argument, handed to the agent with the run's options. The agent's function-invoking client calls
/// it, so it goes through the gate like every other action: roles, the action policies, the before-action hook, the
/// decisions to the observers, the span, Stop. Up to 1.3.0 <c>UIActionsMiddleware</c> added schema-only functions below
/// the agent and ran the calls in a loop of its own, which the gate never saw.
/// </para>
///
/// <para>
/// The page can change during the turn: <c>navigate_to</c> waits for the new page's <c>SignalReady</c>, and the new
/// page registers its own actions. <see cref="Refresh"/> brings the run's tools up to the page now shown after every
/// call, so the next request of the same turn offers the actions on screen and not the ones of the page left.
/// </para>
/// </summary>
internal sealed class UIActionTools
{
    private readonly IMentorPageContext _page;
    private readonly IMentorStateService _state;
    private readonly ILogger _logger;

    internal UIActionTools(IMentorPageContext page, IMentorStateService state, ILogger logger)
    {
        _page = page;
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// The actions of the page now shown, as functions. An action whose name one of <paramref name="others"/> already
    /// has is skipped and reported, as before: the model cannot be offered two tools of one name.
    /// </summary>
    internal List<AITool> Current(IEnumerable<AITool>? others)
    {
        _offeredPage = _page.PageName;
        var actions = _page.UIActions;
        if (actions.Count == 0) return [];

        var taken = (others ?? [])
            .Where(t => !IsPageAction(t))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tools = new List<AITool>(actions.Count);
        foreach (var (_, reg) in actions)
        {
            if (!taken.Add(reg.ActionName))
            {
                _logger.LogWarning(
                    "[MentorAgent] UI action '{Name}' conflicts with an existing tool — skipped.", reg.ActionName);
                continue;
            }

            tools.Add(new UIActionFunction(this, reg));
        }

        return tools;
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
        var shown = _page.UIActions.Keys.ToHashSet(StringComparer.Ordinal);
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
        => tool is UIActionFunction || tool.GetService(typeof(UIActionFunction)) is not null;

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
            return MentorUtils.ToolError(ex.Message);
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
