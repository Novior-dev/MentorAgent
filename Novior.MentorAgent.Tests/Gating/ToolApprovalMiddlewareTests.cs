using FluentAssertions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// ROADMAP #28 — the Agent Framework's <c>ToolApprovalAgent</c> middleware, reached through
/// <c>UseToolApproval</c> and configured by <see cref="MentorOptions.AutoApprovalRules"/>.
///
/// <para>
/// <b>A correction to what this item was for.</b> The roadmap justified #28 by saying that
/// <c>ReadOnlyToolsAutoApprovalRule</c> "expresses structurally what BUG-021 and BUG-035 forced us
/// to reason about by hand — which tools are safe for an unattended caller". Checked against the
/// shipped API, that is wrong: the rule exists only as a property on the framework's own providers
/// (<c>FileAccessProvider.ReadOnlyToolsAutoApprovalRule</c>,
/// <c>AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule</c>) and covers those providers' built-in
/// tools. It cannot classify an application action such as <c>cancel_order</c>, so it offers nothing
/// to the MCP publishing decision, which stays fail-closed.
/// </para>
///
/// <para>
/// What the item genuinely buys is host-programmable auto-approval plus "don't ask again", and one
/// safety property that has already cost this project once: an iteration cap that a per-request
/// limit cannot provide.
/// </para>
/// </summary>
public class ToolApprovalMiddlewareTests
{
    private static MentorHostFixture Host(Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            configure?.Invoke(o);
        });

    // ── Opt-in ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Native mode alone must not pull the middleware in. With no rules its only remaining effect
    /// is "don't ask again", which silently widens what a single approval covers for the rest of
    /// the session — a reasonable thing to ask for and a surprising thing to inherit.
    /// </summary>
    [Fact]
    public async Task Native_mode_alone_does_not_add_the_approval_middleware()
    {
        await using var host = Host();
        await host.RunTurnAsync("ciao");

        host.Logs.Records.Should().NotContain(r => r.Message.Contains("Tool approval middleware"),
            "the middleware is opt-in through AutoApprovalRules or MaxAutoApprovalIterations");
    }

    [Fact]
    public async Task Supplying_a_rule_adds_the_middleware_and_says_so()
    {
        await using var host = Host(o => o.AutoApprovalRules =
            [_ => ValueTask.FromResult(false)]);

        await host.RunTurnAsync("ciao");

        var line = host.Logs.Records.Should()
            .ContainSingle(r => r.Message.Contains("Tool approval middleware")).Subject;
        line.Message.Should().Contain("1 auto-approval rule");
        line.Message.Should().Contain("Role checks are unaffected",
            "the log is where an operator finds out what the feature does and does not waive");
    }

    /// <summary>
    /// Asking only for the cap is a legitimate configuration on its own: a host that wants the
    /// spend bound without waiving any prompt.
    /// </summary>
    [Fact]
    public async Task An_iteration_cap_alone_is_enough_to_add_the_middleware()
    {
        await using var host = Host(o => o.MaxAutoApprovalIterations = 3);
        await host.RunTurnAsync("ciao");

        host.Logs.Records.Should().ContainSingle(r =>
            r.Message.Contains("Tool approval middleware") && r.Message.Contains("iteration cap 3"));
    }

    /// <summary>
    /// Blocking is the default mode, and there approvals never become framework content — they are
    /// MentorAgent's own event and HTTP round trip. The middleware would sit in the pipeline with
    /// nothing to intercept, and a host who set rules expecting them to apply deserves to find out
    /// from behaviour rather than from a support ticket.
    /// </summary>
    [Fact]
    public async Task In_blocking_mode_the_middleware_is_not_added()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = MentorHitlMode.Blocking;
            o.AutoApprovalRules = [_ => ValueTask.FromResult(true)];
        });

        await host.RunTurnAsync("ciao");

        host.Logs.Records.Should().NotContain(r => r.Message.Contains("Tool approval middleware"));
    }

    // ── The security boundary ────────────────────────────────────────────────────

    /// <summary>
    /// The property this whole file exists for. An auto-approval rule waives <em>the prompt</em>.
    /// It must not waive <em>authorization</em>: <c>export_ledger</c> carries
    /// <c>RequiredRoles</c>, and the role check lives inside MentorAgent's own tool wrapper, which
    /// the framework's middleware sits outside of and never sees.
    ///
    /// <para>
    /// This is the same distinction <c>RequireConfirmation</c> already had to make — one flag is a
    /// UX setting, the other is who is allowed to act — and it is the kind of thing a refactor
    /// tidying "all the approval code" into one place would quietly destroy.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_auto_approval_rule_that_approves_everything_still_cannot_bypass_a_role_gate()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.AutoApprovalRules = [_ => ValueTask.FromResult(true)];   // approve absolutely anything
        }, withRecorder: true);

        // Anonymous caller: no roles at all.
        host.Chat!.CallTool("export_ledger", new { }).Say("fatto");

        await host.RunTurnAsync("esporta il libro mastro");

        var toolText = host.Chat.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .OfType<Microsoft.Extensions.AI.FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? "")
            .ToList();

        string.Join("\n", toolText).Should().NotContain("\"success\":true",
            "an approval rule waives the dialog, never the RequiredRoles check");
    }
}
