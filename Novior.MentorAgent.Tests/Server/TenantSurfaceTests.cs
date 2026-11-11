using System.Collections.Concurrent;
using System.Text;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// Story 155 over the real transports: the hub, <c>/mentor/chat</c>, A2A and the MCP server. On each, the resolver is asked
/// with the surface and the caller, an action reads the tenant it named — on the MCP server, the call's own, though the
/// tool classes are shared by every call — and a resolver that names no tenant refuses the request before the model or the
/// action, never serving it as the single tenant. The circuit is in <c>Tenancy/TenantResolverTests</c>.
/// </summary>
public sealed class TenantSurfaceTests
{
    private const string Unresolved = "the organization it belongs to could not be identified";

    /// <summary>What the governor read at each question: the surface, the caller and the tenant.</summary>
    private sealed class TenantProbe
    {
        internal ConcurrentQueue<(MentorActionStage Stage, MentorActionSurface Surface, string? User, string? Tenant)> Seen { get; } = new();

        internal TestActionGovernor Governor { get; }

        internal TenantProbe(Func<MentorActionRequest, CancellationToken, Task>? before = null)
            => Governor = new TestActionGovernor
            {
                Before = before,
                Enabled = r =>
                {
                    Seen.Enqueue((r.Stage, r.Surface, r.User?.Identity?.Name, r.Services.GetRequiredService<IMentorTenant>().Id));
                    return null;
                },
            };
    }

    private static Task<ServerAppFactory> CreateAsync(TenantProbe probe)
        => ServerAppFactory.CreateAsync(
            userId: "ada",
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
                o.A2AServerEnabled = true;
                o.A2AServerUrl = "http://localhost/a2a";
            },
            configureServices: s =>
            {
                s.AddMentorTenantResolver<TestTenantResolver>();
                s.AddSingleton<IMentorActionGovernor>(probe.Governor);
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                    (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e =>
            {
                e.MapMentorAgentMcp();
                e.MapMentorAgentA2A();
            });

    private static TestTenantResolver Resolver(ServerAppFactory app)
        => (TestTenantResolver)app.Services.GetRequiredService<IMentorTenantResolver>();

    private static void Refusing(ServerAppFactory app)
        => Resolver(app).Resolve = _ => throw new InvalidOperationException("tenant directory down");

    /// <summary>One turn on the hub; the text of the Error event, when one came.</summary>
    private static async Task<string?> RunHubTurnAsync(ServerAppFactory app, string message)
    {
        var hub = app.CreateHubConnection();
        string? error = null;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On("StreamingCompleted", () => completed.TrySetResult());
        hub.On<string>("Error", e =>
        {
            error = e;
            completed.TrySetResult();
        });
        await hub.StartAsync();
        try
        {
            await hub.InvokeAsync("SendMessage", message, null);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { await hub.DisposeAsync(); }
        return error;
    }

    /// <summary>One turn on <c>/mentor/chat</c>: the frames it streamed, up to the last.</summary>
    private static async Task<string> RunSseTurnAsync(ServerAppFactory app, string message)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await app.Client.GetAsync(
            "/mentor/chat?message=" + Uri.EscapeDataString(message), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var received = new StringBuilder();
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, cts.Token) is var read and > 0)
        {
            received.Append(Encoding.UTF8.GetString(buffer, 0, read));
            var text = received.ToString();
            if (text.Contains("\"type\":\"completed\"") || text.Contains("\"type\":\"error\"")) break;
        }
        return received.ToString();
    }

    /// <summary>One task over A2A: the state it ended in.</summary>
    private static async Task<A2A.TaskState?> RunA2ATaskAsync(ServerAppFactory app, string message)
    {
        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await A2A.A2AClientFactory.Create(card, app.Client).SendMessageAsync(message, A2A.Role.User, null, deadline.Token);
        return response.Task?.Status?.State;
    }

    private static void TheTurnReadTheTenant(ServerAppFactory app, TenantProbe probe, MentorActionSurface surface)
    {
        var asked = Resolver(app).Seen.Should().ContainSingle("once for the turn").Subject;
        asked.Surface.Should().Be(surface);
        asked.User!.Identity!.Name.Should().Be("ada");
        probe.Seen.Should().Contain(s => s.Stage == MentorActionStage.Call)
            .And.OnlyContain(s => s.Tenant == "contoso" && s.Surface == surface, "the call reached the gate with it");
    }

    private static void NothingRan(ServerAppFactory app, TenantProbe probe)
    {
        app.Chat.CallCount.Should().Be(0, "the model is never called");
        probe.Seen.Should().BeEmpty("nothing is built, offered or called for a request without a tenant");
    }

