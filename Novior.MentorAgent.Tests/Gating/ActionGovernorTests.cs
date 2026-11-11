using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 1549 — an action can ship off (<c>[MentorAction(Enabled = false)]</c>), and the application's
/// <see cref="IMentorActionGovernor"/> decides from outside the code whether each action is on: asked once as a session
/// builds its tools, with the session's id, and again before every call. An action that is off is offered to no agent,
/// described to no model, and refused at the call; one the governor turns on runs. A governor that fails stops the
/// action.
///
/// <para>
/// In the test application <c>archive_order</c> (the coordinator's), <c>reroute_shipment</c> (the ShippingAgent's) and
/// <c>forecast_next_quarter</c> (the Data Analyst's) ship off; everything else ships on.
/// </para>
/// </summary>
public sealed class ActionGovernorTests
{
    private static readonly string[] ShippedOff = ["archive_order", "reroute_shipment", "forecast_next_quarter"];

    private static readonly string[] OffDescriptions =
        ["Archives an order", "Reroutes a shipment", "Forecasts the next quarter"];

    private static MentorHostFixture Create(RecordingActionObserver observer, IMentorActionGovernor? governor,
        Action<MentorOptions>? configure = null, bool withEmbeddings = false, IMentorActionPolicy? policy = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            if (governor is not null) s.AddSingleton(governor);
            if (policy is not null) s.AddMentorActionPolicy(policy);
        }, withEmbeddings: withEmbeddings);

    private static Task FlushAsync(MentorHostFixture host) => host.Get<MentorActionDecisions>().FlushAsync();

    private static ChatResponse Text(MentorHostFixture host, string text)
        => new(new ChatMessage(ChatRole.Assistant, text)) { ModelId = host.Chat!.ModelId };

    private static ChatResponse Invoke(MentorHostFixture host, string name, Dictionary<string, object?> args)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]))
        {
            ModelId = host.Chat!.ModelId,
        };

    private static bool IsMember(FakeChatClient.RecordedCall call, string role)
        => (call.Instructions ?? "").Contains($"Your role in the team is: **{role}**", StringComparison.Ordinal);

    /// <summary>The coordinator asks the team; the Data Analyst speaks, then the Strategist approves.</summary>
    private static Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> AskTheTeam(MentorHostFixture host)
        => (messages, options) =>
        {
            var instructions = options?.Instructions ?? "";
            var tools = options?.Tools?.Select(t => t.Name).ToList() ?? [];
            var answered = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().Any() == true;

            if (instructions.Contains("Your role in the team is: **Data Analyst**", StringComparison.Ordinal))
                return Text(host, "Revenue is steady.");
            if (instructions.Contains("Your role in the team is: **Strategist**", StringComparison.Ordinal))
                return Text(host, "Keep the budget. [TEAM:APPROVED]");
            if (tools.Contains("business_analysis_team"))
                return answered ? Text(host, "relayed") : Invoke(host, "business_analysis_team", new() { ["task"] = "review the quarter" });
            return Text(host, "SAFE");
        };

    /// <summary>What the model was told about its calls: the tool results, and in Native mode the refusals' reasons.</summary>
    private static IReadOnlyList<string> WhatTheModelRead(FakeChatClient chat)
        => [.. chat.Calls
               .SelectMany(c => c.Messages)
               .SelectMany(m => m.Contents)
               .Select(c => c switch
               {
                   FunctionResultContent r => r.Result?.ToString(),
                   ToolApprovalResponseContent a => a.Reason,
                   _ => null,
               })
               .OfType<string>()];

    /// <summary>A turn to the ShippingAgent, then a turn to the team, in one session.</summary>
    private static async Task RunEveryAgentAsync(MentorHostFixture host, string specialistTool)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "where is order 7?", "shipping", specialistTool,
            new() { ["orderId"] = 7 }));
        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        host.Chat.Respond(AskTheTeam(host));
        (await host.RunTurnAsync("review the quarter", deadline.Token)).Should().BeNull();
    }

    // ── enabled.off-by-default ──────────────────────────────────────────────────────

    [Theory]
    [Trait("Rule", "enabled.off-by-default")]
    [Trait("Rule", "enabled.external-decides")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_action_shipped_off_is_offered_to_no_agent_and_described_to_no_model(bool withSilentGovernor)
    {
        var governor = withSilentGovernor ? new TestActionGovernor() : null;
        await using var host = Create(new RecordingActionObserver(), governor);
        host.Auth.SignIn("ada", "Manager", "Admin");   // every role: nothing is left out for one

        await RunEveryAgentAsync(host, "get_tracking_status");

        var calls = host.Chat!.Calls;
        calls.Where(c => c.ToolNames.Contains("count_open_orders")).Should().NotBeEmpty("the coordinator ran");
        calls.Where(c => c.ToolNames.Contains("get_tracking_status")).Should().NotBeEmpty("the specialist ran");
        calls.Where(c => IsMember(c, "Data Analyst")).Should().NotBeEmpty("the team ran. Warnings: {0}", host.Logs.WarningText);
        calls.Should().AllSatisfy(c =>
        {
            c.ToolNames.Should().NotContain(ShippedOff);
            foreach (var text in ShippedOff.Concat(OffDescriptions))
                ((c.Instructions ?? "") + c.SystemText).Should().NotContain(text);
        });
        host.Get<TestShippingAgent>().Invoked.Should().Equal(nameof(TestShippingAgent.GetTrackingStatus));

        // The catalogue still lists them: it says what the code says.
        var catalog = host.Get<IMentorActionCatalog>().GetActions();
        catalog.Where(a => !a.EnabledByDefault).Select(a => a.ToolName).Should().BeEquivalentTo(ShippedOff);
        catalog.Where(a => !a.EnabledByDefault).Should().AllSatisfy(a => a.OnAgentCard.Should().BeFalse());

        if (governor is not null)
        {
            // Asked, answered null: the code's value stands, for every action of every agent, once for the session.
            // A skill's method is the coordinator's and a member's: asked once as each.
            var offers = governor.Seen.Where(r => r.Stage == MentorActionStage.Offer).ToList();
            offers.Select(r => (r.ToolName, r.AgentKind)).Should().OnlyHaveUniqueItems();
            offers.Select(r => r.ToolName).Should().Contain(ShippedOff)
                .And.Contain(["count_open_orders", "get_tracking_status", "read_quarter_revenue"]);
            governor.Seen.Select(r => r.ToolName).Should().NotContain(
                ["navigate_to", "remember", "forget_all", "load_skill", "read_skill_resource", "route_to_specialist",
                 "invoke_ui_action", "business_analysis_team"],
                "MentorAgent's own tools are not the governor's to decide");
        }
    }

    [Fact]
    [Trait("Rule", "enabled.off-by-default")]
    public async Task Tool_filtering_and_its_fallback_never_send_an_action_shipped_off()
    {
        await using var host = Create(new RecordingActionObserver(), null, o =>
        {
            o.EnableToolFiltering = true;
            o.ToolFilterMinScore = 0.99f;   // nothing clears it: the zero-match fallback sends the ungated actions
        }, withEmbeddings: true);
        host.Auth.SignIn("ada", "Manager", "Admin");
        host.Chat!.SayRepeatedly("ok", times: 2);

        (await host.RunTurnAsync("something unrelated")).Should().BeNull();

        host.Chat.Calls.Should().Contain(c => c.ToolNames.Contains("count_open_orders"), "the control: the fallback sent the ungated actions");
        host.Chat.Calls.Should().AllSatisfy(c => c.ToolNames.Should().NotContain("archive_order"));
    }

    // ── enabled.external-decides ────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "enabled.external-decides")]
    [Trait("Rule", "surface.governor-session")]
    public async Task The_governor_turns_actions_on_and_off_for_every_agent_and_names_the_session_each_time()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Enabled = r => r.ToolName switch
            {
                "archive_order" or "reroute_shipment" or "forecast_next_quarter" => true,
                "count_open_orders" or "get_tracking_status"                    => false,
                _                                                               => null,
            },
        };
        await using var host = Create(observer, governor);
        host.Auth.SignIn("ada", "Manager", "Admin");
        host.Chat!.CallTool("archive_order", new { orderId = 7 }).Say("Archived.");

        (await host.RunTurnAsync("archive order 7")).Should().BeNull();
        await RunEveryAgentAsync(host, "reroute_shipment");
        await FlushAsync(host);

        // The coordinator: on is offered and runs, off is not offered.
        var coordinator = host.Chat.Calls.Where(c => c.ToolNames.Contains("show_order_card")).ToList();
        coordinator.Should().NotBeEmpty().And.AllSatisfy(c =>
            c.ToolNames.Should().Contain("archive_order").And.NotContain("count_open_orders"));
        host.Get<TestOrderService>().Invoked.Should().Equal("ArchiveOrder(7)");
        observer.Steps("archive_order").Should().Equal("Allowed/allowed", "Executed/completed");

        // The specialist and the team member: the same.
        var specialist = host.Chat.Calls.Where(c => c.ToolNames.Contains("reroute_shipment")).ToList();
        specialist.Should().NotBeEmpty().And.AllSatisfy(c => c.ToolNames.Should().NotContain("get_tracking_status"));
        host.Get<TestShippingAgent>().Invoked.Should().Equal(nameof(TestShippingAgent.RerouteShipment));
        host.Chat.Calls.Where(c => IsMember(c, "Data Analyst")).Should().NotBeEmpty()
            .And.AllSatisfy(c => c.ToolNames.Should().Contain("forecast_next_quarter"));

        // Asked once for the session as it was built, for every action of every agent, with what each one is.
        var offers = governor.Seen.Where(r => r.Stage == MentorActionStage.Offer).ToList();
        offers.Select(r => (r.ToolName, r.AgentKind)).Should().OnlyHaveUniqueItems("the session is built once, for three turns");
        offers.Single(r => r.ToolName == "archive_order").AgentKind.Should().Be(MentorAgentKind.Coordinator);
        offers.Single(r => r.ToolName == "reroute_shipment").Should().Match<MentorActionRequest>(r =>
            r.AgentKind == MentorAgentKind.Specialist && r.AgentName == "ShippingAgent");
        offers.Single(r => r.ToolName == "forecast_next_quarter").Should().Match<MentorActionRequest>(r =>
            r.AgentKind == MentorAgentKind.TeamMember && r.AgentName == "Data Analyst");
        offers.Should().AllSatisfy(r =>
        {
            r.Arguments.Should().BeNull();
            r.CallId.Should().BeNull();
            r.Surface.Should().Be(MentorActionSurface.InProcess);
            r.User!.Identity!.Name.Should().Be("ada");
        });

        // And before every call, with the call.
        var call = governor.Asked("archive_order", MentorActionStage.Call).Should().ContainSingle().Subject;
        call.Arguments!["orderId"]!.ToString().Should().Be("7");
        call.CallId.Should().NotBeNullOrEmpty();
        governor.Asked("reroute_shipment", MentorActionStage.Call).Should().ContainSingle()
            .Which.AgentKind.Should().Be(MentorAgentKind.Specialist);

        // One session, named the same every time, and as the decisions name it.
        var session = observer.Seen.First(d => d.ToolName == "archive_order").SessionId;
        session.Should().NotBeNullOrEmpty();
        governor.Seen.Should().AllSatisfy(r => r.SessionId.Should().Be(session));
    }

    [Theory]
    [Trait("Rule", "enabled.external-decides")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_session_keeps_the_tools_it_was_built_with_and_an_action_turned_off_meanwhile_is_refused_at_the_call(
        MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var changed = false;
        var governor = new TestActionGovernor
        {
            Enabled = r => !changed ? null : r.ToolName switch
            {
                "issue_refund"  => false,
                "archive_order" => true,
                _               => null,
            },
        };
        await using var host = Create(observer, governor, o => o.HitlMode = mode);
        // A banner is counted, and confirmed so that nothing waits on it.
        var banners = 0;
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            banners++;
            _ = state.ConfirmAsync(req.ActionId);
        };
        host.Chat!.Say("Hello.");
        (await host.RunTurnAsync("hello")).Should().BeNull();

        changed = true;
        var from = host.Chat.CallCount;
        host.Chat.CallTool("issue_refund", new { orderId = 7 }).Say("It is turned off.");
        (await host.RunTurnAsync("refund order 7")).Should().BeNull();
        await FlushAsync(host);

        // What a session offers is decided as it is built: a configuration changed meanwhile counts from the next one.
        host.Chat.Calls.Skip(from).Where(c => c.ToolNames.Contains("show_order_card")).Should().NotBeEmpty()
            .And.AllSatisfy(c => c.ToolNames.Should().Contain("issue_refund").And.NotContain("archive_order"));

        // The call is asked again: off now, so no banner and nothing run.
        banners.Should().Be(0, "mode {0}: an action that is off asks nobody to confirm it", mode);
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("issue_refund").Should().Equal(["Denied/disabled"], "mode {0}", mode);
        WhatTheModelRead(host.Chat).Should().Contain(t => t.Contains("this action is turned off"));
        host.Logs.Records.Should().Contain(r => r.Message.Contains("Tool 'issue_refund' is off — not run."));
    }

    [Theory]
    [Trait("Rule", "enabled.external-decides")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_governor_that_fails_stops_the_action_as_it_is_offered_and_as_it_is_called(bool throws)
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Before = async (r, ct) =>
            {
                if (r.Stage == MentorActionStage.Offer && r.ToolName == "show_order_card")
                    throw new InvalidOperationException("console unreachable 42");
                if (r.Stage != MentorActionStage.Call || r.ToolName != "count_open_orders") return;
                if (throws) throw new InvalidOperationException("console unreachable 42");
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        await using var host = Create(observer, governor, o => o.ActionPolicyTimeout = TimeSpan.FromMilliseconds(200));
        host.Chat!.CallTool("count_open_orders").Say("It could not be checked.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        host.Chat.Calls.First().ToolNames.Should().NotContain("show_order_card", "a governor that fails leaves the action out")
            .And.Contain("count_open_orders");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("PolicyFailed/governor-failed");
        observer.Seen.Single().ExceptionType.Should().Be(throws ? typeof(InvalidOperationException).FullName : null);
        host.Logs.HasWarning(throws ? "threw (InvalidOperationException) for 'count_open_orders'" : "did not answer within 0.2 s")
            .Should().BeTrue(host.Logs.WarningText);
        host.Logs.HasWarning("threw (InvalidOperationException) for 'show_order_card' — the action is not offered")
            .Should().BeTrue(host.Logs.WarningText);
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("console unreachable 42"), "never the governor's message (BUG-148)");
        WhatTheModelRead(host.Chat).Should().Contain(t => t.Contains("the check before this action could not be completed"));
    }

    [Fact]
    [Trait("Rule", "enabled.external-decides")]
    public async Task There_is_one_governor_and_two_registered_by_hand_turn_every_action_off()
    {
        var services = new ServiceCollection();
        services.AddMentorActionGovernor<TestActionGovernor>();
        var second = () => services.AddMentorActionGovernor<SecondActionGovernor>();
        second.Should().Throw<InvalidOperationException>().WithMessage("*already registered*TestActionGovernor*one per application*");

        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            s.AddSingleton<IMentorActionGovernor>(new TestActionGovernor());
            s.AddSingleton<IMentorActionGovernor>(new SecondActionGovernor());
        });
        host.Chat!.Say("Hello.");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        host.Chat.Calls.First().ToolNames.Should().NotContain(["count_open_orders", "show_order_card", "get_active_orders"]);
        host.Logs.Records.Where(r => r.Level == LogLevel.Error && r.Message.Contains("2 IMentorActionGovernor services are registered"))
            .Should().ContainSingle("said once, not once per action");
    }

    // ── roles.external-replace (story 1546) ─────────────────────────────────────────

    [Fact]
    [Trait("Rule", "roles.external-replace")]
    public async Task The_governors_roles_replace_the_codes_for_the_offer_and_at_every_call()
    {
        var observer = new RecordingActionObserver();
        var auditorsLedger = false;
        var governor = new TestActionGovernor
        {
            Roles = r => r.ToolName switch
            {
                "export_ledger"     => auditorsLedger ? ["Auditor"] : [],
                "count_open_orders" => ["Auditor"],
                _                   => null,
            },
        };
        await using var host = Create(observer, governor);
        host.Auth.SignIn("bob");   // signed in, with no role
        host.Chat!.CallTool("export_ledger").Say("Exported.");

        (await host.RunTurnAsync("export the ledger")).Should().BeNull();
        auditorsLedger = true;   // the console narrows it during the session
        host.Chat.CallTool("export_ledger").CallTool("count_open_orders").Say("Not for you.");
        (await host.RunTurnAsync("export it again, and count the open orders")).Should().BeNull();
        await FlushAsync(host);

        // Offered by the roles the governor gave as the session was built: the ledger to bob, who is no Admin, and not
        // the count, which the code gives to everybody.
        host.Chat.Calls.Where(c => c.ToolNames.Contains("show_order_card")).Should().NotBeEmpty().And.AllSatisfy(c =>
            c.ToolNames.Should().Contain("export_ledger").And.NotContain("count_open_orders"));

        // Checked by the governor's answer at each call: nobody's the first time, the Auditor's the second.
        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.ExportLedger));
        observer.Steps("export_ledger").Should().Equal("Allowed/allowed", "Executed/completed", "Denied/role-missing");
        observer.Steps("count_open_orders").Should().Equal("Denied/role-missing");
        observer.Seen.Should().AllSatisfy(d => d.RolesFromOutside.Should().BeTrue());
        observer.Seen.First(d => d.ToolName == "export_ledger").RequiredRoles.Should().BeEmpty();
        observer.Seen.Last(d => d.ToolName == "export_ledger").RequiredRoles.Should().Equal("Auditor");
        WhatTheModelRead(host.Chat).Should().Contain(t => t.Contains("Auditor"));

        // Asked with the code's roles, as the session was built and at every call.
        governor.RolesSeen.Where(r => r.ToolName == "export_ledger").Select(r => r.Stage)
            .Should().Equal(MentorActionStage.Offer, MentorActionStage.Call, MentorActionStage.Call);
        governor.RolesSeen.Where(r => r.ToolName == "export_ledger").Should().AllSatisfy(r => r.RequiredRoles.Should().Equal("Admin"));
    }

    [Fact]
    [Trait("Rule", "roles.external-replace")]
    public async Task The_governors_roles_decide_what_a_specialist_and_a_team_member_offer()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Roles = r => r.ToolName switch
            {
                "get_tracking_status" => ["Manager"],
                "cancel_shipment"     => [],
                "approve_budget_line" => [],
                _                     => null,
            },
        };
        await using var host = Create(observer, governor);
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);
        host.Auth.SignIn("bob");   // no role: the code gives him the tracking and neither the cancellation nor the approval

        await RunEveryAgentAsync(host, "cancel_shipment");

        var specialist = host.Chat!.Calls.Where(c => c.ToolNames.Contains("cancel_shipment")).ToList();
        specialist.Should().NotBeEmpty().And.AllSatisfy(c => c.ToolNames.Should().NotContain("get_tracking_status"));
        host.Get<TestShippingAgent>().Invoked.Should().Equal(nameof(TestShippingAgent.CancelShipment));
        observer.Steps("cancel_shipment").Should().Contain("Executed/completed");
        host.Chat.Calls.Where(c => IsMember(c, "Data Analyst")).Should().NotBeEmpty()
            .And.AllSatisfy(c => c.ToolNames.Should().Contain("approve_budget_line"));
        governor.RolesSeen.Single(r => r.ToolName == "get_tracking_status").Should().Match<MentorActionRequest>(r =>
            r.Stage == MentorActionStage.Offer && r.AgentKind == MentorAgentKind.Specialist && r.RequiredRoles.Count == 0);
    }

    [Theory]
    [Trait("Rule", "roles.external-replace")]
    [Trait("Rule", "roles.checked-before-confirmation")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task The_governors_roles_are_checked_before_the_banner_and_again_after_it(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        // Offered to everybody as the session is built; at the call, the Manager's — then the Admin's.
        string[] refundRoles = ["Manager"];
        var governor = new TestActionGovernor
        {
            Roles = r => r.ToolName != "issue_refund" ? null : r.Stage == MentorActionStage.Offer ? [] : refundRoles,
        };
        await using var host = Create(observer, governor, o => o.HitlMode = mode);
        var state = host.Get<IMentorStateService>();
        var banners = 0;
        state.OnConfirmationRequired += req =>
        {
            banners++;
            refundRoles = ["Admin"];   // the console narrows it while the banner waits
            _ = state.ConfirmAsync(req.ActionId);
        };

        // bob holds no role: offered the refund, refused at the call by the governor's roles then, and asked nothing.
        host.Auth.SignIn("bob");
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("Not for you.");
        (await host.RunTurnAsync("refund order 7")).Should().BeNull();
        await FlushAsync(host);
        banners.Should().Be(0, "mode {0}: a user without the governor's role is never asked to confirm", mode);

        // ada is a Manager: asked, and refused after her answer by the roles the governor gives then.
        host.Auth.SignIn("ada", "Manager");
        host.Chat.CallTool("issue_refund", new { orderId = 7 }).Say("No longer yours.");
        (await host.RunTurnAsync("refund order 7")).Should().BeNull();
        await FlushAsync(host);

        banners.Should().Be(1);
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("issue_refund").Should().Equal(
            ["Denied/role-missing", "Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Denied/role-missing"],
            "mode {0}", mode);
        observer.Seen.Should().AllSatisfy(d => d.RolesFromOutside.Should().BeTrue());
    }

    [Fact]
    [Trait("Rule", "roles.external-replace")]
    public async Task A_governor_that_fails_on_the_roles_stops_the_action_as_it_is_offered_and_as_it_is_called()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            BeforeRoles = (r, _) => (r.Stage, r.ToolName) is (MentorActionStage.Offer, "show_order_card")
                                                         or (MentorActionStage.Call, "count_open_orders")
                ? throw new InvalidOperationException("console unreachable 42")
                : Task.CompletedTask,
        };
        await using var host = Create(observer, governor);
        host.Chat!.CallTool("count_open_orders").Say("It could not be checked.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        host.Chat.Calls.First().ToolNames.Should().NotContain("show_order_card").And.Contain("count_open_orders");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("PolicyFailed/governor-failed");
        observer.Seen.Single().ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("console unreachable 42"));
    }

    // ── confirm.external-decides (story 1547) ───────────────────────────────────────

    /// <summary>The tools a turn asked to confirm, in order; each banner is confirmed.</summary>
    private static List<string> ConfirmEveryBanner(MentorHostFixture host)
    {
        var banners = new List<string>();
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req =>
        {
            banners.Add(req.ToolName);
            _ = state.ConfirmAsync(req.ActionId);
        };
        return banners;
    }

    private static readonly string[] Confirmed =
        ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed"];

    private static readonly string[] RunUnasked = ["Allowed/allowed", "Executed/completed"];

    [Theory]
    [Trait("Rule", "confirm.external-decides")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task The_governor_takes_away_the_codes_confirmation_and_requires_one_the_code_does_not_ask_for(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Confirmation = r => r.ToolName switch
            {
                "issue_refund"    => MentorConfirmationChoice.None,       // the code asks
                "show_order_card" => MentorConfirmationChoice.Required,   // the code does not
                _                 => MentorConfirmationChoice.Code,
            },
        };
        await using var host = Create(observer, governor, o => o.HitlMode = mode);
        var banners = ConfirmEveryBanner(host);
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).CallTool("show_order_card", new { orderId = 7 }).Say("Done.");

        (await host.RunTurnAsync("refund order 7 and show it")).Should().BeNull();
        await FlushAsync(host);

        banners.Should().Equal(["show_order_card"], "mode {0}", mode);
        host.Get<TestOrderService>().Invoked.Should().Equal(
            nameof(TestOrderService.IssueRefundAsync), nameof(TestOrderService.ShowOrderCard));
        observer.Steps("issue_refund").Should().Equal(RunUnasked, "mode {0}", mode);
        observer.Steps("show_order_card").Should().Equal(Confirmed, "mode {0}", mode);
        observer.Seen.Where(d => d.ToolName is "issue_refund" or "show_order_card")
            .Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeTrue());

        // Asked as the session was built and at the call, with the code's confirmation.
        governor.ConfirmationSeen.Where(r => r.ToolName is "issue_refund" or "show_order_card")
            .Select(r => (r.ToolName, r.Stage, r.RequiresConfirmation)).Should().BeEquivalentTo(
            [
                ("issue_refund", MentorActionStage.Offer, true), ("issue_refund", MentorActionStage.Call, true),
                ("show_order_card", MentorActionStage.Offer, false), ("show_order_card", MentorActionStage.Call, false),
            ]);
    }

    [Theory]
    [Trait("Rule", "confirm.external-decides")]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_confirmation_added_counts_at_once_and_one_taken_away_from_the_next_session(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        // The code's word as the session is built; by the call the console has changed its mind about both.
        var governor = new TestActionGovernor
        {
            Confirmation = r => (r.Stage, r.ToolName) switch
            {
                (MentorActionStage.Call, "issue_refund")    => MentorConfirmationChoice.None,
                (MentorActionStage.Call, "show_order_card") => MentorConfirmationChoice.Required,
                _                                           => MentorConfirmationChoice.Code,
            },
        };
        await using var host = Create(observer, governor, o => o.HitlMode = mode);
        var banners = ConfirmEveryBanner(host);
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).CallTool("show_order_card", new { orderId = 7 }).Say("Done.");

        (await host.RunTurnAsync("refund order 7 and show it")).Should().BeNull();
        await FlushAsync(host);

        banners.Should().Equal(["issue_refund", "show_order_card"], "mode {0}: narrowing counts at once, widening does not", mode);
        observer.Steps("issue_refund").Should().Equal(Confirmed, "mode {0}", mode);
        observer.Steps("show_order_card").Should().Equal(Confirmed, "mode {0}", mode);
        observer.Seen.Where(d => d.ToolName == "issue_refund")
            .Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeFalse("the code's confirmation was asked"));
        observer.Seen.Where(d => d.ToolName == "show_order_card")
            .Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeTrue());
    }

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    public async Task In_Native_mode_a_confirmation_the_governor_requires_is_asked_even_when_an_auto_approval_rule_approves()
    {
        var observer = new RecordingActionObserver();
        // Required for the session only, for the card the code does not confirm; at the call only, for the refund it does.
        var governor = new TestActionGovernor
        {
            Confirmation = r => (r.Stage, r.ToolName) switch
            {
                (MentorActionStage.Offer, "show_order_card") => MentorConfirmationChoice.Required,
                (MentorActionStage.Call, "issue_refund")     => MentorConfirmationChoice.Required,
                _                                            => MentorConfirmationChoice.Code,
            },
        };
        await using var host = Create(observer, governor, o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.AutoApprovalRules = [ctx => ValueTask.FromResult(ctx.FunctionCallContent.Name is "issue_refund" or "show_order_card")];
        });
        var banners = ConfirmEveryBanner(host);
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).CallTool("show_order_card", new { orderId = 7 }).Say("Done.");

        (await host.RunTurnAsync("refund order 7 and show it")).Should().BeNull();
        await FlushAsync(host);

        // The rule is the code's, and lets both through; the gate asks for what the governor requires all the same.
        banners.Should().Equal("issue_refund", "show_order_card");
        observer.Steps("issue_refund").Should().Equal(Confirmed);
        observer.Steps("show_order_card").Should().Equal(Confirmed);
    }

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    public async Task In_Native_mode_an_auto_approval_rule_still_approves_the_codes_confirmation_with_a_governor()
    {
        var observer = new RecordingActionObserver();
        await using var host = Create(observer, new TestActionGovernor(), o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.AutoApprovalRules = [ctx => ValueTask.FromResult(ctx.FunctionCallContent.Name == "issue_refund")];
        });
        var banners = ConfirmEveryBanner(host);
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("Refunded.");

        (await host.RunTurnAsync("refund order 7")).Should().BeNull();
        await FlushAsync(host);

        banners.Should().BeEmpty();
        observer.Steps("issue_refund").Should().Equal("Allowed/auto-approved", "Executed/completed");
    }

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    public async Task A_specialist_runs_an_action_without_its_codes_confirmation_when_the_governor_takes_it_away()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Confirmation = r => r.ToolName == "cancel_shipment" ? MentorConfirmationChoice.None : MentorConfirmationChoice.Code,
        };
        await using var host = Create(observer, governor);
        var banners = ConfirmEveryBanner(host);
        host.Auth.SignIn("ada", "Manager");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        host.Chat!.Respond(ActionDecisionTests.Delegating(host, "where is order 7?", "shipping", "cancel_shipment",
            new() { ["orderId"] = 7 }));

        (await host.RunTurnAsync("where is order 7?", deadline.Token)).Should().BeNull();
        await FlushAsync(host);

        banners.Should().BeEmpty();
        host.Get<TestShippingAgent>().Invoked.Should().Equal(nameof(TestShippingAgent.CancelShipment));
        observer.Steps("cancel_shipment").Should().Equal(RunUnasked);
        observer.Seen.Where(d => d.ToolName == "cancel_shipment")
            .Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeTrue());
        governor.ConfirmationSeen.Should().Contain(r => r.ToolName == "cancel_shipment"
            && r.Stage == MentorActionStage.Offer && r.AgentKind == MentorAgentKind.Specialist && r.RequiresConfirmation);
    }

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    [Trait("Rule", "confirm.predicate-adds")]
    public async Task The_governor_takes_away_a_confirmation_the_predicate_asks_for()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Confirmation = r => r.ToolName == "show_order_card" ? MentorConfirmationChoice.None : MentorConfirmationChoice.Code,
        };
        // Neither asks of itself: the predicate asks for both.
        await using var host = Create(observer, governor,
            o => o.RequiresApproval = name => name is "show_order_card" or "get_active_orders");
        var banners = ConfirmEveryBanner(host);
        host.Chat!.CallTool("show_order_card", new { orderId = 7 }).CallTool("get_active_orders").Say("Done.");

        (await host.RunTurnAsync("show order 7 and the active orders")).Should().BeNull();
        await FlushAsync(host);

        banners.Should().Equal("get_active_orders");
        observer.Steps("show_order_card").Should().Equal(RunUnasked);
        observer.Steps("get_active_orders").Should().Equal(Confirmed);
        observer.Seen.Where(d => d.ToolName == "show_order_card")
            .Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeTrue());
    }

    [Theory]
    [Trait("Rule", "confirm.external-decides")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_governor_that_leaves_the_confirmation_to_the_code_changes_nothing(bool withGovernor)
    {
        var observer = new RecordingActionObserver();
        var governor = withGovernor ? new TestActionGovernor() : null;
        await using var host = Create(observer, governor);
        var banners = ConfirmEveryBanner(host);
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).CallTool("show_order_card", new { orderId = 7 }).Say("Done.");

        (await host.RunTurnAsync("refund order 7 and show it")).Should().BeNull();
        await FlushAsync(host);

        banners.Should().Equal("issue_refund");
        observer.Steps("issue_refund").Should().Equal(Confirmed);
        observer.Steps("show_order_card").Should().Equal(RunUnasked);
        observer.Seen.Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeFalse());
        if (withGovernor)
            governor!.ConfirmationSeen.Where(r => r.Stage == MentorActionStage.Call).Select(r => r.ToolName)
                .Should().Equal("issue_refund", "show_order_card");
    }

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    [Trait("Rule", "confirm.master-switch")]
    public async Task With_confirmations_off_a_confirmation_the_governor_requires_is_not_asked()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor { Confirmation = _ => MentorConfirmationChoice.Required };
        await using var host = Create(observer, governor, o => o.RequireConfirmation = false);
        var banners = ConfirmEveryBanner(host);
        host.Chat!.CallTool("show_order_card", new { orderId = 7 }).Say("Done.");

        (await host.RunTurnAsync("show order 7")).Should().BeNull();
        await FlushAsync(host);

        banners.Should().BeEmpty();
        observer.Steps("show_order_card").Should().Equal(RunUnasked);
        observer.Seen.Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeFalse());
    }

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    public async Task A_governor_that_fails_on_the_confirmation_stops_the_action_as_it_is_offered_and_as_it_is_called()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            BeforeConfirmation = (r, _) => (r.Stage, r.ToolName) is (MentorActionStage.Offer, "show_order_card")
                                                                or (MentorActionStage.Call, "count_open_orders")
                ? throw new InvalidOperationException("console unreachable 43")
                : Task.CompletedTask,
        };
        await using var host = Create(observer, governor);
        host.Chat!.CallTool("count_open_orders").Say("It could not be checked.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host);

        host.Chat.Calls.First().ToolNames.Should().NotContain("show_order_card").And.Contain("count_open_orders");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("PolicyFailed/governor-failed");
        observer.Seen.Single().ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("console unreachable 43"));
    }

    // ── describe.external-replace (story 1548) ──────────────────────────────────────

    [Fact]
    [Trait("Rule", "describe.external-replace")]
    public async Task The_model_reads_the_governors_description_and_the_name_and_the_schema_stay_the_codes()
    {
        var governor = new TestActionGovernor
        {
            Description = r => r.ToolName switch
            {
                "count_open_orders" => "Tells how many orders are still open",
                "cancel_order"      => "Cancels an order the customer asked to cancel",   // the code's ProactiveHint follows
                "show_order_card"   => "   ",                                             // blank: the code's
                _                   => null,
            },
        };
        await using var code = Create(new RecordingActionObserver(), null);
        await using var host = Create(new RecordingActionObserver(), governor);
        foreach (var each in new[] { code, host })
        {
            each.Auth.SignIn("ada", "Manager");
            each.Chat!.Say("ok");
            (await each.RunTurnAsync("hello")).Should().BeNull();
        }

        var before = code.Chat!.Calls.First(c => c.ToolNames.Contains("count_open_orders"));
        var after = host.Chat!.Calls.First(c => c.ToolNames.Contains("count_open_orders"));
        after.ToolNames.Should().Equal(before.ToolNames);
        after.Tool("count_open_orders")!.Description.Should().Be("Tells how many orders are still open");
        after.Tool("cancel_order")!.Description.Should().Be(
            "Cancels an order the customer asked to cancel Guidance: Suggest checking active orders first");
        foreach (var name in after.ToolNames.Where(n => n is not ("count_open_orders" or "cancel_order")))
            after.Tool(name)?.Description.Should().Be(before.Tool(name)?.Description, "{0} keeps the code's description", name);
        foreach (var name in after.ToolNames)
            after.Tool(name)?.JsonSchema.GetRawText().Should().Be(before.Tool(name)?.JsonSchema.GetRawText(),
                "{0}'s schema never changes", name);

        // Asked with the code's description, as the session was built.
        governor.DescriptionSeen.Single(r => r.ToolName == "count_open_orders").Should().Match<MentorActionRequest>(r =>
            r.Stage == MentorActionStage.Offer && r.Description == "Counts open orders");
    }

    [Fact]
    [Trait("Rule", "describe.external-replace")]
    public async Task A_specialist_and_a_team_member_read_the_governors_description()
    {
        var governor = new TestActionGovernor
        {
            Description = r => r.ToolName switch
            {
                "get_tracking_status"  => "Tells where a parcel is now",
                "read_quarter_revenue" => "Reads the revenue booked this quarter",
                _                      => null,
            },
        };
        await using var host = Create(new RecordingActionObserver(), governor);
        host.Auth.SignIn("ada", "Manager");

        await RunEveryAgentAsync(host, "get_tracking_status");

        host.Chat!.Calls.Where(c => c.ToolNames.Contains("get_tracking_status")).Should().NotBeEmpty()
            .And.AllSatisfy(c => c.Tool("get_tracking_status")!.Description.Should().Be("Tells where a parcel is now"));
        host.Chat.Calls.Where(c => IsMember(c, "Data Analyst")).Should().NotBeEmpty()
            .And.AllSatisfy(c => c.Tool("read_quarter_revenue")!.Description.Should().Be("Reads the revenue booked this quarter"));
    }

    [Fact]
    [Trait("Rule", "describe.external-replace")]
    public async Task What_the_user_sees_keeps_the_codes_description()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Description = r => r.ToolName == "issue_refund" ? "Sends the money back to the customer" : null,
        };
        var policy = new TestActionPolicy();
        await using var host = Create(observer, governor, policy: policy);
        var state = host.Get<IMentorStateService>();
        var banners = new List<string>();
        state.OnConfirmationRequired += req =>
        {
            banners.Add(req.Message);
            _ = state.ConfirmAsync(req.ActionId);
        };
        host.Chat!.CallTool("issue_refund", new { orderId = 7 }).Say("Refunded.");

        (await host.RunTurnAsync("refund order 7")).Should().BeNull();
        await FlushAsync(host);

        // The model reads the governor's; the user confirms what the programmer wrote.
        host.Chat.Calls.First().Tool("issue_refund")!.Description.Should().Be("Sends the money back to the customer");
        banners.Should().ContainSingle().Which.Should().StartWith("Issues a refund");
        observer.Seen.Where(d => d.ToolName == "issue_refund").Should().NotBeEmpty()
            .And.AllSatisfy(d => d.DisplayName.Should().Be("Issues a refund"));
        governor.DescriptionSeen.Should().NotBeEmpty()
            .And.OnlyContain(r => r.Stage == MentorActionStage.Offer, "the description is asked only as the tools are built");

        // The policies are asked with what the model read, the governor with the code's.
        policy.Seen.Where(s => s.Request.ToolName == "issue_refund").Select(s => s.Request.Stage)
            .Should().Contain([MentorActionStage.Offer, MentorActionStage.Call]);
        policy.Seen.Where(s => s.Request.ToolName == "issue_refund")
            .Should().AllSatisfy(s => s.Request.Description.Should().Be("Sends the money back to the customer"));
        governor.Seen.Where(r => r.ToolName == "issue_refund").Should().Contain(r => r.Stage == MentorActionStage.Call)
            .And.AllSatisfy(r => r.Description.Should().Be("Issues a refund"));
    }

    [Fact]
    [Trait("Rule", "describe.external-replace")]
    public async Task A_governor_that_fails_on_the_description_leaves_the_action_out()
    {
        var governor = new TestActionGovernor
        {
            BeforeDescription = (r, _) => r.ToolName == "show_order_card"
                ? throw new InvalidOperationException("console unreachable 44")
                : Task.CompletedTask,
        };
        await using var host = Create(new RecordingActionObserver(), governor);
        host.Chat!.Say("ok");

        (await host.RunTurnAsync("hello")).Should().BeNull();

        host.Chat.Calls.First().ToolNames.Should().NotContain("show_order_card").And.Contain("count_open_orders");
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("console unreachable 44"));
    }
}
