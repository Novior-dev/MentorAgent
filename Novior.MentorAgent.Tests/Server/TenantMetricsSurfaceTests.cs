using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Novior.MentorAgent.Tests.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Story 156 over the real transports: <c>GET /mentor/admin/metrics</c> serves the figures of the caller's tenant, and a
/// call to the MCP server counts for its tenant, in the dashboard's figures and in the OpenTelemetry series. The circuit is
/// in <c>Tenancy/TenantMetricsTests</c> and <c>Tenancy/TenantDashboardTests</c>.
/// </summary>
public sealed class TenantMetricsSurfaceTests
{
    private static Task<ServerAppFactory> CreateAsync(bool resolver = true, Action<MentorOptions>? configure = null)
        => ServerAppFactory.CreateAsync(
            userId: "ada",
            roles: ["Admin"],
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
                o.DashboardRole = "Admin";
                configure?.Invoke(o);
            },
            configureServices: s =>
            {
                if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                    (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());

    /// <summary>From now on every request belongs to <paramref name="tenant"/>.</summary>
    private static void From(ServerAppFactory app, string? tenant)
        => ((TestTenantResolver)app.Services.GetRequiredService<IMentorTenantResolver>()).Resolve = _ => tenant;

    /// <summary>One turn on a hub connection of its own.</summary>
    private static async Task TalkAsync(ServerAppFactory app, string message)
    {
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", message, null);
    }

    private static async Task<MentorMetricsSnapshot> MetricsAsync(ServerAppFactory app)
    {
        using var response = await app.Client.GetAsync("/mentor/admin/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MentorMetricsSnapshot>())!;
    }

    [Fact]
    public async Task The_admin_endpoint_serves_the_figures_of_the_callers_tenant_only()
    {
        await using var app = await CreateAsync();
        app.Chat.CallTool("count_open_orders").Say("There are two.").Say("Ok.");
        From(app, "contoso");
        await TalkAsync(app, "How many open orders?");
        From(app, "fabrikam");
        await TalkAsync(app, "ciao");

        var fabrikam = await MetricsAsync(app);
        From(app, "contoso");
        var contoso = await MetricsAsync(app);

        fabrikam.TurnCount.Should().Be(1);
        fabrikam.TopActions.Should().BeEmpty("the action ran in contoso");
        contoso.TurnCount.Should().Be(1);
        contoso.TopActions.Select(a => a.Action).Should().Equal(["count_open_orders"]);
    }

    [Fact]
    public async Task A_caller_whose_tenant_is_not_named_reads_no_figures()
    {
        await using var app = await CreateAsync();
        app.Chat.Say("Ok.");
        From(app, "contoso");
        await TalkAsync(app, "ciao");
        From(app, null);

        using var response = await app.Client.GetAsync("/mentor/admin/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("named no tenant").And.NotContain("TurnCount");
    }

    [Fact]
    public async Task Without_a_resolver_the_admin_endpoint_serves_the_instance()
    {
        await using var app = await CreateAsync(resolver: false);
        app.Chat.SayRepeatedly("Ok.", 2);
        await TalkAsync(app, "uno");
        await TalkAsync(app, "due");

        (await MetricsAsync(app)).TurnCount.Should().Be(2);
    }

    [Fact]
    public async Task A_call_to_the_mcp_server_counts_for_its_tenant()
    {
        using var telemetry = new TenantMetricsTests.Recorded();
        await using var app = await CreateAsync(configure: o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = telemetry.Source;
        });
        var ada = await McpTestSession.OpenAsync(app.Client);

        From(app, "contoso");
        (await ada.CallAsync("count_open_orders")).IsError.Should().BeFalse();

        var metrics = app.Services.GetRequiredService<IMentorMetrics>();
        metrics.GetSnapshot("contoso")!.TopActions.Select(a => a.Action).Should().Equal(["count_open_orders"]);
        metrics.GetSnapshot("fabrikam")!.TopActions.Should().BeEmpty();
        telemetry.Measured.Should().ContainSingle(m => m.Instrument == "mentoragent.tool_calls")
            .Which.Should().Be(("mentoragent.tool_calls", "contoso", "count_open_orders", true));
    }

    // ── Story 157: the tenant on the log lines ───────────────────────────────────

    /// <summary>The tenants named by the log scopes <paramref name="scopes"/>, as <c>mentoragent.tenant</c>.</summary>
    private static List<object?> Tenants(IReadOnlyList<object> scopes)
        => [.. scopes.OfType<IReadOnlyList<KeyValuePair<string, object?>>>()
               .SelectMany(s => s).Where(p => p.Key == "mentoragent.tenant").Select(p => p.Value)];

    [Fact]
    public async Task The_line_of_a_call_to_the_mcp_server_names_its_tenant()
    {
        await using var app = await CreateAsync();
        var ada = await McpTestSession.OpenAsync(app.Client);

        From(app, "contoso");
        (await ada.CallAsync("count_open_orders")).IsError.Should().BeFalse();

        var line = app.Logs.ScopedRecords.Where(l => l.Message.Contains("tools/call 'count_open_orders'"))
            .Should().ContainSingle().Subject;
        Tenants(line.Scopes).Should().Equal(["contoso"]);
    }

    /// <summary>A governor that writes a line whenever it is asked, as an application's own code does.</summary>
    private sealed class LoggingGovernor(ILogger<LoggingGovernor> logger) : IMentorActionGovernor
    {
        public ValueTask<bool?> IsEnabledAsync(MentorActionRequest request, CancellationToken cancellationToken)
        {
            logger.LogInformation("Governor asked about {Tool}.", request.ToolName);
            return ValueTask.FromResult<bool?>(null);
        }
    }

    [Fact]
    public async Task The_lines_written_while_tools_list_is_served_name_its_tenant()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada",
            roles: ["Admin"],
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
            },
            configureServices: s =>
            {
                s.AddMentorTenantResolver<TestTenantResolver>();
                s.AddMentorActionGovernor<LoggingGovernor>();
            },
            mapEndpoints: e => e.MapMentorAgentMcp());
        var ada = await McpTestSession.OpenAsync(app.Client);
        From(app, "contoso");
        var before = app.Logs.ScopedRecords.Count;

        (await ada.ListToolNamesAsync()).Should().NotBeEmpty();

        var asked = app.Logs.ScopedRecords.Skip(before).Where(l => l.Message.StartsWith("Governor asked about")).ToList();
        asked.Should().NotBeEmpty().And.AllSatisfy(l => Tenants(l.Scopes).Should().Equal(["contoso"]));
    }
}
