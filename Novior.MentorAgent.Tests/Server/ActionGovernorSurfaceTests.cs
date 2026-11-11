using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Story 1549 over the real transports. The MCP server lists and runs an action only when it is on — by the code, or
/// by the <see cref="IMentorActionGovernor"/>, asked on every <c>tools/list</c> and every <c>tools/call</c> with the MCP
/// session's fingerprint — and refuses one that is off with the words of an unknown tool. The A2A card follows the
/// code alone; a task asks the governor as the widget does. In the test application <c>archive_order</c> ships off.
/// </summary>
public sealed class ActionGovernorSurfaceTests
{
    private static Task FlushAsync(ServerAppFactory app) => app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

    private static Task<ServerAppFactory> CreateAsync(RecordingActionObserver observer, IMentorActionGovernor? governor,
        bool callerPrincipal = true, Action<MentorOptions>? configure = null, IMentorActionPolicy? policy = null)
        => ServerAppFactory.CreateAsync(
            userId: "ada",
            roles: ["Admin"],
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
                o.A2AServerEnabled = true;
                o.A2AServerUrl = "http://localhost/a2a";
                configure?.Invoke(o);
            },
            configureServices: s =>
            {
                s.AddMentorActionObserver(observer);
                if (governor is not null) s.AddSingleton(governor);
                if (policy is not null) s.AddMentorActionPolicy(policy);
                s.AddHttpContextAccessor();
                if (callerPrincipal)
                    s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                        (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e =>
            {
                e.MapMentorAgentMcp();
                e.MapMentorAgentA2A();
            });

    private static async Task<IReadOnlyList<string>> CardSkillsAsync(ServerAppFactory app)
    {
        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        return [.. card.Skills!.Select(s => s.Id)];
    }

    private static IReadOnlyList<string> AuditLines(ServerAppFactory app, string tool) =>
        [.. app.Logs.Records
            .Where(r => r.Level == LogLevel.Information && r.Message.Contains($"tools/call '{tool}'"))
            .Select(r => r.Message)];

    [Theory]
    [Trait("Rule", "enabled.off-by-default")]
    [Trait("Rule", "surface.mcp-withholds-gated")]
    [Trait("Rule", "surface.card-ungated-only")]
    [InlineData(true)]
    [InlineData(false)]   // no action published per caller: the list every caller gets
    public async Task An_action_shipped_off_is_not_listed_not_run_and_not_on_the_card(bool callerPrincipal)
    {
        var observer = new RecordingActionObserver();
        await using var app = await CreateAsync(observer, null, callerPrincipal);
        var ada = await McpTestSession.OpenAsync(app.Client);

        var listed = await ada.ListToolNamesAsync();
        var (text, isError) = await ada.CallAsync("archive_order", new { orderId = 7 });
        await FlushAsync(app);

        listed.Should().NotContain("archive_order").And.Contain("show_order_card");
        isError.Should().BeTrue();
        text.Should().Contain("Unknown tool: 'archive_order'", "the words of an unknown tool (BUG-101)");
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("archive_order").Should().Equal("Denied/disabled");
        AuditLines(app, "archive_order").Should().ContainSingle().Which.Should().Contain(": disabled in");

        (await CardSkillsAsync(app)).Should().NotContain("archive_order").And.Contain("show_order_card");
        var archive = app.Services.GetRequiredService<IMentorActionCatalog>().GetActions().Single(a => a.ToolName == "archive_order");
        archive.EnabledByDefault.Should().BeFalse();
        archive.McpExposure.Should().Be(MentorMcpExposure.Withheld);
        archive.OnAgentCard.Should().BeFalse();
    }

    [Fact]
    [Trait("Rule", "enabled.external-decides")]
    [Trait("Rule", "surface.governor-session")]
    [Trait("Rule", "surface.card-ungated-only")]
    public async Task On_the_mcp_server_the_governor_decides_the_list_and_the_call_and_the_card_follows_the_code()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Enabled = r => r.ToolName switch { "archive_order" => true, "show_order_card" => false, _ => null },
        };
        await using var app = await CreateAsync(observer, governor);
        var ada = await McpTestSession.OpenAsync(app.Client);

        var listed = await ada.ListToolNamesAsync();
        var (archived, archiveError) = await ada.CallAsync("archive_order", new { orderId = 7 });
        var (shown, showError) = await ada.CallAsync("show_order_card", new { orderId = 7 });
        await FlushAsync(app);

