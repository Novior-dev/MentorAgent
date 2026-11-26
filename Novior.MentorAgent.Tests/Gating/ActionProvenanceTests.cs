using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Core.Mcp;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 1877 — who provides the tool a request or a decision is about: <see cref="MentorActionRequest.Provenance"/> and
/// <see cref="MentorActionDecision.Provenance"/>, with the MCP server's name. A policy can be stricter with what the
/// application does not provide itself; <see cref="MentorActionProvenance.Unknown"/>, the default, is never what
/// MentorAgent says on a path it knows.
/// </summary>
[Trait("Rule", "policy.provenance")]
public sealed class ActionProvenanceTests
{
    private static MentorMcpServer Server(string name)
        => new() { Name = name, ServerUrl = $"http://127.0.0.1:59999/{name}" };

    /// <summary>
    /// One turn that calls an action of the application, a tool of an MCP server, a UI action of the page and one of
    /// MentorAgent's own tools, with a policy and an observer.
    /// </summary>
    private static async Task<(MentorHostFixture Host, TestActionPolicy Policy, RecordingActionObserver Observer)> TurnAsync()
    {
        var policy = new TestActionPolicy();
        var observer = new RecordingActionObserver();
        var factory = new FakeMcpSessionFactory();
        factory.Servers["warehouse"] = ([AIFunctionFactory.Create((string sku) => "12 in stock", "check_stock", "Checks the stock of a product")], []);
        var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.McpServers = [Server("warehouse")];
        }, s =>
        {
            s.AddSingleton<IMcpSessionFactory>(factory);
            s.AddMentorActionPolicy(policy);
            s.AddMentorActionObserver(observer);
        });
        host.Get<IMentorPageContext>().RegisterUIAction("highlight_row", "Highlights a row", _ => { });
        host.Chat!
            .CallTool("count_open_orders")
            .CallTool("check_stock", new { sku = "A-1" })
            .CallTool("highlight_row", new { parameter = "5" })
            .CallTool("navigate_to", new { url = "/orders" })
            .Say("Done.");

        (await host.RunTurnAsync("count the orders, check the stock, highlight row 5 and open the orders")).Should().BeNull();
        await host.Get<MentorActionDecisions>().FlushAsync();
        return (host, policy, observer);
    }

    private static MentorActionRequest Call(TestActionPolicy policy, string tool)
        => policy.Seen.Select(s => s.Request).First(r => r.ToolName == tool && r.Stage == MentorActionStage.Call);

    [Fact]
    public async Task A_tool_of_an_MCP_server_is_the_servers_with_its_name_in_the_request_and_in_the_decisions()
    {
        var (host, policy, observer) = await TurnAsync();
        await using var _ = host;

        var request = Call(policy, "check_stock");
        request.Provenance.Should().Be(MentorActionProvenance.McpServer);
        request.ProvenanceName.Should().Be("warehouse");

        var decisions = observer.Seen.Where(d => d.ToolName == "check_stock").ToList();
        decisions.Should().NotBeEmpty();
        decisions.Should().OnlyContain(d => d.Provenance == MentorActionProvenance.McpServer && d.ProvenanceName == "warehouse");
    }

    [Fact]
    public async Task An_action_and_a_UI_action_of_the_application_are_the_applications_and_name_no_provider()
    {
        var (host, policy, observer) = await TurnAsync();
        await using var _ = host;

        foreach (var tool in new[] { "count_open_orders", "highlight_row" })
        {
            var request = Call(policy, tool);
            request.Provenance.Should().Be(MentorActionProvenance.Application, tool);
            request.ProvenanceName.Should().BeNull(tool);
            observer.Seen.Where(d => d.ToolName == tool).Should().NotBeEmpty(tool)
                .And.OnlyContain(d => d.Provenance == MentorActionProvenance.Application && d.ProvenanceName == null, tool);
        }
        policy.Seen.Select(s => s.Request).Where(r => r.Stage == MentorActionStage.Offer && r.ToolName == "highlight_row")
            .Should().NotBeEmpty("the page's action is offered through the policies")
            .And.OnlyContain(r => r.Provenance == MentorActionProvenance.Application);
    }

    [Fact]
    public async Task One_of_MentorAgents_own_tools_is_the_librarys_in_its_decisions()
    {
        var (host, policy, observer) = await TurnAsync();
        await using var _ = host;

        policy.Asked("navigate_to").Should().BeEmpty("the policies are not asked about MentorAgent's own tools");
        observer.Seen.Where(d => d.ToolName == "navigate_to").Should().NotBeEmpty()
            .And.OnlyContain(d => d.Provenance == MentorActionProvenance.Library && d.ProvenanceName == null);
    }

    [Fact]
    public async Task No_request_and_no_decision_of_a_turn_is_unknown()
    {
        var (host, policy, observer) = await TurnAsync();
        await using var _ = host;

        policy.Seen.Should().NotBeEmpty();
        policy.Seen.Select(s => s.Request).Where(r => r.Provenance == MentorActionProvenance.Unknown)
            .Select(r => $"{r.Stage} {r.ToolName}").Should().BeEmpty();
        observer.Seen.Should().NotBeEmpty();
        observer.Seen.Where(d => d.Provenance == MentorActionProvenance.Unknown)
            .Select(d => $"{d.Outcome} {d.ToolName}").Should().BeEmpty();
    }

    [Fact]
    public async Task On_MentorAgents_MCP_server_a_call_is_to_an_action_of_the_application()
    {
        var policy = new TestActionPolicy();
        var observer = new RecordingActionObserver();
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada",
            roles: ["Admin"],
            roleClaimType: "roles",
            configure: o => o.McpServerEnabled = true,
            configureServices: s =>
            {
                s.AddMentorActionPolicy(policy);
                s.AddMentorActionObserver(observer);
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                    (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());
        var ada = await McpTestSession.OpenAsync(app.Client);

        var (_, isError) = await ada.CallAsync("count_open_orders");
        await app.Services.GetRequiredService<MentorActionDecisions>().FlushAsync();

        isError.Should().BeFalse();
        policy.Seen.Select(s => s.Request).Where(r => r.ToolName == "count_open_orders").Should().NotBeEmpty()
            .And.OnlyContain(r => r.Provenance == MentorActionProvenance.Application && r.ProvenanceName == null);
        observer.Seen.Where(d => d.ToolName == "count_open_orders").Should().NotBeEmpty()
            .And.OnlyContain(d => d.Provenance == MentorActionProvenance.Application);
        policy.Seen.Select(s => s.Request).Should().NotContain(r => r.Provenance == MentorActionProvenance.Unknown);
        observer.Seen.Should().NotContain(d => d.Provenance == MentorActionProvenance.Unknown);
    }
}
