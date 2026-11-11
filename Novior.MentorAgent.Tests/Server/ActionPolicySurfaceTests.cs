using System.Collections.Concurrent;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Stories 311 and 153 over the real transports: the MCP server and A2A. The same <see cref="IMentorActionPolicy"/>
/// decides there as in the widget: on <c>tools/list</c> what each caller is offered, on <c>tools/call</c> whether the
/// call runs. Nobody can confirm there, so a Confirm refuses the call. The policy's services are a scope of the
/// request, never the root ones. BUG-202: the catalogue reads <c>RequiresApproval</c> for every list and every call.
/// And <see cref="IMentorActionCatalog"/> says what the MCP server and the A2A card publish, by the same rules.
/// </summary>
public sealed class ActionPolicySurfaceTests
{
    /// <summary>A scoped service of the application: one instance per scope.</summary>
    private sealed class ScopedProbe;

    private static Task FlushAsync(ServerAppFactory app) => app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

    /// <summary>ada holds Admin; eve (by header) holds no role; "-" is anonymous.</summary>
    private static Task<ServerAppFactory> McpAsync(RecordingActionObserver observer, IMentorActionPolicy? policy,
        Action<MentorOptions>? configure = null, bool a2a = false)
        => ServerAppFactory.CreateAsync(
            userId: "ada",
            roles: ["Admin"],
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
                if (a2a)
                {
                    o.A2AServerEnabled = true;
                    o.A2AServerUrl = "http://localhost/a2a";
                }
                configure?.Invoke(o);
            },
            configureServices: s =>
            {
                s.AddMentorActionObserver(observer);
                if (policy is not null) s.AddMentorActionPolicy(policy);
                s.AddScoped<ScopedProbe>();
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                    (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e =>
            {
                e.MapMentorAgentMcp();
                if (a2a) e.MapMentorAgentA2A();
            });

    private static IReadOnlyList<string> AuditLines(ServerAppFactory app, string tool) =>
        [.. app.Logs.Records
            .Where(r => r.Level == LogLevel.Information && r.Message.Contains($"tools/call '{tool}'"))
            .Select(r => r.Message)];

    // ── tools/call ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_the_mcp_server_a_Stop_runs_nothing_and_the_caller_reads_the_rule()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Verdict = _ => MentorActionVerdict.Stop("the ledger is being closed") };
        await using var app = await McpAsync(observer, policy);
        var ada = await McpTestSession.OpenAsync(app.Client);

        var (text, isError) = await ada.CallAsync("export_ledger");
        await FlushAsync(app);

        isError.Should().BeTrue();
        text.Should().Contain("Stopped by a rule: the ledger is being closed");
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("export_ledger").Should().Equal("Blocked/policy");
        observer.Seen.Single().Detail.Should().Be("the ledger is being closed");
        AuditLines(app, "export_ledger").Should().ContainSingle().Which.Should().Contain(": blocked in");

        var asked = policy.Seen.Single(s => s.Method == "BeforeActionAsync").Request;
        asked.Surface.Should().Be(MentorActionSurface.Mcp);
        asked.AgentKind.Should().BeNull();
        asked.User!.Identity!.Name.Should().Be("ada");
        asked.Arguments.Should().NotBeNull().And.BeEmpty();
        asked.RequiredRoles.Should().Equal("Admin");
    }

