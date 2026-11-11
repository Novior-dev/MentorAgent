using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Components;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Tenancy;

/// <summary>
/// Story 156 — the dashboard in a circuit shows the figures of its reader's tenant: contoso's action is not in fabrikam's
/// dashboard, a reader whose tenant the resolver does not name sees no figures, and without a resolver the dashboard shows
/// the instance as before.
/// </summary>
public sealed class TenantDashboardTests : ComponentTestBase
{
    private static MentorHostFixture Host(bool resolver = true)
        => MentorHostFixture.Create(o => o.MemoryAutoCapture = false, s =>
        {
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
        });

    private static TestTenantResolver Resolver(MentorHostFixture host) => (TestTenantResolver)host.Get<IMentorTenantResolver>();

    /// <summary>Contoso's turn calls an action, in the host's circuit.</summary>
    private static async Task ContosoTalksAsync(MentorHostFixture host)
    {
        host.Auth.SignIn("ada");
        if (host.Get<IMentorTenantResolver>() is TestTenantResolver resolver) resolver.Resolve = _ => "contoso";
        host.Chat!.CallTool("count_open_orders").Say("There are two.");
        await host.RunTurnAsync("How many open orders?");
    }

    /// <summary>The dashboard of the circuit whose services are <paramref name="circuit"/>.</summary>
    private IRenderedComponent<MentorDashboard> DashboardIn(MentorHostFixture host, IServiceProvider circuit)
    {
        Services.AddSingleton(host.Get<IMentorMetrics>());
        Services.AddSingleton(circuit.GetRequiredService<IMentorTenant>());
        return Render<MentorDashboard>();
    }

    [Fact]
    public async Task Contosos_action_is_in_contosos_dashboard_and_not_in_fabrikams()
    {
        await using var host = Host();
        await ContosoTalksAsync(host);
        Resolver(host).Resolve = _ => "fabrikam";
        await using var fabrikam = host.Get<IServiceScopeFactory>().CreateAsyncScope();

        var cut = DashboardIn(host, fabrikam.ServiceProvider);

        cut.WaitForAssertion(() => cut.FindAll(".bm-dash__grid").Should().NotBeEmpty("fabrikam's figures are shown"));
        cut.Markup.Should().NotContain("count_open_orders", "the action ran in contoso");
    }

    [Fact]
    public async Task The_circuit_that_talked_as_contoso_reads_contosos_figures()
    {
        await using var host = Host();
        await ContosoTalksAsync(host);
        Resolver(host).Resolve = _ => "fabrikam";   // the circuit keeps the tenant of its first turn

        var cut = DashboardIn(host, host.Get<IServiceProvider>());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("count_open_orders"));
    }

    [Fact]
    public async Task A_reader_whose_tenant_is_not_named_sees_no_figures()
    {
        await using var host = Host();
        await ContosoTalksAsync(host);
        Resolver(host).Resolve = _ => null;
        await using var nobody = host.Get<IServiceScopeFactory>().CreateAsyncScope();

        var cut = DashboardIn(host, nobody.ServiceProvider);

        cut.WaitForAssertion(() => cut.FindAll(".bm-dash--empty").Should().HaveCount(1));
        cut.Markup.Should().NotContain("count_open_orders");
    }

    [Fact]
    public async Task An_external_store_that_serves_the_instance_is_not_shown_to_a_tenant()
    {
        await using var host = Host();
        await ContosoTalksAsync(host);
        Services.AddSingleton<IMentorMetricsStore>(new InstanceStore());

        var cut = DashboardIn(host, host.Get<IServiceProvider>());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("count_open_orders", "contoso's figures, from RAM"));
        cut.Markup.Should().NotContain("every_tenants_action");
    }

    /// <summary>A store that queries a backend for the whole instance, and leaves the tenant's question to the default.</summary>
    private sealed class InstanceStore : IMentorMetricsStore
    {
        public Task<Abstractions.Models.MentorMetricsSnapshot?> QueryAsync(CancellationToken ct = default)
            => Task.FromResult<Abstractions.Models.MentorMetricsSnapshot?>(new() { TopActions = [new("every_tenants_action", 9)] });
    }

    [Fact]
    public async Task Without_a_resolver_the_dashboard_shows_the_instance()
    {
        await using var host = Host(resolver: false);
        await ContosoTalksAsync(host);
        await using var other = host.Get<IServiceScopeFactory>().CreateAsyncScope();

        var cut = DashboardIn(host, other.ServiceProvider);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("count_open_orders"));
    }
}
