using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Memory;
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
/// Story 156 over the real transports: the same user, ada, in two tenants of one application. On the hub her memory is
/// her tenant's own and a session snapshot is restored for that tenant only; on the MCP server each tenant has its own
/// allowance. The circuit is in <c>Tenancy/TenantIsolationTests</c>.
/// </summary>
public sealed class TenantIsolationSurfaceTests
{
    private static Task<ServerAppFactory> CreateAsync(Action<MentorOptions>? configure = null)
        => ServerAppFactory.CreateAsync(
            userId: "ada",
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
                o.UseMemoryContext = true;
                configure?.Invoke(o);
            },
            configureServices: s =>
            {
                s.AddMentorTenantResolver<TestTenantResolver>();
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                    (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());

    /// <summary>From now on every request belongs to <paramref name="tenant"/>.</summary>
    private static void From(ServerAppFactory app, string tenant)
        => ((TestTenantResolver)app.Services.GetRequiredService<IMentorTenantResolver>()).Resolve = _ => tenant;

    /// <summary>A hub connection with one turn sent: its scope's tenant is the one requests belonged to then.</summary>
    private static async Task<HubConnection> ConnectedAsync(ServerAppFactory app, string message = "ciao")
    {
        var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", message, null);
        return hub;
    }

    private static string RequestText(FakeChatClient.RecordedCall call)
        => call.Instructions + "\n" + string.Join("\n", call.Messages.Select(m => m.Text));

    [Fact]
    public async Task On_the_hub_a_users_memory_is_her_tenants_own()
    {
        await using var app = await CreateAsync();
        var store = app.Services.GetRequiredService<IMentorMemoryStore>();
        await store.SaveAsync("tenant:contoso/ada", "codice_pratica", "PRATICA-CONTOSO");
        await store.SaveAsync("tenant:fabrikam/ada", "codice_pratica", "PRATICA-FABRIKAM");
        app.Chat.SayRepeatedly("Ok.", 2);

        From(app, "contoso");
        await using (await ConnectedAsync(app, "qual è il mio codice pratica?")) { }
        From(app, "fabrikam");
        await using (await ConnectedAsync(app, "qual è il mio codice pratica?")) { }

        RequestText(app.Chat.Calls[0]).Should().Contain("PRATICA-CONTOSO").And.NotContain("PRATICA-FABRIKAM");
        RequestText(app.Chat.Calls[1]).Should().Contain("PRATICA-FABRIKAM").And.NotContain("PRATICA-CONTOSO");
    }

    [Fact]
    public async Task On_the_hub_a_snapshot_is_restored_for_its_tenant_only()
    {
        await using var app = await CreateAsync();
        app.Chat.SayRepeatedly("Ok.", 3);

        From(app, "contoso");
        await using var contoso = await ConnectedAsync(app);
        var snapshot = await app.Client.GetFromJsonAsync<JsonElement>($"/mentor/session?connectionId={contoso.ConnectionId}");
        From(app, "fabrikam");
        await using var fabrikam = await ConnectedAsync(app);
        From(app, "contoso");
        await using var contosoAgain = await ConnectedAsync(app);

        using var refused = await app.Client.PostAsync(
            $"/mentor/session?connectionId={fabrikam.ConnectionId}", JsonContent.Create(snapshot));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the same user, another tenant");
        (await refused.Content.ReadAsStringAsync()).Should().Contain("another tenant");

        using var restored = await app.Client.PostAsync(
            $"/mentor/session?connectionId={contosoAgain.ConnectionId}", JsonContent.Create(snapshot));
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task On_the_mcp_server_each_tenant_has_its_own_allowance()
    {
        await using var app = await CreateAsync(o => o.RateLimitPerUser = 1);
        var ada = await McpTestSession.OpenAsync(app.Client);

        From(app, "contoso");
        var first = await ada.CallAsync("count_open_orders");
        var second = await ada.CallAsync("count_open_orders");
        From(app, "fabrikam");
        var third = await ada.CallAsync("count_open_orders");

        first.IsError.Should().BeFalse(first.Text);
        second.IsError.Should().BeTrue();
        second.Text.Should().Contain("Rate limit reached");
        third.IsError.Should().BeFalse("the same caller in fabrikam is counted apart: {0}", third.Text);
    }
}
