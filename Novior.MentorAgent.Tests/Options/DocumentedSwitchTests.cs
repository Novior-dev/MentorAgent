using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Options;

/// <summary>
/// Documented options that the claims ledger found nobody was testing.
///
/// <para>
/// <c>claims.tsv</c> exists to answer one question — which README claims has nobody taken
/// responsibility for — and the first run of it named nine options with **zero** occurrences
/// anywhere in the test project. Every one is in the published option table, so a reader will set
/// them. This file takes the ones whose failure would matter most.
/// </para>
///
/// <para>
/// <c>RequireConfirmation</c> leads because it is the <b>kill switch for the confirmation dialogs of the
/// application's own actions</b>. The question that matters is not whether it silences the banner —
/// that is its job — but whether switching it off also takes the <b>role gate</b> with it. One
/// property is a UX setting; the other is authorisation, and the two must not travel together. It does
/// not reach the approvals a hosted MCP server asks for, which the README once said it did (BUG-137).
/// </para>
/// </summary>
public sealed class DocumentedSwitchTests
{
    private static Novior.MentorAgent.Tests.App.TestOrderService Orders(MentorHostFixture host)
        => host.Get<Novior.MentorAgent.Tests.App.TestOrderService>();

    // ── RequireConfirmation: the global kill switch ─────────────────────────────

    [Fact]
    public async Task With_confirmations_off_an_approval_required_action_runs_unattended()
    {
        // Documented as on/off for the confirmation dialogs of your actions. A headless job or a trusted
        // internal tool switches it off so a RequiresConfirmation action does not park forever
        // waiting for a click nobody will make.
        await using var host = MentorHostFixture.Create(configure: o => o.RequireConfirmation = false);
        host.Auth.SignIn("u1", "Manager", "Admin");

        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("done");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.RunTurnAsync("refund order 7", deadline.Token);

        Orders(host).Invoked.Should()
            .Contain(nameof(Novior.MentorAgent.Tests.App.TestOrderService.IssueRefundAsync));
    }

    [Fact]
    public async Task Switching_confirmations_off_does_not_switch_the_role_gate_off_with_them()
    {
        // The property this whole file was written for. `RequireConfirmation` is a UX setting;
        // `RequiredRoles` is authorisation. If one flag disabled both, an application that turned
        // off dialogs for convenience would silently open every Admin-only action to everybody —
        // and it would look like it was working, because the action would simply run.
        await using var host = MentorHostFixture.Create(configure: o => o.RequireConfirmation = false);
        host.Auth.SignOut();

        host.Chat!.CallTool("export_ledger").Say("done");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.RunTurnAsync("export the ledger", deadline.Token);

        Orders(host).Invoked.Should()
            .NotContain(nameof(Novior.MentorAgent.Tests.App.TestOrderService.ExportLedger),
                "an anonymous caller must still be refused an Admin-only action");
    }

    [Fact]
    public async Task The_default_is_that_confirmations_are_required()
    {
        // Fail-safe by default: a host that never read this option still gets asked.
        await using var host = MentorHostFixture.Create();
        host.Auth.SignIn("u1", "Manager", "Admin");

        host.Options.RequireConfirmation.Should().BeTrue();

        var asked = false;
        var state = host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>();
        state.OnConfirmationRequired += req => { asked = true; _ = state.ConfirmAsync(req.ActionId); };

        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("done");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.RunTurnAsync("refund order 7", deadline.Token);

        asked.Should().BeTrue("the banner is the default, not the opt-in");
    }

#pragma warning disable MEAI001 // the approval contents are experimental
    [Theory]
    [InlineData(Novior.MentorAgent.Options.MentorHitlMode.Blocking)]
    [InlineData(Novior.MentorAgent.Options.MentorHitlMode.Native)]
    public async Task With_confirmations_off_a_hosted_MCP_server_still_asks(Novior.MentorAgent.Options.MentorHitlMode mode)
    {
        // The switch is MentorAgent's own gate. A hosted MCP server's approval is asked for by the provider
        // (RequireApproval, AlwaysRequireApprovalTools on the server) and arrives as a ToolApprovalRequestContent,
        // which shows the usual banner in either mode, whatever this option says (BUG-137).
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.RequireConfirmation = false;
            o.HitlMode = mode;
        });
        host.Chat!.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new ToolApprovalRequestContent("approval-1",
                new McpServerToolCallContent("call-1", "microsoft_docs_search", "microsoft_learn"))]))
            {
                ModelId = host.Chat.ModelId,
            })
            .Say("done");

        Novior.MentorAgent.Abstractions.Models.ConfirmationRequest? raised = null;
        var state = host.Get<Novior.MentorAgent.Abstractions.Interfaces.IMentorStateService>();
        state.OnConfirmationRequired += req => { raised = req; state.Cancel(req.ActionId); };

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = await host.RunTurnAsync("search the docs", deadline.Token);

        error.Should().BeNull();
        raised.Should().NotBeNull("mode {0}: the provider asked for approval and the user was never shown it", mode);
        raised!.Message.Should().Contain("microsoft_learn", "the banner names the server that receives the call");
    }
