using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Server.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// Story 105 — the MCP server and the A2A agent moved from <c>MentorAgent</c> to <c>Novior.MentorAgent.Server</c>.
///
/// <para>
/// <c>MentorAgent</c> used to reference <c>ModelContextProtocol.AspNetCore</c> and
/// <c>Microsoft.Agents.AI.Hosting.A2A.AspNetCore</c>, both of which require <c>Microsoft.AspNetCore.App</c>.
/// That framework does not exist for Android, iOS or Mac Catalyst, so a MAUI app that referenced the core
/// compiled for Windows only (NETSDK1082) while the README presented MAUI as supported. The two servers
/// now live with the hub and SSE endpoints; <c>AddMentorAgent</c> leaves only a snapshot of the options
/// behind, and <c>AddMentorAgentMcp()</c> / <c>AddMentorAgentA2A()</c> — which <c>AddMentorAgentServer()</c>
/// calls — register what the flags ask for.
/// </para>
///
/// <para>
/// The hosts this changes are the Blazor Server ones that served MCP or A2A without the hub. They must
/// not be pushed onto <c>AddMentorAgentServer()</c> to get them: it registers an
/// <see cref="AuthenticationStateProvider"/> with <c>TryAdd</c>, which replaces Blazor's own when it runs
/// first (story 104).
/// </para>
/// </summary>
public sealed class ProtocolRegistrationTests
{
    private static void Configure(MentorOptions o, FakeChatClient chat, bool mcp = true, bool a2a = true)
    {
        o.AppName = "BlazorHost";
        o.ScanAssemblies = [typeof(TestOrderService).Assembly];
        o.ChatClient = chat;
        o.McpServerEnabled = mcp;
        o.A2AServerEnabled = a2a;
        o.A2AServerUrl = "http://localhost/a2a";
        o.MemoryAutoCapture = false;
    }

