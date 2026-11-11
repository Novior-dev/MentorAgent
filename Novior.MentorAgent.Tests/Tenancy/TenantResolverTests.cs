using System.Collections.Concurrent;
using System.Security.Claims;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Tenancy;

/// <summary>
/// Story 155 — the tenant of a request, named by the application's <see cref="IMentorTenantResolver"/> and read through
/// <see cref="IMentorTenant"/>, in a Blazor circuit (in-process). The hub, <c>/mentor/chat</c>, A2A and the MCP server are
/// in <c>Server/TenantSurfaceTests</c>. Without a resolver there is one tenant, as before; with one, a request is never
/// served without a tenant, and a conversation stays with the tenant it began with.
/// </summary>
public sealed class TenantResolverTests
{
    private const string Unresolved = "the organization it belongs to could not be identified";
    private const string Changed = "This conversation belongs to another organization";

    /// <summary>What the governor read of the tenant, at each question about an action.</summary>
    private sealed class TenantProbe
    {
        internal ConcurrentQueue<(MentorActionStage Stage, MentorActionSurface Surface, string? Tenant)> Seen { get; } = new();

        internal TestActionGovernor Governor()
            => new()
            {
                Enabled = r =>
                {
                    Seen.Enqueue((r.Stage, r.Surface, r.Services.GetRequiredService<IMentorTenant>().Id));
                    return null;
                },
            };
    }