    // ── The hub ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_the_hub_the_turn_and_its_actions_read_the_tenant_of_the_connections_user()
    {
        var probe = new TenantProbe();
        await using var app = await CreateAsync(probe);
        app.Chat.CallTool("count_open_orders").Say("3 open orders.");

        (await RunHubTurnAsync(app, "how many open orders?")).Should().BeNull();

        TheTurnReadTheTenant(app, probe, MentorActionSurface.Hub);
    }

    [Fact]
    public async Task On_the_hub_a_resolver_that_names_no_tenant_refuses_the_turn()
    {
        var probe = new TenantProbe();
        await using var app = await CreateAsync(probe);
        Refusing(app);
        app.Chat.Say("unexpected");

        (await RunHubTurnAsync(app, "how many open orders?")).Should().Contain(Unresolved);

        NothingRan(app, probe);
    }

    // ── /mentor/chat ────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_mentor_chat_the_turn_and_its_actions_read_the_tenant_of_the_requests_user()
    {
        var probe = new TenantProbe();
        await using var app = await CreateAsync(probe);
        app.Chat.CallTool("count_open_orders").Say("3 open orders.");

        (await RunSseTurnAsync(app, "how many open orders?")).Should().Contain("\"type\":\"completed\"");

        TheTurnReadTheTenant(app, probe, MentorActionSurface.Sse);
    }

    [Fact]
    public async Task On_mentor_chat_a_resolver_that_names_no_tenant_refuses_the_turn()
    {
        var probe = new TenantProbe();
        await using var app = await CreateAsync(probe);
        Refusing(app);
        app.Chat.Say("unexpected");

        (await RunSseTurnAsync(app, "how many open orders?")).Should().Contain("\"type\":\"error\"").And.Contain(Unresolved);

        NothingRan(app, probe);
    }

    // ── A2A ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_an_a2a_task_the_turn_and_its_actions_read_the_tenant_of_the_caller()
    {
        var probe = new TenantProbe();
        await using var app = await CreateAsync(probe);
        app.Chat.CallTool("count_open_orders").Say("3 open orders.");

        (await RunA2ATaskAsync(app, "how many open orders?")).Should().Be(A2A.TaskState.Completed);

        TheTurnReadTheTenant(app, probe, MentorActionSurface.A2A);
    }

    [Fact]
    public async Task On_an_a2a_task_a_resolver_that_names_no_tenant_fails_the_task()
    {
        var probe = new TenantProbe();
        await using var app = await CreateAsync(probe);
        Refusing(app);
        app.Chat.Say("unexpected");

        (await RunA2ATaskAsync(app, "how many open orders?")).Should().Be(A2A.TaskState.Failed);

        NothingRan(app, probe);
    }

    // ── The MCP server ──────────────────────────────────────────────────────────

    [Fact]
    public async Task On_the_mcp_server_each_call_reads_its_own_callers_tenant()
    {
        // The tool classes are resolved once and shared by every call: two callers at once must still read their own.
        var probe = new TenantProbe(before: (_, ct) => Task.Delay(100, ct));
        await using var app = await CreateAsync(probe);
        var ada = await McpTestSession.OpenAsync(app.Client);
        var bob = await McpTestSession.OpenAsync(app.Client, asUser: "bob");

        var listed = await ada.ListToolNamesAsync();
        var calls = await Task.WhenAll(
            ada.CallAsync("count_open_orders"), bob.CallAsync("count_open_orders"),
            ada.CallAsync("count_open_orders"), bob.CallAsync("count_open_orders"));

        listed.Should().Contain("count_open_orders");
        calls.Should().OnlyContain(c => !c.IsError);
        Resolver(app).Seen.Should().HaveCount(5, "once for the list and once for every call")
            .And.OnlyContain(r => r.Surface == MentorActionSurface.Mcp);
        var mcp = probe.Seen.Where(s => s.Surface == MentorActionSurface.Mcp).ToList();
        mcp.Count(s => s.Stage == MentorActionStage.Call).Should().Be(4);
        mcp.Should().OnlyContain(s => (s.User == "ada" && s.Tenant == "contoso") || (s.User == "bob" && s.Tenant == "fabrikam"));
        mcp.Where(s => s.Stage == MentorActionStage.Call).Select(s => s.Tenant).Should().Contain("contoso").And.Contain("fabrikam");
    }

    [Fact]
    public async Task On_the_mcp_server_a_resolver_that_names_no_tenant_lists_nothing_and_refuses_the_call()
    {
        var probe = new TenantProbe();
        await using var app = await CreateAsync(probe);
        Refusing(app);
        var ada = await McpTestSession.OpenAsync(app.Client);

        var listed = await ada.ListToolNamesAsync();
        var (text, isError) = await ada.CallAsync("count_open_orders");

        listed.Should().BeEmpty();
        isError.Should().BeTrue();
        text.Should().Contain(Unresolved);
        NothingRan(app, probe);
        app.Logs.WarningText.Should().Contain("named no tenant").And.NotContain("tenant directory down");
    }
}