    /// <summary>
    /// A Blazor Server host as the samples write one: Razor components, the core, and the two servers,
    /// with no <c>AddMentorAgentServer()</c> and no hub.
    /// </summary>
    private static async Task<IHost> StartBlazorHostAsync(FakeChatClient chat)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureLogging(b => b.ClearProviders());
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRazorComponents();
                    services.AddScoped<TestOrderService>();
                    services.AddScoped<TestWeatherService>();
                    services.AddScoped<TestShippingAgent>();
                    services.AddScoped<TestExpenseSkill>();
                    services.AddScoped<TestAnalysisTools>();
                    services.AddMentorAgent(o => Configure(o, chat));
                    services.AddMentorAgentMcp();
                    services.AddMentorAgentA2A();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        e.MapMentorAgentMcp();
                        e.MapMentorAgentA2A();
                    });
                });
            });
        return await host.StartAsync();
    }

    [Fact]
    public async Task A_Blazor_Server_host_serves_mcp_and_a2a_without_the_hub()
    {
        using var host = await StartBlazorHostAsync(new FakeChatClient());
        var client = host.GetTestClient();

        var mcp = await McpTestSession.OpenAsync(client);
        (await mcp.ListToolNamesAsync()).Should().Contain("count_open_orders",
            "AddMentorAgentMcp() registers the same server AddMentorAgent used to");

        var card = await client.GetAsync("/.well-known/agent-card.json");
        card.StatusCode.Should().Be(HttpStatusCode.OK);
        (await card.Content.ReadAsStringAsync()).Should().Contain("BlazorHost");

        (await client.PostAsync("/a2a", new StringContent("{}"))).StatusCode.Should().NotBe(HttpStatusCode.NotFound);

        // The hub is not part of this host: nothing of Novior.MentorAgent.Server's own transports was added.
        (await client.GetAsync("/mentor/tour")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void The_two_servers_leave_the_hosts_AuthenticationStateProvider_alone()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRazorComponents();
        var blazors = services.Where(d => d.ServiceType == typeof(AuthenticationStateProvider)).ToList();
        blazors.Should().NotBeEmpty("the test is only about something if Blazor registered its own provider");

        services.AddMentorAgent(o => Configure(o, new FakeChatClient()));
        services.AddMentorAgentMcp();
        services.AddMentorAgentA2A();

        services.Where(d => d.ServiceType == typeof(AuthenticationStateProvider)).Should().Equal(blazors,
            "only AddMentorAgentServer() fills in a provider; the protocol servers must not bring it along");
    }

    [Fact]
    public void Headless_hosts_get_both_servers_from_AddMentorAgentServer_alone()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMentorAgent(o => Configure(o, new FakeChatClient()));
        services.AddMentorAgentServer();

        services.Should().ContainSingle(d => d.ServiceType == typeof(MentorAgentMcpToolCollection));
        services.Should().ContainSingle(d => d.ServiceType == typeof(A2A.IAgentHandler));
    }

    [Fact]
    public void Calling_the_registrations_twice_registers_each_server_once()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMentorAgent(o => Configure(o, new FakeChatClient()));
        services.AddMentorAgentServer();
        services.AddMentorAgentMcp();
        services.AddMentorAgentA2A();
        services.AddMentorAgentMcp();

        services.Should().ContainSingle(d => d.ServiceType == typeof(MentorAgentMcpToolCollection));
        services.Should().ContainSingle(d => d.ServiceType == typeof(A2A.IAgentHandler));
    }

    [Fact]
    public void With_the_flags_off_nothing_is_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMentorAgent(o => Configure(o, new FakeChatClient(), mcp: false, a2a: false));
        services.AddMentorAgentMcp();
        services.AddMentorAgentA2A();

        services.Should().NotContain(d => d.ServiceType == typeof(MentorAgentMcpToolCollection));
        services.Should().NotContain(d => d.ServiceType == typeof(A2A.IAgentHandler));
    }

    [Fact]
    public void A_flag_set_after_AddMentorAgent_does_not_register_the_server()
    {
        // Documented in "When the options are read and checked": McpServerEnabled and A2AServerEnabled
        // are read at registration, from the AddMentorAgent callback. A Configure<MentorOptions> added
        // afterwards changes the real options, not that decision. Pinned so the README stays true.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMentorAgent(o => Configure(o, new FakeChatClient(), mcp: false, a2a: false));
        services.Configure<Novior.MentorAgent.Options.MentorOptions>(o =>
        {
            o.McpServerEnabled = true;
            o.A2AServerEnabled = true;
        });
        services.AddMentorAgentMcp();
        services.AddMentorAgentA2A();

        services.Should().NotContain(d => d.ServiceType == typeof(MentorAgentMcpToolCollection));
        services.Should().NotContain(d => d.ServiceType == typeof(A2A.IAgentHandler));
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("a2a")]
    public void Mapping_without_registering_fails_naming_the_method_to_add(string protocol)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMentorAgent(o => Configure(o, new FakeChatClient()));
        using var app = builder.Build();

        var act = protocol == "mcp"
            ? (Action)(() => app.MapMentorAgentMcp())
            : () => app.MapMentorAgentA2A();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(protocol == "mcp" ? "*AddMentorAgentMcp()*" : "*AddMentorAgentA2A()*",
                "the host moved from AddMentorAgent alone; the error must say which line it now needs")
            .And.Message.Should().Contain("after AddMentorAgent()");
    }

    [Fact]
    public void Registering_before_AddMentorAgent_registers_nothing_and_the_map_says_so()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMentorAgentMcp();   // too early: the options are not known yet
        builder.Services.AddMentorAgent(o => Configure(o, new FakeChatClient()));
        using var app = builder.Build();

        app.Services.GetService<MentorAgentMcpToolCollection>().Should().BeNull(
            "no half-registered server: the flag was not readable when the method ran");
        var act = () => app.MapMentorAgentMcp();
        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*AddMentorAgentMcp() ran before AddMentorAgent()*")
            .And.Message.Should().Contain("after AddMentorAgent()", "the message says where the call goes");
    }

    // ── A wrong order or a late flag stops the host at start-up ─────────────────
    //
    // Until 1.0.0 the two cases below registered nothing and said nothing. A host that maps the server failed
    // at the map call, and for a flag turned on late with a message that named a call the host had made; a host
    // that does not map it ran without the server. The check runs where the options are validated, so the host
    // stops at start-up even without a map call.

    private static WebApplication BuildHost(Action<IServiceCollection> register)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped<TestOrderService>();
        register(builder.Services);
        return builder.Build();
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("a2a")]
    public async Task Registering_before_AddMentorAgent_stops_the_host_at_start_up(string protocol)
    {
        await using var app = BuildHost(services =>
        {
            if (protocol == "mcp") services.AddMentorAgentMcp(); else services.AddMentorAgentA2A();
            services.AddMentorAgent(o => Configure(o, new FakeChatClient()));
        });

        var start = () => app.StartAsync();

        var method = protocol == "mcp" ? "AddMentorAgentMcp" : "AddMentorAgentA2A";
        (await start.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>())
            .WithMessage($"*{method}() ran before AddMentorAgent()*")
            .And.Message.Should().Contain($"Call builder.Services.{method}() after AddMentorAgent()");
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("a2a")]
    public async Task A_flag_turned_on_after_AddMentorAgent_read_it_stops_the_host_at_start_up(string protocol)
    {
        await using var app = BuildHost(services =>
        {
            services.AddMentorAgent(o => Configure(o, new FakeChatClient(), mcp: false, a2a: false));
            services.PostConfigure<MentorOptions>(o =>
            {
                if (protocol == "mcp") o.McpServerEnabled = true; else o.A2AServerEnabled = true;
            });
            services.AddMentorAgentServer();   // calls both registrations, in the right place
        });

        var start = () => app.StartAsync();

        var flag = protocol == "mcp" ? "McpServerEnabled" : "A2AServerEnabled";
        (await start.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>())
            .WithMessage($"*{flag} is true, but it was false when AddMentorAgent() read it*")
            .And.Message.Should().Contain($"set {flag} in the AddMentorAgent() callback");
    }

    [Fact]
    public async Task Registering_before_AddMentorAgent_with_the_flags_off_starts()
    {
        // The host asked for nothing it did not get: no reason to stop it.
        await using var app = BuildHost(services =>
        {
            services.AddMentorAgentMcp();
            services.AddMentorAgentA2A();
            services.AddMentorAgent(o => Configure(o, new FakeChatClient(), mcp: false, a2a: false));
        });

        await app.StartAsync();
        await app.StopAsync();
    }

    [Fact]
    public async Task Registering_again_after_AddMentorAgent_repairs_an_early_call()
    {
        // An early call followed by AddMentorAgentServer() in the right place registers both servers.
        await using var app = BuildHost(services =>
        {
            services.AddMentorAgentMcp();
            services.AddMentorAgent(o => Configure(o, new FakeChatClient()));
            services.AddMentorAgentServer();
        });

        await app.StartAsync();
        app.Services.GetService<MentorAgentMcpToolCollection>().Should().NotBeNull();
        await app.StopAsync();
    }

    [Fact]
    public async Task The_A2A_card_is_built_from_the_real_options_and_only_the_MCP_server_name_from_the_snapshot()
    {
        // Documented in "When the options are read and checked" (BUG-156): of the identity options, only
        // AppName as the MCP server's name is read at registration. The card served at the well-known path
        // is built when MapMentorAgentA2A() runs, so a PostConfigure registered after AddMentorAgent
        // reaches it.
        await using var app = BuildHost(services =>
        {
            services.AddScoped<TestWeatherService>();
            services.AddScoped<TestShippingAgent>();
            services.AddScoped<TestExpenseSkill>();
            services.AddScoped<TestAnalysisTools>();
            services.AddMentorAgent(o =>
            {
                Configure(o, new FakeChatClient());
                o.AppDescription = "Read at registration";
            });
            services.PostConfigure<MentorOptions>(o =>
            {
                o.AppName = "RenamedLater";
                o.AppDescription = "Set after AddMentorAgent";
                o.AgentCard = new AgentCardInfo { Version = "2.0.0", Provider = "Late Provider" };
            });
            services.AddMentorAgentMcp();
            services.AddMentorAgentA2A();
        });
        app.MapMentorAgentMcp();
        app.MapMentorAgentA2A();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/.well-known/agent-card.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var card = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        card.GetProperty("name").GetString().Should().Be("RenamedLater");
        card.GetProperty("description").GetString().Should().Be("Set after AddMentorAgent");
        card.GetProperty("version").GetString().Should().Be("2.0.0");
        card.GetProperty("provider").GetProperty("organization").GetString().Should().Be("Late Provider");

        var mcp = await McpTestSession.OpenAsync(client);
        mcp.Initialize.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()
            .Should().Be("BlazorHost", "the MCP server is registered with the services, named from the snapshot");

        await app.StopAsync();
    }

    [Fact]
    public void The_core_assembly_references_no_ASP_NET_Core_server_assembly()
    {
        // What makes a MAUI target compile: Microsoft.AspNetCore.Components* ship as NuGet packages for
        // every platform; anything else from ASP.NET Core comes only with Microsoft.AspNetCore.App.
        var references = typeof(MentorOptions).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Where(n => n.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal))
            .Should().OnlyContain(n => n.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal));
        references.Should().NotContain(n => n.EndsWith(".AspNetCore", StringComparison.Ordinal));
        references.Should().NotContain(n => n.StartsWith("Microsoft.Agents.AI.Hosting", StringComparison.Ordinal));
    }

    [Fact]
    public void The_core_project_references_no_package_that_requires_ASP_NET_Core()
    {
        // The assembly check above cannot see a package referenced for nothing — which is how the two
        // server packages kept the MAUI targets from compiling while the code that used them was small.
        var csproj = XDocument.Load(Path.Combine(RepoFiles.Root, RepoFiles.Core, RepoFiles.Core + ".csproj"));
        var packages = csproj.Descendants("PackageReference").Select(p => (string)p.Attribute("Include")!).ToList();

        packages.Should().NotContain(p => p.EndsWith(".AspNetCore", StringComparison.Ordinal));
        packages.Should().NotContain(p => p.StartsWith("Microsoft.Agents.AI.Hosting", StringComparison.Ordinal));
        csproj.Descendants("FrameworkReference").Should().BeEmpty();
    }
}