#pragma warning restore MEAI001

    // ── ConfigureChatClientPipeline: the public middleware seam ─────────────────

    [Fact]
    public async Task A_hosts_own_middleware_is_invoked_on_every_turn()
    {
        // The documented extension point. A seam that is never called is worse than no seam: the
        // host believes their logging, caching or extra guardrail is running.
        var seen = 0;

        await using var host = MentorHostFixture.Create(configure: o =>
            o.ConfigureChatClientPipeline = b => b.Use(inner => new CountingChatClient(inner, () => seen++)));

        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        seen.Should().BeGreaterThan(0, "ConfigureChatClientPipeline is documented as applied to the pipeline");
    }

    [Fact]
    public async Task The_readme_example_UseLogging_builds_and_answers()
    {
        // The READMEs' own example is `b => b.UseLogging()`. MEAI resolves the logger factory from the
        // builder's service provider, and the pipeline was built without one — so the documented line
        // made every coordinator build fail, and the user saw "The assistant could not start".
        await using var host = MentorHostFixture.Create(configure: o =>
            o.ConfigureChatClientPipeline = b => b.UseLogging());

        host.Chat!.Say("ok");
        var error = await host.RunTurnAsync("hello");

        error.Should().BeNull();
        host.Chat.CallCount.Should().BeGreaterThan(0, "the coordinator was built and called");
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("could not be built"),
            "the pipeline's services come from the host");
    }

    [Fact]
    public async Task A_host_that_configures_no_pipeline_is_unaffected()
    {
        // The control: the seam being null is the normal case and must cost nothing.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");

        (await host.RunTurnAsync("hello")).Should().BeNull();
    }

    // ── ObservabilitySourceName ────────────────────────────────────────────────

    [Fact]
    public async Task Spans_are_emitted_under_the_configured_source_name()
    {
        // The host wires its exporter with .AddSource("<name>"). If the library published under a
        // different name the exporter would receive nothing at all, and the only symptom is an
        // empty dashboard that looks like "no traffic" rather than "wrong name".
        const string sourceName = "AcmeMentor";
        var captured = new List<string>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => captured.Add(a.Source.Name),
        };
        ActivitySource.AddActivityListener(listener);

        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = sourceName;
        });

        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        captured.Should().NotBeEmpty($"the host asked for spans under '{sourceName}'");
        captured.Should().OnlyContain(n => n == sourceName);
    }

    [Fact]
    public void The_default_source_name_is_the_documented_one()
    {
        // Both READMEs and the ROADMAP tell hosts to write .AddSource("MentorAgent").
        new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] }
            .ObservabilitySourceName.Should().Be("MentorAgent");
    }

    // ── IncludeWorkflowExceptionDetails ────────────────────────────────────────

    [Fact]
    public void Workflow_exception_details_are_off_by_default()
    {
        // Documented "Do NOT enable in production": it puts an agent's exception message into the
        // reply the user reads, which is where a connection string goes to be screenshotted. The
        // default is the only thing protecting a host that never read the option.
        new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] }
            .IncludeWorkflowExceptionDetails.Should().BeFalse();
    }

    // ── EnableOutputSafetyCheck ────────────────────────────────────────────────

    [Fact]
    public void Output_moderation_is_off_by_default_because_it_costs_a_call_per_reply()
    {
        // Documented as adding one lightweight model call per reply, and as buffering the answer so
        // nothing streams that turn. Both are things a host must choose, not inherit.
        new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] }
            .EnableOutputSafetyCheck.Should().BeFalse();
    }

    [Fact]
    public async Task A_custom_output_guardrail_takes_precedence_over_the_built_in_check()
    {
        // Stated in the option's own documentation. If both ran, a host with a custom moderator
        // would pay for two model calls per reply and never know.
        var customRan = 0;

        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.EnableOutputSafetyCheck = true;
            o.OutputGuardrail = (_, _) => { customRan++; return Task.FromResult(true); };
        });

        host.Chat!.Say("a perfectly ordinary answer");
        await host.RunTurnAsync("hello");

        customRan.Should().BeGreaterThan(0, "the custom guardrail is the one that runs");
        host.Chat!.CallCount.Should().Be(1,
            "the built-in check must not also fire — that would be a second paid call per reply");
    }

    /// <summary>Counts how many requests pass through, to prove a host's middleware is in the chain.</summary>
    private sealed class CountingChatClient(IChatClient inner, Action onCall) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            onCall();
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            onCall();
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }
}