    [Fact]
    public async Task On_the_mcp_server_a_Confirm_refuses_the_call_with_nobody_to_confirm()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Verdict = _ => MentorActionVerdict.Confirm("over the daily limit") };
        await using var app = await McpAsync(observer, policy);
        var ada = await McpTestSession.OpenAsync(app.Client);

        var (text, isError) = await ada.CallAsync("show_order_card", new { orderId = 7 });
        await FlushAsync(app);

        isError.Should().BeTrue();
        text.Should().Contain("nobody to ask");
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("show_order_card").Should().Equal(["Denied/nobody-to-confirm"], "one code belongs to one outcome");
        observer.Seen.Single().Detail.Should().Be("over the daily limit");
        policy.Seen.Single(s => s.Method == "BeforeActionAsync").Request.Arguments!["orderId"]!.ToString().Should().Be("7");
    }

    [Fact]
    public async Task On_the_mcp_server_a_policy_that_fails_stops_the_call()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Before = (r, _) => r.Stage == MentorActionStage.Call ? throw new InvalidOperationException("down") : Task.CompletedTask,
        };
        await using var app = await McpAsync(observer, policy);
        var ada = await McpTestSession.OpenAsync(app.Client);

        (await ada.CallAsync("count_open_orders")).IsError.Should().BeTrue();
        await FlushAsync(app);

        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("PolicyFailed/policy-exception");
    }

    // ── 153: who is offered what ─────────────────────────────────────────────────

    [Fact]
    public async Task On_the_mcp_server_an_action_a_policy_does_not_allow_is_not_listed_and_not_run()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Allowed = r => r.ToolName != "count_open_orders" || r.User?.Identity?.Name == "ada" };
        await using var app = await McpAsync(observer, policy);
        var ada = await McpTestSession.OpenAsync(app.Client);
        var eve = await McpTestSession.OpenAsync(app.Client, asUser: "eve");

        (await ada.ListToolNamesAsync()).Should().Contain("count_open_orders");
        (await eve.ListToolNamesAsync()).Should().NotContain("count_open_orders").And.Contain("show_order_card");
        var (text, isError) = await eve.CallAsync("count_open_orders");
        await FlushAsync(app);

        isError.Should().BeTrue();
        text.Should().Contain("Unknown tool: 'count_open_orders'", "the same words as for a role eve lacks (BUG-101)");
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("Denied/not-authorized");
        AuditLines(app, "count_open_orders").Should().ContainSingle().Which.Should().Contain(": denied-policy in");
        policy.Seen.Where(s => s.Request.Stage == MentorActionStage.Offer).Should().AllSatisfy(s =>
        {
            s.Request.Arguments.Should().BeNull();
            s.Request.Surface.Should().Be(MentorActionSurface.Mcp);
        });
    }

    // ── BUG-202 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_action_RequiresApproval_gates_after_startup_leaves_the_mcp_server_at_once()
    {
        var observer = new RecordingActionObserver();
        var gated = false;
        await using var app = await McpAsync(observer, null, o => o.RequiresApproval = name => gated && name == "count_open_orders");
        var ada = await McpTestSession.OpenAsync(app.Client);
        (await ada.ListToolNamesAsync()).Should().Contain("count_open_orders");

        gated = true;
        var listed = await ada.ListToolNamesAsync();
        var (text, _) = await ada.CallAsync("count_open_orders");
        await FlushAsync(app);

        listed.Should().NotContain("count_open_orders", "the predicate is read for every list, not kept from the first");
        text.Should().Contain("Unknown tool: 'count_open_orders'");
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty("it would run with nobody to confirm it");
        observer.Steps("count_open_orders").Should().Equal("Denied/withheld");
    }

    // ── The policy's services ────────────────────────────────────────────────────

    [Fact]
    public async Task On_the_mcp_server_the_policy_reads_a_scope_of_the_request()
    {
        var probes = new ConcurrentQueue<(MentorActionStage Stage, ScopedProbe Probe)>();
        var policy = new TestActionPolicy
        {
            Before = (r, _) =>
            {
                probes.Enqueue((r.Stage, r.Services.GetRequiredService<ScopedProbe>()));
                return Task.CompletedTask;
            },
        };
        await using var app = await McpAsync(new RecordingActionObserver(), policy);
        var ada = await McpTestSession.OpenAsync(app.Client);

        await ada.ListToolNamesAsync();
        await ada.CallAsync("count_open_orders");
        await ada.CallAsync("count_open_orders");

        var offered = probes.Where(p => p.Stage == MentorActionStage.Offer).Select(p => p.Probe).Distinct().ToList();
        var called = probes.Where(p => p.Stage == MentorActionStage.Call).Select(p => p.Probe).Distinct().ToList();
        offered.Should().ContainSingle("one tools/list is one request, with one scope");
        called.Should().HaveCount(2, "each tools/call has a scope of its own: the root services would give one for all")
            .And.NotContain(offered);
    }

    // ── A2A ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_an_a2a_task_a_Stop_runs_nothing_and_the_policy_reads_the_tasks_scope()
    {
        var observer = new RecordingActionObserver();
        var probes = new ConcurrentQueue<ScopedProbe>();
        var policy = new TestActionPolicy
        {
            Verdict = _ => MentorActionVerdict.Stop("closed for the night"),
            Before = (r, _) =>
            {
                probes.Enqueue(r.Services.GetRequiredService<ScopedProbe>());
                return Task.CompletedTask;
            },
        };
        await using var app = await McpAsync(observer, policy, a2a: true);
        app.Chat.CallTool("count_open_orders").Say("Closed.");

        foreach (var message in new[] { "how many open orders?", "and now?" })
        {
            var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            (await A2A.A2AClientFactory.Create(card, app.Client).SendMessageAsync(message, A2A.Role.User, null, deadline.Token))
                .Should().NotBeNull();
            app.Chat.CallTool("count_open_orders").Say("Closed.");
        }
        await FlushAsync(app);

        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("count_open_orders").Should().Equal("Blocked/policy", "Blocked/policy");
        observer.Seen.Should().AllSatisfy(d => d.Surface.Should().Be(MentorActionSurface.A2A));
        policy.Seen.Where(s => s.Request.ToolName == "count_open_orders").Should().AllSatisfy(s =>
            s.Request.Surface.Should().Be(MentorActionSurface.A2A));
        probes.Distinct().Should().HaveCount(2, "each task has a scope of its own");
    }

    // ── IMentorActionCatalog ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Rule", "surface.coordinator-only")]
    [Trait("Rule", "surface.mcp-withholds-gated")]
    public async Task The_catalog_publishes_on_mcp_and_the_card_exactly_what_they_serve()
    {
        await using var app = await McpAsync(new RecordingActionObserver(), null, a2a: true);
        var catalog = app.Services.GetRequiredService<IMentorActionCatalog>();
        var anonymous = await McpTestSession.OpenAsync(app.Client, asUser: "-");
        var ada = await McpTestSession.OpenAsync(app.Client);

        var actions = catalog.GetActions();
        var coordinator = actions.Where(a => a.AgentKind == MentorAgentKind.Coordinator).ToList();

        coordinator.Where(a => a.McpExposure == MentorMcpExposure.Published).Select(a => a.ToolName)
            .Should().BeEquivalentTo(await anonymous.ListToolNamesAsync());
        coordinator.Where(a => a.McpExposure == MentorMcpExposure.Published
                               || (a.McpExposure == MentorMcpExposure.PublishedToRoles && a.RequiredRoles.Contains("Admin")))
            .Select(a => a.ToolName)
            .Should().BeEquivalentTo(await ada.ListToolNamesAsync());

        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        actions.Where(a => a.OnAgentCard).Select(a => a.ToolName).Should().BeEquivalentTo(card.Skills!.Select(s => s.Id));

        var refund = actions.Single(a => a.ToolName == "issue_refund");
        refund.McpExposure.Should().Be(MentorMcpExposure.Withheld);
        refund.RequiresConfirmation.Should().BeTrue();
        actions.Single(a => a.ToolName == "export_ledger").McpExposure.Should().Be(MentorMcpExposure.PublishedToRoles);
        actions.Single(a => a.ToolName == "count_open_orders").ReadOnly.Should().BeTrue();
        actions.Should().Contain(a => a.AgentKind == MentorAgentKind.Specialist && a.AgentName == "ShippingAgent"
                                      && a.McpExposure == MentorMcpExposure.NotServed && !a.OnAgentCard);
    }

    [Fact]
    public async Task Without_the_mcp_endpoint_and_a2a_the_catalog_says_nothing_is_served()
    {
        await using var app = await ServerAppFactory.CreateAsync(configure: o => o.MemoryAutoCapture = false);

        var actions = app.Services.GetRequiredService<IMentorActionCatalog>().GetActions();

        actions.Should().NotBeEmpty().And.AllSatisfy(a =>
        {
            a.McpExposure.Should().Be(MentorMcpExposure.NotServed);
            a.OnAgentCard.Should().BeFalse();
        });
    }
}