    private static MentorHostFixture Host(bool resolver = true, IMentorActionGovernor? governor = null)
        => MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
            if (governor is not null) s.AddSingleton(governor);
        }, withRecorder: true);

    private static TestTenantResolver Resolver(MentorHostFixture host) => (TestTenantResolver)host.Get<IMentorTenantResolver>();

    private static IReadOnlyList<string?> Errors(MentorHostFixture host)
        => [.. host.Recorder!.Log.Where(e => e.Event == nameof(RecordingStateService.NotifyError)).Select(e => e.Args[0] as string)];

    // ── One tenant, as before ────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_resolver_there_is_one_tenant_and_nothing_changes()
    {
        await using var host = Host(resolver: false);
        host.Get<IMentorTenantResolver>().Should().BeOfType<MentorSingleTenantResolver>("AddMentorAgent registers it");
        host.Get<IMentorTenant>().Id.Should().BeNull("before any turn too");
        host.Chat!.Say("Ciao.");

        (await host.RunTurnAsync("ciao")).Should().BeNull();

        host.Get<IMentorTenant>().Id.Should().BeNull();
        host.Chat.CallCount.Should().Be(1);
        Errors(host).Should().BeEmpty();
    }

    // ── The circuit ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task In_a_circuit_the_turn_and_its_actions_read_the_tenant_the_resolver_names()
    {
        var probe = new TenantProbe();
        await using var host = Host(governor: probe.Governor());
        host.Auth.SignIn("ada");
        host.Chat!.CallTool("count_open_orders").Say("Ci sono 3 ordini aperti.");

        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull();

        var asked = Resolver(host).Seen.Should().ContainSingle("once for the turn").Subject;
        asked.Surface.Should().Be(MentorActionSurface.InProcess);
        asked.User!.Identity!.Name.Should().Be("ada");
        asked.Services.Should().NotBeNull();
        host.Get<IMentorTenant>().Id.Should().Be("contoso");
        probe.Seen.Should().Contain(s => s.Stage == MentorActionStage.Offer).And.Contain(s => s.Stage == MentorActionStage.Call)
            .And.OnlyContain(s => s.Tenant == "contoso", "the session's tools are built, and the call runs, for the turn's tenant");
        host.Get<TestOrderService>().Invoked.Should().Equal(nameof(TestOrderService.CountOpenOrders));
    }

    [Fact]
    public async Task The_tenant_is_resolved_once_for_every_turn()
    {
        await using var host = Host();
        host.Auth.SignIn("ada");
        host.Chat!.SayRepeatedly("Ok.", 3);

        await host.RunTurnAsync("uno");
        await host.RunTurnAsync("due");
        await host.RunTurnAsync("tre");

        Resolver(host).Seen.Should().HaveCount(3);
        host.Chat.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Before_the_first_turn_the_tenant_cannot_be_read_as_the_single_one()
    {
        await using var host = Host();

        host.Invoking(h => h.Get<IMentorTenant>().Id).Should().Throw<InvalidOperationException>()
            .WithMessage("*No tenant has been resolved*");
    }

    // ── Never without a tenant ───────────────────────────────────────────────────

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("throw")]
    public async Task A_resolver_that_names_no_tenant_refuses_the_turn_before_anything_runs(string answer)
    {
        var probe = new TenantProbe();
        await using var host = Host(governor: probe.Governor());
        host.Auth.SignIn("ada");
        Resolver(host).Resolve = _ => answer switch
        {
            "null" => null,
            "throw" => throw new InvalidOperationException("tenant directory down: secret detail"),
            _ => answer,
        };
        host.Chat!.Say("unexpected");

        (await host.RunTurnAsync("ciao")).Should().BeNull();

        host.Chat.CallCount.Should().Be(0, "the model is never called");
        probe.Seen.Should().BeEmpty("nothing is built or offered for a request without a tenant");
        Errors(host).Should().ContainSingle().Which.Should().Contain(Unresolved);
        host.Logs.WarningText.Should().Contain("named no tenant").And.NotContain("secret detail", "only the exception's type");
        host.Invoking(h => h.Get<IMentorTenant>().Id).Should().Throw<InvalidOperationException>("never the single tenant");
    }

    [Fact]
    public async Task A_turn_whose_tenant_is_not_the_sessions_is_refused_and_the_conversation_stays_with_its_tenant()
    {
        await using var host = Host();
        host.Auth.SignIn("ada");
        host.Chat!.Say("Ciao Ada.").Say("unexpected");

        await host.RunTurnAsync("ciao");
        host.Auth.SignIn("bob");
        (await host.RunTurnAsync("ciao di nuovo")).Should().BeNull();

        host.Chat.CallCount.Should().Be(1, "the second turn is refused before the model");
        Errors(host).Should().ContainSingle().Which.Should().Contain(Changed);
        host.Get<IMentorTenant>().Id.Should().Be("contoso");
        host.Logs.WarningText.Should().Contain("not the one its session began with");
    }

    [Fact]
    public async Task A_reset_before_the_first_turn_builds_nothing_for_a_request_without_a_tenant()
    {
        var probe = new TenantProbe();
        await using var host = Host(governor: probe.Governor());
        Resolver(host).Resolve = _ => throw new InvalidOperationException("down");

        await host.Orchestrator.ResetSessionAsync();

        probe.Seen.Should().BeEmpty("the session's tools are not built");
        Errors(host).Should().ContainSingle();
    }

    [Fact]
    public async Task The_warm_up_serves_nobody_asks_for_no_tenant_and_leaves_nothing_to_the_sessions()
    {
        // As MentorWarmUpService does it: a scope of its own, with no user and no request. A governor that reads the
        // tenant would fail there for every action at every start; it is not asked, and the first real session is built
        // as the governor answers for its tenant.
        var probe = new TenantProbe();
        await using var host = Host(governor: probe.Governor());
        await using (var warm = host.Get<IServiceScopeFactory>().CreateAsyncScope())
            await warm.ServiceProvider.GetRequiredService<MentorOrchestrator>().WarmUpAsync();

        Resolver(host).Seen.Should().BeEmpty("nobody to name a tenant for");
        probe.Seen.Should().BeEmpty("nobody to ask the governor for");

        host.Auth.SignIn("ada");
        host.Chat!.CallTool("count_open_orders").Say("Ci sono 3 ordini aperti.");
        (await host.RunTurnAsync("quanti ordini sono aperti?")).Should().BeNull();

        probe.Seen.Should().Contain(s => s.Stage == MentorActionStage.Offer).And.OnlyContain(s => s.Tenant == "contoso");
        host.Get<TestOrderService>().Invoked.Should().Equal([nameof(TestOrderService.CountOpenOrders)],
            "nothing the warm-up built turned the action off");
        host.Logs.Records.Should().NotContain(r => r.Level >= LogLevel.Error)
            .And.NotContain(r => r.Message.Contains("Action governor"), "no failure is logged at every start");
    }

    // ── Registration ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_resolver_takes_the_place_of_the_single_tenant_and_there_can_be_one()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMentorTenantResolver, MentorSingleTenantResolver>();   // as AddMentorAgent does, first

        services.AddMentorTenantResolver<TestTenantResolver>();

        services.Where(d => d.ServiceType == typeof(IMentorTenantResolver)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be<TestTenantResolver>();
        services.Invoking(s => s.AddMentorTenantResolver<TestTenantResolver>()).Should().Throw<InvalidOperationException>()
            .WithMessage("*there can be one*");
    }
}
