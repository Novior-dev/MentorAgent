using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// Story 1620 — a decision of the tool gate names the tenant of its call. An observer of several tenants, such as the
/// bridge, could not tell the same user id in two companies apart: <see cref="MentorActionDecision.User"/> is the same
/// in both. With an <see cref="IMentorTenantResolver"/> every decision carries the tenant the turn, or the MCP request,
/// was resolved to; without one, <see cref="MentorActionDecision.TenantId"/> is <see langword="null"/>.
/// </summary>
public sealed class ActionDecisionTenantTests
{
    private static MentorHostFixture Host(RecordingActionObserver observer, bool resolver = true,
        Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
        });

    private static TestTenantResolver Resolver(IServiceProvider services)
        => (TestTenantResolver)services.GetRequiredService<IMentorTenantResolver>();

    private static Task FlushAsync(IServiceProvider services) => services.GetRequiredService<MentorActionDecisions>().FlushAsync();

    private static ChatResponse ToolCall(MentorHostFixture host, string callId, string tool, Dictionary<string, object?>? args = null)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, args)])) { ModelId = host.Chat!.ModelId };

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task Every_decision_of_a_confirmed_call_carries_the_turns_tenant(MentorHitlMode mode)
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer, configure: o => o.HitlMode = mode);
        host.Auth.SignIn("ada", "Manager");
        Resolver(host.Scope.ServiceProvider).Resolve = _ => "contoso";
        host.Chat!.Respond((_, _) => ToolCall(host, "call-7", "cancel_order", new() { ["orderId"] = 7, ["reason"] = "test" }))
            .Say("done");
        var state = host.Get<IMentorStateService>();
        state.OnConfirmationRequired += req => _ = state.ConfirmAsync(req.ActionId);

        await host.RunTurnAsync("cancel order 7");
        await FlushAsync(host.Scope.ServiceProvider);

        observer.Steps("cancel_order").Should().Equal(
            ["Allowed/allowed", "ConfirmationRequested/operator", "Confirmed/operator", "Executed/completed"], "mode {0}", mode);
        observer.Seen.Should().AllSatisfy(d => d.TenantId.Should().Be("contoso", "mode {0}, {1}", mode, d.Outcome));
    }

    [Fact]
    public async Task The_same_user_in_two_tenants_is_told_apart_by_the_tenant()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer);
        host.Auth.SignIn("ada");
        host.Chat!.CallTool("count_open_orders").Say("Three.").CallTool("count_open_orders").Say("Two.");

        Resolver(host.Scope.ServiceProvider).Resolve = _ => "contoso";
        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();

        // Another circuit of the same application, whose requests belong to fabrikam: the same user, another company.
        Resolver(host.Scope.ServiceProvider).Resolve = _ => "fabrikam";
        await using (var other = host.Get<IServiceScopeFactory>().CreateAsyncScope())
            await other.ServiceProvider.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("and in fabrikam?");
        await FlushAsync(host.Scope.ServiceProvider);

        var executed = observer.Seen.Where(d => d.Outcome == MentorActionOutcome.Executed).ToList();
        executed.Should().HaveCount(2);
        executed.Should().AllSatisfy(d => d.User!.Identity!.Name.Should().Be("ada"));
        executed.Select(d => d.TenantId).Should().Equal("contoso", "fabrikam");
        observer.Seen.GroupBy(d => d.SessionId).Should().AllSatisfy(session =>
            session.Select(d => d.TenantId).Distinct().Should().ContainSingle("every decision of a session is its tenant's"));
    }

    [Fact]
    public async Task Without_a_resolver_the_tenant_is_null()
    {
        var observer = new RecordingActionObserver();
        await using var host = Host(observer, resolver: false);
        host.Auth.SignIn("ada");
        host.Chat!.CallTool("count_open_orders").Say("Three.");

        (await host.RunTurnAsync("how many open orders?")).Should().BeNull();
        await FlushAsync(host.Scope.ServiceProvider);

        observer.Steps("count_open_orders").Should().Equal("Allowed/allowed", "Executed/completed");
        observer.Seen.Should().AllSatisfy(d => d.TenantId.Should().BeNull());
    }

    [Fact]
    public async Task On_the_mcp_server_a_decision_carries_the_requests_tenant()
    {
        var observer = new RecordingActionObserver();
        await using var app = await ServerAppFactory.CreateAsync(
            userId: "ada",
            configure: o =>
            {
                o.McpServerEnabled = true;
                o.MemoryAutoCapture = false;
            },
            configureServices: s =>
            {
                s.AddMentorActionObserver(observer);
                s.AddMentorTenantResolver<TestTenantResolver>();
                s.AddHttpContextAccessor();
                s.AddOptions<MentorOptions>().Configure<IHttpContextAccessor>(
                    (o, http) => o.McpCallerPrincipal = () => http.HttpContext?.User);
            },
            mapEndpoints: e => e.MapMentorAgentMcp());
        var ada = await McpTestSession.OpenAsync(app.Client);

        Resolver(app.Services).Resolve = _ => "contoso";
        (await ada.CallAsync("count_open_orders")).IsError.Should().BeFalse();
        Resolver(app.Services).Resolve = _ => "fabrikam";
        (await ada.CallAsync("count_open_orders")).IsError.Should().BeFalse();
        await FlushAsync(app.Services);

        observer.Steps("count_open_orders").Should().Equal(
            "Allowed/allowed", "Executed/completed", "Allowed/allowed", "Executed/completed");
        observer.Seen.Select(d => d.TenantId).Should().Equal("contoso", "contoso", "fabrikam", "fabrikam");
        observer.Seen.Should().AllSatisfy(d => d.Surface.Should().Be(MentorActionSurface.Mcp));
    }
}
