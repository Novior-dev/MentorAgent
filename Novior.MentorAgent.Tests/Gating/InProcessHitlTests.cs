using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Human-in-the-loop and role gating <b>in process</b> — the Blazor Server path, where the widget
/// and the orchestrator share a DI scope and no HTTP is involved.
///
/// <para>
/// The server transport suite covers the same controls over SignalR. This is the other host model,
/// and it exercises a different code path: the approval comes from a click handler on the same
/// scope rather than from a separate request, and the turn is awaited by the caller rather than by
/// a hub invocation. Both have to behave identically, because the READMEs promise the widget works
/// the same in either.
/// </para>
/// </summary>
public sealed class InProcessHitlTests
{
    /// <summary>
    /// Runs a turn that calls a confirmation-gated tool, answering the confirmation as soon as it
    /// is raised. The turn cannot be awaited before answering — it is parked inside the tool call.
    /// </summary>
    private static async Task<Exception?> RunAndAnswerAsync(
        MentorHostFixture host, string message, bool approve)
    {
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            if (approve) _ = state.ConfirmAsync(req.ActionId);
            else state.Cancel(req.ActionId);
        };

        return await host.RunTurnAsync(message);
    }

    // ── The confirmation is raised ──────────────────────────────────────────────

    [Fact]
    public async Task A_confirmation_gated_tool_raises_a_request_before_running()
    {
        await using var host = MentorHostFixture.Create(o => o.HitlMode = MentorHitlMode.Blocking);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("done");
        var service = host.Get<TestOrderService>();

        ConfirmationRequest? raised = null;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            raised = req;
            _ = state.ConfirmAsync(req.ActionId);
        };

        await host.RunTurnAsync("refund order 1001");

        raised.Should().NotBeNull("the user has to be asked before the action runs");
        raised!.ToolName.Should().Be("issue_refund");
    }

    [Fact]
    public async Task The_request_carries_the_arguments_the_user_is_approving()
    {
        // A dialog saying only "the assistant wants to do something" is not consent.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("done");

        ConfirmationRequest? raised = null;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            raised = req;
            _ = state.ConfirmAsync(req.ActionId);
        };

        await host.RunTurnAsync("refund order 1001");

        raised!.Message.Should().Contain("1001");
    }

    [Fact]
    public async Task An_ungated_tool_raises_no_confirmation()
    {
        // The control. If everything raised a confirmation, the tests above would pass for the
        // wrong reason and the widget would be unusable.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("count_open_orders").Say("done");

        var raised = 0;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ => Interlocked.Increment(ref raised);

        await host.RunTurnAsync("how many open orders");

        raised.Should().Be(0);
    }

    // ── Approve and deny actually decide whether the code runs ──────────────────

    [Fact]
    public async Task Approving_lets_the_method_run()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("done");
        var service = host.Get<TestOrderService>();

        var error = await RunAndAnswerAsync(host, "refund order 1001", approve: true);

        error.Should().BeNull();
        string.Join(", ", service.Invoked).Should().Contain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Fact]
    public async Task Denying_stops_the_method_from_running_at_all()
    {
        // The whole point of the gate. A dialog that asks and then runs the action anyway is worse
        // than no dialog, because the user believes they stopped it.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("understood");
        var service = host.Get<TestOrderService>();

        var error = await RunAndAnswerAsync(host, "refund order 1001", approve: false);

        error.Should().BeNull();
        string.Join(", ", service.Invoked).Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Fact]
    public async Task Denying_still_finishes_the_turn()
    {
        // A refusal must complete, not hang. The model is told the action was declined and writes
        // a short acknowledgement.
        //
        // Deliberately NOT withRecorder: the recorder is a fake state service, not a decorator, so
        // its Cancel never reaches the real approval store and the turn would park forever. Any
        // HITL test has to run on the real service.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("Understood, I won't proceed.");

        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        var error = await host.RunTurnAsync("refund order 1001");

        error.Should().BeNull();
        host.Chat.CallCount.Should().BeGreaterThan(1,
            "the model is asked again after the refusal so it can acknowledge it");
    }

    // ── Both HITL modes behave the same from the outside ────────────────────────

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Either_mode_raises_the_same_confirmation_shape(MentorHitlMode mode)
    {
        // Documented: "the banner and the client protocol are identical in both modes … so you can
        // flip the mode without touching a single client". That is only true if the request looks
        // the same, which is what this asserts.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("done");

        ConfirmationRequest? raised = null;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            raised = req;
            _ = state.ConfirmAsync(req.ActionId);
        };

        await host.RunTurnAsync("refund order 1001");

        raised.Should().NotBeNull("mode {0} raised nothing", mode);
        raised!.ToolName.Should().Be("issue_refund");
        raised.ActionId.Should().NotBe(Guid.Empty);
        raised.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Either_mode_blocks_the_method_on_a_denial(MentorHitlMode mode)
    {
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("understood");
        var service = host.Get<TestOrderService>();

        await RunAndAnswerAsync(host, "refund order 1001", approve: false);

        string.Join(", ", service.Invoked).Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task After_Stop_during_an_approval_the_next_turn_still_works(MentorHitlMode mode)
    {
        // rc.14 analysis (H5): Stop pressed while the banner waits leaves, in Native mode, a stored approval
        // request with no response — and the framework refuses a request that carries one. Probed in both
        // modes: the next message must reach the model and be answered.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("second answer");
        var state = host.Get<IMentorStateService>();
        var first = true;
        state.OnConfirmationRequired += _ =>
        {
            if (!first) return;
            first = false;
            host.Orchestrator.CancelCurrentRequest();
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("refund order 1001", deadline.Token);
        var error = await host.RunTurnAsync("how many open orders?", deadline.Token);

        error.Should().BeNull();
        var problems = string.Join(" | ", host.Logs.Records.Where(r => r.Level >= Microsoft.Extensions.Logging.LogLevel.Warning).Select(r => r.Message));
        host.Chat.Calls[^1].Messages.Should().Contain(m => m.Text.Contains("how many open orders?"),
            "the turn after a Stop must reach the model; logged: {0} :: {1}", problems, host.Logs.ExceptionText);
        host.Get<TestOrderService>().Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
        if (mode == MentorHitlMode.Native)
            TheTurnStillWorks(host, error, "how many open orders?");
    }

    // BUG-116, the windows the first fix left open (rc.14 review). A run that throws is not stored, so the
    // request it was to answer is still open afterwards: it must be answered on the next turn that succeeds.

    private static MentorHostFixture NativeHost()
        => MentorHostFixture.Create(o => { o.HitlMode = MentorHitlMode.Native; o.MemoryAutoCapture = false; });

    private static void StopAtTheFirstBanner(MentorHostFixture host)
    {
        var first = true;
        host.Get<IMentorStateService>().OnConfirmationRequired += _ =>
        {
            if (!first) return;
            first = false;
            host.Orchestrator.CancelCurrentRequest();
        };
    }

    private static void TheTurnStillWorks(MentorHostFixture host, Exception? error, string question, string answeredAs = "not_run")
    {
        error.Should().BeNull();
        host.Chat!.Calls[^1].Messages.Should().Contain(m => m.Text.Contains(question),
            "the turn must reach the model; exceptions: {0}", host.Logs.ExceptionText);
        host.Logs.ExceptionText.Should().NotContain("no matching ToolApprovalResponseContent");

        // The stricter half. In process the function-invoking client marks a call it has handled as
        // informational on the very instance the in-memory history keeps, and then lets an unanswered request
        // through untouched — so "the turn worked" can hide a call that is still open (a service-managed history
        // refuses every turn while a function call has no output). The request left open must reach the model
        // closed by the answer recorded for it.
        string.Concat(host.Chat.Calls[^1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .Select(r => r.Result?.ToString()))
            .Should().Contain(answeredAs, "the request left open is closed by its answer; calls: {0}", Dump(host));
    }

    private static string Dump(MentorHostFixture host)
        => string.Join(" || ", host.Chat!.Calls.Select(c => string.Join(" ; ", c.Messages.Select(m =>
            m.Role + ":" + string.Join(",", m.Contents.Select(x => x.GetType().Name
                + (x is FunctionResultContent fr ? "=" + fr.Result : x is TextContent tc ? "=" + tc.Text : "")))))));

    [Fact]
    public async Task A_second_Stop_before_the_left_open_request_is_answered_keeps_it_for_the_turn_after()
    {
        await using var host = NativeHost();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("third answer");
        StopAtTheFirstBanner(host);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);
        host.Chat.BeforeRespond = () => { host.Orchestrator.CancelCurrentRequest(); return Task.CompletedTask; };
        await host.RunTurnAsync("how many open orders?", deadline.Token);   // stopped too, before its answer
        host.Chat.BeforeRespond = null;
        var error = await host.RunTurnAsync("and the delivered ones?", deadline.Token);

        TheTurnStillWorks(host, error, "and the delivered ones?");
        host.Get<TestOrderService>().Invoked.Should().NotContain(nameof(TestOrderService.IssueRefundAsync));
    }

    [Fact]
    public async Task A_failure_before_the_left_open_request_is_answered_keeps_it_for_the_turn_after()
    {
        await using var host = NativeHost();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("third answer");
        StopAtTheFirstBanner(host);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);
        host.Chat.ThrowOnNextCall = new InvalidOperationException("HTTP 429 (rate_limit_exceeded)");
        await host.RunTurnAsync("how many open orders?", deadline.Token);
        var error = await host.RunTurnAsync("and the delivered ones?", deadline.Token);

        TheTurnStillWorks(host, error, "and the delivered ones?");
    }

    [Fact]
    public async Task A_run_that_fails_after_the_user_confirmed_leaves_the_conversation_usable_and_the_action_run_once()
    {
        // The answer was sent, the action ran, and the call that was to report it failed: nothing of that run
        // was stored. Next turn the request is answered as "may have run" — never approved a second time.
        await using var host = NativeHost();
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("second answer");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            host.Chat.ThrowOnNextCall = new InvalidOperationException("HTTP 429 (rate_limit_exceeded)");
            _ = state.ConfirmAsync(req.ActionId);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund order 1001", deadline.Token);
        var error = await host.RunTurnAsync("how many open orders?", deadline.Token);

        TheTurnStillWorks(host, error, "how many open orders?", answeredAs: "outcome_unknown");
        host.Get<TestOrderService>().Invoked.Count(n => n == nameof(TestOrderService.IssueRefundAsync))
            .Should().Be(1, "the confirmed action ran once, and the recovery must not run it again");
    }

    [Fact]
    public async Task Giving_up_after_the_approval_round_cap_leaves_the_conversation_usable()
    {
        await using var host = NativeHost();
        for (var i = 0; i < 9; i++) host.Chat!.CallTool("issue_refund", new { orderId = 1001 + i });
        host.Chat!.Say("after the cap");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await host.RunTurnAsync("refund every order", deadline.Token);
        var error = await host.RunTurnAsync("how many open orders?", deadline.Token);

        host.Logs.WarningText.Should().Contain("gave up after");
        TheTurnStillWorks(host, error, "how many open orders?");
    }

    // ── The RequiresApproval predicate ──────────────────────────────────────────

    [Fact]
    public async Task The_predicate_can_gate_a_tool_the_attribute_left_open()
    {
        // The options-level escape hatch, for tools whose attributes a host does not own — an MCP
        // server's tools, or an action declared in a library.
        await using var host = MentorHostFixture.Create(o =>
            o.RequiresApproval = name => name == "count_open_orders");
        host.Chat!.CallTool("count_open_orders").Say("done");

        ConfirmationRequest? raised = null;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            raised = req;
            _ = state.ConfirmAsync(req.ActionId);
        };

        await host.RunTurnAsync("how many open orders");

        raised.Should().NotBeNull();
        raised!.ToolName.Should().Be("count_open_orders");
    }

    [Fact]
    public async Task A_predicate_returning_false_leaves_the_attribute_gate_in_place()
    {
        // The predicate adds tools to the gated set; it must not be able to switch off a gate the
        // developer declared on the method. Silently un-gating a destructive action would be the
        // most dangerous possible reading of this option.
        await using var host = MentorHostFixture.Create(o => o.RequiresApproval = _ => false);
        host.Chat!.CallTool("issue_refund", new { orderId = 1001 }).Say("done");

        var raised = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            Interlocked.Increment(ref raised);
            _ = state.ConfirmAsync(req.ActionId);
        };

        await host.RunTurnAsync("refund order 1001");

        raised.Should().Be(1,
            "RequiresConfirmation on the method is the developer's decision and the predicate " +
            "must not be able to override it");
    }

    // ── Role gating in process ──────────────────────────────────────────────────

    [Fact]
    public async Task An_anonymous_user_is_refused_a_role_gated_tool_in_process()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("export_ledger").Say("done");
        var service = host.Get<TestOrderService>();

        await host.RunTurnAsync("export the ledger");

        string.Join(", ", service.Invoked).Should().NotContain(nameof(TestOrderService.ExportLedger));
    }

    [Fact]
    public async Task A_user_with_the_role_reaches_the_tool_in_process()
    {
        await using var host = MentorHostFixture.Create();
        host.Auth.SignIn("ada", "Admin");
        host.Chat!.CallTool("export_ledger").Say("done");
        var service = host.Get<TestOrderService>();

        await host.RunTurnAsync("export the ledger");

        string.Join(", ", service.Invoked).Should().Contain(nameof(TestOrderService.ExportLedger));
    }

    [Fact]
    public async Task Signing_out_takes_the_permission_away_again()
    {
        // Roles are read per tool call, not cached at startup. A session that loses its rights
        // mid-conversation has to lose them for the next action too.
        await using var host = MentorHostFixture.Create();
        host.Auth.SignIn("ada", "Admin");
        host.Chat!.CallTool("export_ledger").Say("done")
                  .CallTool("export_ledger").Say("done");
        var service = host.Get<TestOrderService>();

        await host.RunTurnAsync("export the ledger");
        var afterSignedIn = service.Invoked.Count(i => i == nameof(TestOrderService.ExportLedger));

        host.Auth.SignOut();
        await host.RunTurnAsync("export the ledger again");

        service.Invoked.Count(i => i == nameof(TestOrderService.ExportLedger))
            .Should().Be(afterSignedIn, "the second attempt must be refused");
    }

    [Fact]
    public async Task A_refused_tool_still_lets_the_turn_finish()
    {
        // The model is handed the refusal as the tool result and explains it. A gate that killed
        // the turn would look like a crash.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.CallTool("export_ledger").Say("You don't have permission for that.");

        var error = await host.RunTurnAsync("export the ledger");

        error.Should().BeNull();
        host.Recorder!.Log.Select(e => e.Event).Should().Contain("NotifyStreamingCompleted");
    }

    [Fact]
    public async Task The_refusal_reaches_the_model_as_the_tool_result()
    {
        // So the assistant can say why. Returning an empty result instead would have it report
        // that the export produced nothing — which is a different, and wrong, statement.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("export_ledger").Say("no permission");

        await host.RunTurnAsync("export the ledger");

        var results = host.Chat.Calls[^1].Messages
            .SelectMany(m => m.Contents)
            .OfType<Microsoft.Extensions.AI.FunctionResultContent>()
            .Select(c => c.Result?.ToString() ?? "");

        string.Join(" ", results).Should().NotBeEmpty("the model needs to be told the call was refused");
    }

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_user_without_the_role_is_never_asked_to_confirm(MentorHitlMode mode)
    {
        // Found live on rc.4 (BUG-063). In Native mode an anonymous visitor was shown the banner for
        // an Admin-only action, pressed Conferma, and only then was refused. Security held — the role
        // check still runs at invocation — but the gates ran in the wrong order: the framework raises
        // the approval request before the call ever reaches the middleware that checks roles, so the
        // user was asked to approve something they could never do. Blocking mode checks the role
        // first; the two modes are documented as indistinguishable from the outside.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = mode);
        host.Chat!.CallTool("cancel_order", new { orderId = 1001, reason = "test" }).Say("no permission");
        var service = host.Get<TestOrderService>();

        var raised = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            Interlocked.Increment(ref raised);
            state.Cancel(req.ActionId);   // answered, so a regression fails instead of hanging
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = await host.RunTurnAsync("cancel order 1001", deadline.Token);

        error.Should().BeNull();
        raised.Should().Be(0, "mode {0} asked an anonymous user to confirm a Manager-only action", mode);
        string.Join(", ", service.Invoked).Should().NotContain(nameof(TestOrderService.CancelOrderAsync));
    }

    [Fact]
    public async Task In_native_mode_the_model_is_told_the_refusal_is_about_permission()
    {
        // The reason travels with the refusal, so the assistant says "you need to sign in" rather
        // than "you declined" — which the user did not do.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = MentorHitlMode.Native);
        host.Chat!.CallTool("cancel_order", new { orderId = 1001, reason = "test" }).Say("no permission");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => state.Cancel(req.ActionId);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("cancel order 1001", deadline.Token);

        var results = string.Join(" ", host.Chat.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .OfType<Microsoft.Extensions.AI.FunctionResultContent>()
            .Select(c => c.Result?.ToString() ?? ""));

        var signIn = new Novior.MentorAgent.Abstractions.Core.MentorLocalizer(host.Options.Language)
            .Get("auth.login.required");
        results.Should().Contain(signIn, "the refusal is about who the user is, not about a choice they made");
        results.Should().NotContain("declined_by_user", "nobody declined anything");
    }

    [Fact]
    public async Task In_native_mode_a_user_with_the_role_is_still_asked()
    {
        // The control: checking the role first must not swallow the confirmation for someone who
        // is allowed to proceed.
        await using var host = MentorHostFixture.Create(o => o.HitlMode = MentorHitlMode.Native);
        host.Auth.SignIn("mara", "Manager");
        host.Chat!.CallTool("cancel_order", new { orderId = 1001, reason = "test" }).Say("done");
        var service = host.Get<TestOrderService>();

        var raised = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            Interlocked.Increment(ref raised);
            _ = state.ConfirmAsync(req.ActionId);
        };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("cancel order 1001", deadline.Token);

        raised.Should().Be(1);
        string.Join(", ", service.Invoked).Should().Contain(nameof(TestOrderService.CancelOrderAsync));
    }

    [Fact]
    public async Task A_role_gated_tool_is_refused_when_no_auth_provider_is_registered()
    {
        // Fails closed, and says why. The dangerous alternative — treating "no auth configured" as
        // "no restrictions" — would silently open every RequiredRoles action in any host that
        // forgot to wire authentication.
        await using var host = MentorHostFixture.Create(
            configureServices: s => s.RemoveAll<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>());
        host.Chat!.CallTool("export_ledger").Say("done");
        var service = host.Get<TestOrderService>();

        await host.RunTurnAsync("export the ledger");

        string.Join(", ", service.Invoked).Should().NotContain(nameof(TestOrderService.ExportLedger));
    }
}
