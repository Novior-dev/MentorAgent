using A2A;
using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-083 — a Blazor Server host has to be able to answer another agent.
///
/// <para>
/// Found live on 19 September 2026 while verifying BUG-082 on the source mirror pair, the first time
/// the Blazor sample was driven as the <b>peer</b> rather than the caller. Every task it received was
/// closed <c>TASK_STATE_COMPLETED</c> with the message <c>""</c>. A request from another agent runs
/// outside any circuit; on Blazor Server the <see cref="NavigationManager"/> that DI hands out there
/// was never initialised, and the one unguarded read in <c>AppContextProvider</c> —
/// <c>_nav.Uri</c> — threw "'RemoteNavigationManager' has not been initialized". The orchestrator
/// turned that into "Error in Coordinator" for a widget nobody was looking at; the A2A handler,
/// handed no text, completed the task with none; and the caller's workflow, handed an empty message,
/// reported NOT DELEGATED and its coordinator told the user "non posso richiedere a ShopFlowRemote".
/// The headless server has no NavigationManager at all, which is why two campaigns never saw it.
/// </para>
/// </summary>
public sealed class A2AOutsideACircuitTests
{
    /// <summary>What Blazor Server resolves outside a circuit: a manager nobody called Initialize on.</summary>
    private sealed class UninitialisedNavigationManager : NavigationManager;

    private static string TextOf(A2A.SendMessageResponse response) =>
        response.PayloadCase == A2A.SendMessageResponseCase.Message
            ? string.Join("", response.Message!.Parts.Select(p => p.Text))
            : string.Join("", (response.Task?.Status?.Message?.Parts ?? []).Select(p => p.Text));

    [Fact]
    public async Task A_host_whose_navigation_manager_has_no_circuit_still_answers_over_a2a()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.A2AServerUrl = "http://localhost/a2a"; o.MemoryAutoCapture = false; },
            configureServices: s => s.AddScoped<NavigationManager, UninitialisedNavigationManager>(),
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));
        app.Chat.CallTool("count_open_orders").Say("Otto prodotti a catalogo.");

        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await A2A.A2AClientFactory.Create(card, app.Client)
            .SendMessageAsync("quanti prodotti ci sono a catalogo?", A2A.Role.User, null, deadline.Token);

        TextOf(response).Should().Contain("Otto prodotti",
            "a turn with no page to report has no current URL — it does not have no answer. Server errors:{0}{1}",
            Environment.NewLine, string.Join(Environment.NewLine, app.Logs.Exceptions.Select(e => e.Message)));
        app.Logs.Exceptions.Should().NotContain(e => e.Message.Contains("has not been initialized"));
    }

    /// <summary>
    /// BUG-098 — found live on the published rc.11, once BUG-083's fix made A2A reachable on a
    /// Blazor Server host. <c>MentorAgentNuget</c>'s <c>update_stock</c> has <c>NavigateTo</c>: asked
    /// over A2A to set a stock, the action ran, then the auto-navigation read
    /// <see cref="NavigationManager.Uri"/> — unguarded, inside the tool's try — and the tool was
    /// reported failed. The model tried again, so the update ran <b>twice</b>, and the caller was told
    /// "Non riesco ad aggiornare lo stock … problema tecnico interno".
    /// </summary>
    [Fact]
    public async Task An_action_with_NavigateTo_that_ran_is_reported_as_done_on_a_turn_with_no_page()
    {
        // mark_order_reviewed declares no confirmation; since rc.15 that means it asks by default, and an A2A
        // task cannot answer. The case is a write the host chose to run unconfirmed, so the default is off.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.A2AServerEnabled = true; o.A2AServerUrl = "http://localhost/a2a"; o.MemoryAutoCapture = false;
                o.RequireConfirmationForWrites = false;
            },
            configureServices: s => s.AddScoped<NavigationManager, UninitialisedNavigationManager>(),
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));
        app.Chat.CallTool("mark_order_reviewed", new { orderId = 7 }).Say("Fatto: l'ordine 7 è segnato come revisionato.");

        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await A2A.A2AClientFactory.Create(card, app.Client)
            .SendMessageAsync("segna l'ordine 7 come revisionato", A2A.Role.User, null, deadline.Token);

        var results = app.Chat.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? "")
            .ToList();
        results.Should().ContainSingle(r => r.Contains("marked as reviewed"),
            "the action ran, so the model must be told it ran — a failure invites a second run. Results: {0}",
            string.Join(" | ", results));
        app.Logs.HasWarning("failed").Should().BeFalse("nothing failed. Warnings:{0}{1}", Environment.NewLine, app.Logs.WarningText);
    }

    [Fact]
    public async Task A_turn_that_produced_no_words_is_a_failed_task_not_a_completed_empty_one()
    {
        // The second half of BUG-083, and the part that outlives its cause: whatever makes a turn
        // end silently next time, "completed, with nothing" must not be what the caller is told.
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.A2AServerUrl = "http://localhost/a2a"; o.MemoryAutoCapture = false; },
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));
        app.Chat.CallTool("count_open_orders").Say("");

        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await A2A.A2AClientFactory.Create(card, app.Client)
            .SendMessageAsync("quanti prodotti ci sono a catalogo?", A2A.Role.User, null, deadline.Token);

        response.Task?.Status?.State.Should().Be(A2A.TaskState.Failed);
        TextOf(response).Should().Contain("could not produce a reply");
        app.Logs.HasWarning("produced no reply").Should().BeTrue(
            "the operator is the only one who can find the cause. Warnings:{0}{1}", Environment.NewLine, app.Logs.WarningText);
    }
}