        listed.Should().Contain("archive_order").And.NotContain("show_order_card");
        archiveError.Should().BeFalse(archived);
        archived.Should().Contain("Order 7 archived");
        observer.Steps("archive_order").Should().Equal("Allowed/allowed", "Executed/completed");
        showError.Should().BeTrue();
        shown.Should().Contain("Unknown tool: 'show_order_card'");
        observer.Steps("show_order_card").Should().Equal("Denied/disabled");

        // Asked on the list and on the call, as the MCP server's surface, with the session's fingerprint.
        governor.Asked("archive_order", MentorActionStage.Offer).Should().ContainSingle();
        governor.Asked("archive_order", MentorActionStage.Call).Should().ContainSingle()
            .Which.Arguments!["orderId"]!.ToString().Should().Be("7");
        var mcp = governor.Seen.Where(r => r.Surface == MentorActionSurface.Mcp).ToList();
        mcp.Should().NotBeEmpty().And.AllSatisfy(r =>
        {
            r.SessionId.Should().StartWith("mcp#").And.NotContain(ada.SessionId!, "a fingerprint, never the id");
            r.AgentKind.Should().BeNull();
            r.User!.Identity!.Name.Should().Be("ada");
        });
        mcp.Select(r => r.SessionId).Distinct().Should().ContainSingle();

        // The card is one for every caller: the code's, not the governor's.
        (await CardSkillsAsync(app)).Should().NotContain("archive_order").And.Contain("show_order_card");
    }

    [Fact]
    [Trait("Rule", "enabled.external-decides")]
    public async Task On_the_mcp_server_a_governor_that_fails_stops_the_call()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Before = (r, _) => r.Stage == MentorActionStage.Call ? throw new InvalidOperationException("console down 42") : Task.CompletedTask,
        };
        await using var app = await CreateAsync(observer, governor);
        var ada = await McpTestSession.OpenAsync(app.Client);

        var (text, isError) = await ada.CallAsync("show_order_card", new { orderId = 7 });
        await FlushAsync(app);

        isError.Should().BeTrue();
        text.Should().Contain("the check before this action could not be completed");
        app.Services.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
        observer.Steps("show_order_card").Should().Equal("PolicyFailed/governor-failed");
        observer.Seen.Single().ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        AuditLines(app, "show_order_card").Should().ContainSingle().Which.Should().Contain(": governor-failed in");
        app.Logs.Records.Should().NotContain(r => r.Message.Contains("console down 42"));
    }

    [Fact]
    [Trait("Rule", "enabled.external-decides")]
    [Trait("Rule", "surface.governor-session")]
    public async Task On_an_a2a_task_the_governor_is_asked_as_in_the_widget_with_the_tasks_session()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor { Enabled = r => r.ToolName == "archive_order" ? true : null };
        await using var app = await CreateAsync(observer, governor);
        app.Chat.CallTool("archive_order", new { orderId = 7 }).Say("Archived.");

        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await A2A.A2AClientFactory.Create(card, app.Client).SendMessageAsync("archive order 7", A2A.Role.User, null, deadline.Token))
            .Should().NotBeNull();
        await FlushAsync(app);

        observer.Steps("archive_order").Should().Equal("Allowed/allowed", "Executed/completed");
        var decided = observer.Seen.First(d => d.ToolName == "archive_order");
        decided.Surface.Should().Be(MentorActionSurface.A2A);
        var asked = governor.Seen.Where(r => r.ToolName == "archive_order").ToList();
        asked.Select(r => r.Stage).Should().Equal(MentorActionStage.Offer, MentorActionStage.Call);
        asked.Should().AllSatisfy(r =>
        {
            r.Surface.Should().Be(MentorActionSurface.A2A);
            r.SessionId.Should().NotBeNullOrEmpty().And.Be(decided.SessionId);
        });
    }

    // ── roles.external-replace (story 1546) ─────────────────────────────────────────

    [Fact]
    [Trait("Rule", "roles.external-replace")]
    [Trait("Rule", "surface.mcp-withholds-gated")]
    public async Task A_governor_that_leaves_the_codes_roles_publishes_nothing_more_than_the_code_does()
    {
        // With a governor the server builds the actions withheld for their roles, so that its roles can publish them.
        // Built is not published: with the code's roles, and no McpCallerPrincipal, they stay withheld.
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor();   // null for everything: the code's word
        await using var app = await CreateAsync(observer, governor, callerPrincipal: false);
        var anyone = await McpTestSession.OpenAsync(app.Client, asUser: "-");
        var ada = await McpTestSession.OpenAsync(app.Client);

        var listedToAnyone = await anyone.ListToolNamesAsync();
        var listedToAda = await ada.ListToolNamesAsync();
        var (text, isError) = await ada.CallAsync("export_ledger");
        await FlushAsync(app);

        listedToAnyone.Should().NotContain("export_ledger", "it requires Admin, and nobody can be identified as one")
            .And.Contain("show_order_card");
        listedToAda.Should().NotContain("export_ledger", "without McpCallerPrincipal not even an Admin is one here");
        isError.Should().BeTrue();
        text.Should().Contain("Unknown tool: 'export_ledger'");
        observer.Steps("export_ledger").Should().Equal("Denied/withheld");
        observer.Seen.Single().RolesFromOutside.Should().BeFalse();
        AuditLines(app, "export_ledger").Should().ContainSingle().Which.Should().Contain(": withheld in");
        governor.RolesSeen.Should().Contain(r => r.ToolName == "export_ledger", "the governor was asked, and left the code's roles");
    }

    [Fact]
    [Trait("Rule", "roles.external-replace")]
    public async Task Without_a_caller_principal_the_governors_roles_publish_or_withhold_an_action()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Roles = r => r.ToolName switch { "export_ledger" => [], "count_open_orders" => ["Admin"], _ => null },
        };
        await using var app = await CreateAsync(observer, governor, callerPrincipal: false);
        var anyone = await McpTestSession.OpenAsync(app.Client, asUser: "-");

        var listed = await anyone.ListToolNamesAsync();
        var (exported, exportError) = await anyone.CallAsync("export_ledger");
        var (counted, countError) = await anyone.CallAsync("count_open_orders");
        await FlushAsync(app);

        listed.Should().Contain("export_ledger", "the governor requires no role: nobody needs to be identified")
            .And.NotContain("count_open_orders", "a role without McpCallerPrincipal is withheld, as the code's is")
            .And.Contain("show_order_card");
        exportError.Should().BeFalse(exported);
        observer.Steps("export_ledger").Should().Equal("Allowed/allowed", "Executed/completed");
        countError.Should().BeTrue();
        counted.Should().Contain("Unknown tool: 'count_open_orders'");
        observer.Steps("count_open_orders").Should().Equal("Denied/withheld");
        observer.Seen.Should().AllSatisfy(d => d.RolesFromOutside.Should().BeTrue());
        AuditLines(app, "count_open_orders").Should().ContainSingle().Which.Should().Contain(": withheld in");
    }

    [Fact]
    [Trait("Rule", "roles.external-replace")]
    public async Task With_a_caller_principal_the_governors_roles_decide_which_callers_see_and_call_an_action()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor { Roles = r => r.ToolName == "show_order_card" ? ["Admin"] : null };
        await using var app = await CreateAsync(observer, governor);
        var ada = await McpTestSession.OpenAsync(app.Client);
        var eve = await McpTestSession.OpenAsync(app.Client, asUser: "eve");

        (await ada.ListToolNamesAsync()).Should().Contain("show_order_card").And.Contain("export_ledger");
        (await eve.ListToolNamesAsync()).Should().NotContain("show_order_card").And.NotContain("export_ledger")
            .And.Contain("count_open_orders");
        var (text, isError) = await eve.CallAsync("show_order_card", new { orderId = 7 });
        await FlushAsync(app);

        isError.Should().BeTrue();
        text.Should().Contain("Unknown tool: 'show_order_card'", "the words of a role the caller lacks (BUG-101)");
        var refused = observer.Seen.Should().ContainSingle().Subject;
        refused.Outcome.Should().Be(MentorActionOutcome.Denied);
        refused.ReasonCode.Should().Be(MentorActionReasons.RoleMissing);
        refused.RequiredRoles.Should().Equal("Admin");
        refused.RolesFromOutside.Should().BeTrue();
        governor.RolesSeen.Where(r => r.ToolName == "show_order_card").Should().NotBeEmpty().And.AllSatisfy(r =>
        {
            r.Surface.Should().Be(MentorActionSurface.Mcp);
            r.RequiredRoles.Should().BeEmpty("the governor is asked with the code's roles");
        });
    }

    // ── confirm.external-decides (story 1547) ───────────────────────────────────────

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    [Trait("Rule", "surface.mcp-withholds-gated")]
    public async Task On_the_mcp_server_the_governor_publishes_an_action_whose_confirmation_it_takes_away_and_withholds_one_it_requires()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Confirmation = r => r.ToolName switch
            {
                "issue_refund" or "get_active_orders" => MentorConfirmationChoice.None,       // declared; the predicate's
                "show_order_card"                     => MentorConfirmationChoice.Required,   // the code does not ask
                _                                     => MentorConfirmationChoice.Code,
            },
        };
        await using var app = await CreateAsync(observer, governor, callerPrincipal: false,
            configure: o => o.RequiresApproval = name => name == "get_active_orders");
        var anyone = await McpTestSession.OpenAsync(app.Client, asUser: "-");

        var listed = await anyone.ListToolNamesAsync();
        var (_, refundFailed) = await anyone.CallAsync("issue_refund", new { orderId = 7 });
        var (_, activeFailed) = await anyone.CallAsync("get_active_orders");
        var (cardText, cardFailed) = await anyone.CallAsync("show_order_card", new { orderId = 7 });
        await FlushAsync(app);

        listed.Should().Contain("issue_refund").And.Contain("get_active_orders").And.Contain("count_open_orders")
            .And.NotContain("show_order_card");
        refundFailed.Should().BeFalse();
        activeFailed.Should().BeFalse();
        cardFailed.Should().BeTrue();
        cardText.Should().Contain("Unknown tool: 'show_order_card'");
        observer.Steps("issue_refund").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Steps("get_active_orders").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Steps("show_order_card").Should().Equal("Denied/withheld");
        observer.Seen.Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeTrue());
        AuditLines(app, "show_order_card").Should().ContainSingle().Which.Should().Contain(": withheld in");

        // Asked with the code's confirmation, as the list is made and at the call.
        governor.ConfirmationSeen.Where(r => r.ToolName == "issue_refund").Select(r => r.Stage)
            .Should().Equal(MentorActionStage.Offer, MentorActionStage.Call);
        governor.ConfirmationSeen.Where(r => r.ToolName is "issue_refund" or "get_active_orders")
            .Should().AllSatisfy(r => r.RequiresConfirmation.Should().BeTrue());
        governor.ConfirmationSeen.Where(r => r.ToolName == "show_order_card")
            .Should().AllSatisfy(r => r.RequiresConfirmation.Should().BeFalse());
    }

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    [Trait("Rule", "surface.mcp-withholds-gated")]
    public async Task A_governor_that_leaves_the_codes_confirmation_publishes_nothing_that_asks_for_one()
    {
        // With a governor the server builds the actions withheld for their confirmation, so that it can publish them.
        // Built is not published: with the code's confirmation they stay withheld.
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor();   // Code for everything: the code's word
        await using var app = await CreateAsync(observer, governor, callerPrincipal: false,
            configure: o => o.RequiresApproval = name => name == "get_active_orders");
        var anyone = await McpTestSession.OpenAsync(app.Client, asUser: "-");

        var listed = await anyone.ListToolNamesAsync();
        var (refundText, refundFailed) = await anyone.CallAsync("issue_refund", new { orderId = 7 });
        var (_, activeFailed) = await anyone.CallAsync("get_active_orders");
        await FlushAsync(app);

        listed.Should().NotContain("issue_refund", "it declares a confirmation")
            .And.NotContain("get_active_orders", "the predicate asks for one")
            .And.NotContain("mark_order_reviewed", "it asks for one by default")
            .And.Contain("show_order_card");
        refundFailed.Should().BeTrue();
        refundText.Should().Contain("Unknown tool: 'issue_refund'");
        activeFailed.Should().BeTrue();
        observer.Steps("issue_refund").Should().Equal("Denied/withheld");
        observer.Steps("get_active_orders").Should().Equal("Denied/withheld");
        observer.Seen.Should().AllSatisfy(d => d.ConfirmationFromOutside.Should().BeFalse());
        AuditLines(app, "issue_refund").Should().ContainSingle().Which.Should().Contain(": withheld in");
    }

    [Fact]
    [Trait("Rule", "confirm.external-decides")]
    [Trait("Rule", "confirm.beats-read-only")]
    public async Task On_the_mcp_server_an_action_whose_confirmation_the_governor_takes_away_is_never_read_only()
    {
        // A write the host's predicate calls read-only, built for the governor: published once the governor takes its
        // confirmation away, and still not read-only — the confirmation is the code's mark of an action that changes data.
        var governor = new TestActionGovernor
        {
            Confirmation = r => r.ToolName == "issue_refund" ? MentorConfirmationChoice.None : MentorConfirmationChoice.Code,
        };
        await using var app = await CreateAsync(new RecordingActionObserver(), governor, callerPrincipal: false,
            configure: o => o.IsReadOnlyAction = name => name is "issue_refund" or "count_open_orders");

        var listed = await ListedAsync(app);

        listed.Should().ContainKey("issue_refund");
        ReadOnlyHint(listed["issue_refund"]).Should().NotBe(true, "it asks for a confirmation in the code");
        ReadOnlyHint(listed["count_open_orders"]).Should().BeTrue("a read stays read-only");
    }

    private static bool? ReadOnlyHint(JsonElement tool)
        => tool.TryGetProperty("annotations", out var annotations) && annotations.TryGetProperty("readOnlyHint", out var hint)
            ? hint.GetBoolean()
            : null;

    // ── describe.external-replace (story 1548) ──────────────────────────────────────

    /// <summary>The tools of a <c>tools/list</c>, by name, as the server wrote them.</summary>
    private static async Task<IReadOnlyDictionary<string, JsonElement>> ListedAsync(ServerAppFactory app)
    {
        var anyone = await McpTestSession.OpenAsync(app.Client, asUser: "-");
        var response = await anyone.SendAsync("tools/list", new { });
        return response.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.Clone());
    }

    private static string Without(JsonElement tool, string property)
    {
        var node = JsonNode.Parse(tool.GetRawText())!.AsObject();
        node.Remove(property);
        return node.ToJsonString();
    }

    [Fact]
    [Trait("Rule", "describe.external-replace")]
    public async Task On_the_mcp_server_tools_list_shows_the_governors_description_with_the_codes_name_schema_and_hints()
    {
        var observer = new RecordingActionObserver();
        var governor = new TestActionGovernor
        {
            Description = r => r.ToolName == "count_open_orders" ? "Tells how many orders are still open" : null,
            BeforeDescription = (r, _) => r.ToolName == "show_order_card"
                ? throw new InvalidOperationException("console unreachable 45")
                : Task.CompletedTask,
        };
        var policy = new TestActionPolicy();
        await using var code = await CreateAsync(new RecordingActionObserver(), null, callerPrincipal: false);   // no governor
        await using var app = await CreateAsync(observer, governor, callerPrincipal: false, policy: policy);

        var before = await ListedAsync(code);
        var after = await ListedAsync(app);
        var anyone = await McpTestSession.OpenAsync(app.Client, asUser: "-");
        var (_, failed) = await anyone.CallAsync("count_open_orders");
        await FlushAsync(app);

        // A governor that fails on a description leaves the action out of the list.
        before.Keys.Should().Contain("show_order_card");
        after.Keys.Should().BeEquivalentTo(before.Keys.Where(k => k != "show_order_card"));
        app.Logs.Records.Should().NotContain(r => r.Message.Contains("console unreachable 45"));
        after["count_open_orders"].GetProperty("description").GetString().Should().Be("Tells how many orders are still open");
        Without(after["count_open_orders"], "description").Should().Be(Without(before["count_open_orders"], "description"),
            "the name, the schema and the annotations stay the code's");
        after["count_open_orders"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
        foreach (var name in after.Keys.Where(n => n != "count_open_orders"))
            after[name].GetRawText().Should().Be(before[name].GetRawText(), "{0} keeps the code's description", name);

        // A call is decided with the code's description, and the governor is not asked about it at the call.
        failed.Should().BeFalse();
        observer.Seen.Should().NotBeEmpty().And.AllSatisfy(d => d.DisplayName.Should().Be("Counts open orders"));
        governor.DescriptionSeen.Should().NotBeEmpty().And.OnlyContain(r => r.Stage == MentorActionStage.Offer);
        governor.DescriptionSeen.Where(r => r.ToolName == "count_open_orders")
            .Should().AllSatisfy(r => r.Description.Should().Be("Counts open orders"));

        // The policies are asked with the governor's description as the list is made, with the code's at the call.
        policy.Seen.Where(s => s.Request is { ToolName: "count_open_orders", Stage: MentorActionStage.Offer })
            .Should().NotBeEmpty().And.AllSatisfy(s => s.Request.Description.Should().Be("Tells how many orders are still open"));
        policy.Seen.Where(s => s.Request is { ToolName: "count_open_orders", Stage: MentorActionStage.Call })
            .Should().NotBeEmpty().And.AllSatisfy(s => s.Request.Description.Should().Be("Counts open orders"));
    }
}
