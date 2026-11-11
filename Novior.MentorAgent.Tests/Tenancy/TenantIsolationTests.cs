using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Memory;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Tenancy;

/// <summary>
/// Story 156 — two tenants on one instance share no memory, no rate limit and no conversation, in a Blazor circuit: two
/// scopes of one application, the same user in both, each with its own tenant. The hub and the MCP server are in
/// <c>Server/TenantIsolationSurfaceTests</c>. Without a resolver every key and every snapshot is as before.
/// </summary>
public sealed class TenantIsolationTests
{
    private static MentorHostFixture Host(bool resolver = true, Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.UseMemoryContext = true;
            configure?.Invoke(o);
        }, s =>
        {
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
        }, withRecorder: true);

    private static TestTenantResolver Resolver(MentorHostFixture host) => (TestTenantResolver)host.Get<IMentorTenantResolver>();

    /// <summary>Another scope of the same application — another circuit — whose requests belong to <paramref name="tenant"/>.</summary>
    private static AsyncServiceScope ScopeOf(MentorHostFixture host, string tenant)
    {
        Resolver(host).Resolve = _ => tenant;
        return host.Get<IServiceScopeFactory>().CreateAsyncScope();
    }

    // ── Memory ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_same_user_in_two_tenants_keeps_two_memories()
    {
        await using var host = Host();
        host.Auth.SignIn("ada");
        Resolver(host).Resolve = _ => "contoso";
        var contoso = host.Get<MentorMemoryService>();
        await contoso.RememberAsync("codice_pratica", "PRATICA-8891");

        await using var other = ScopeOf(host, "fabrikam");
        var fabrikam = other.ServiceProvider.GetRequiredService<MentorMemoryService>();

        (await contoso.GetUserIdAsync()).Should().Be("tenant:contoso/ada", "resolved on demand: no turn ran in this scope");
        (await fabrikam.GetUserIdAsync()).Should().Be("tenant:fabrikam/ada");
        (await fabrikam.RecallAsync("codice_pratica")).Should().BeNull();
        (await fabrikam.GetMemoryContextAsync()).Should().NotContain("PRATICA-8891");
        (await contoso.RecallAsync("codice_pratica")).Should().Be("PRATICA-8891");
        (await host.Get<IMentorMemoryStore>().GetAsync("ada", "codice_pratica")).Should().BeNull("never under the bare key");
    }

    [Fact]
    public async Task The_shared_anonymous_bucket_is_one_per_tenant()
    {
        await using var host = Host(configure: o => o.AnonymousIdentity = MentorAnonymousIdentity.Shared);
        Resolver(host).Resolve = _ => "contoso";
        var contoso = host.Get<MentorMemoryService>();
        await contoso.RememberAsync("preferenza", "tema scuro");

        await using var other = ScopeOf(host, "fabrikam");
        var fabrikam = other.ServiceProvider.GetRequiredService<MentorMemoryService>();

        (await contoso.GetUserIdAsync()).Should().Be("tenant:contoso/anonymous");
        (await fabrikam.GetUserIdAsync()).Should().Be("tenant:fabrikam/anonymous");
        (await fabrikam.RecallAsync("preferenza")).Should().BeNull();
    }

    [Fact]
    public void A_tenant_id_is_escaped_so_that_it_cannot_pass_for_another()
    {
        MentorTenantContext.Key("a/b", "c").Should().Be("tenant:a%2Fb/c");
        MentorTenantContext.Key("a", "b/c").Should().NotBe(MentorTenantContext.Key("a/b", "c"));
        MentorTenantContext.Key(null, "sub:ada").Should().Be("sub:ada", "a single tenant keeps the key as it is");
    }

    [Fact]
    public async Task A_scope_whose_tenant_the_resolver_cannot_name_reads_no_memory()
    {
        await using var host = Host();
        host.Auth.SignIn("ada");
        Resolver(host).Resolve = _ => null;

        await host.Get<MentorMemoryService>().Invoking(m => m.RecallAsync("codice_pratica"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*named no tenant*");
    }

    // ── The rate limit ──────────────────────────────────────────────────────────

    [Fact]
    public async Task One_tenant_using_up_its_limit_leaves_the_others_allowance()
    {
        await using var host = Host(configure: o => o.RateLimitPerUser = 1);
        host.Auth.SignIn("ada");
        Resolver(host).Resolve = _ => "contoso";
        host.Chat!.SayRepeatedly("Ok.", 3);

        await host.RunTurnAsync("uno");
        await host.RunTurnAsync("due");
        host.Chat.CallCount.Should().Be(1, "contoso's second message is over the limit");

        await using var other = ScopeOf(host, "fabrikam");
        await other.ServiceProvider.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("tre");

        host.Chat.CallCount.Should().Be(2, "the same user in fabrikam is counted apart");
    }

    // ── Snapshots ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_snapshot_names_its_tenant_and_is_restored_for_that_tenant_only()
    {
        await using var host = Host();
        host.Auth.SignIn("ada");
        Resolver(host).Resolve = _ => "contoso";
        host.Chat!.SayRepeatedly("Ciao.", 3);
        await host.RunTurnAsync("ciao");
        var snapshot = (await host.Orchestrator.SerializeSessionAsync())!.Value;
        snapshot.GetProperty("tenant").GetString().Should().Be("contoso");

        await using (var fabrikam = ScopeOf(host, "fabrikam"))
            await fabrikam.ServiceProvider.GetRequiredService<IMentorOrchestrator>().Invoking(o => o.RestoreSessionAsync(snapshot))
                .Should().ThrowAsync<ArgumentException>().WithMessage("*another tenant*");

        await using var contoso = ScopeOf(host, "contoso");
        await contoso.ServiceProvider.GetRequiredService<IMentorOrchestrator>().RestoreSessionAsync(snapshot);
    }

    [Fact]
    public async Task A_snapshot_with_no_tenant_is_not_restored_where_a_resolver_names_one_nor_one_with_a_tenant_where_none_does()
    {
        JsonElement single, tenanted;
        await using (var host = Host(resolver: false))
        {
            host.Chat!.Say("Ciao.");
            await host.RunTurnAsync("ciao");
            single = (await host.Orchestrator.SerializeSessionAsync())!.Value;
            single.TryGetProperty("tenant", out _).Should().BeFalse("without a resolver a snapshot is as before");
        }
        await using (var host = Host())
        {
            Resolver(host).Resolve = _ => "contoso";
            host.Chat!.Say("Ciao.");
            await host.RunTurnAsync("ciao");
            tenanted = (await host.Orchestrator.SerializeSessionAsync())!.Value;

            await using var other = ScopeOf(host, "contoso");
            await other.ServiceProvider.GetRequiredService<IMentorOrchestrator>().Invoking(o => o.RestoreSessionAsync(single))
                .Should().ThrowAsync<ArgumentException>("nobody can say whose it is");
        }
        await using (var host = Host(resolver: false))
            await host.Orchestrator.Invoking(o => o.RestoreSessionAsync(tenanted)).Should().ThrowAsync<ArgumentException>();
    }

    // ── History ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_history_factory_runs_in_the_conversations_scope_where_the_tenant_is_read()
    {
        string? seen = "unset";
        await using var host = Host(configure: o => o.ChatHistoryProviderFactory = sp =>
        {
            seen = sp.GetRequiredService<IMentorTenant>().Id;
            return new InMemoryChatHistoryProvider();
        });
        host.Auth.SignIn("ada");
        host.Chat!.Say("Ciao.");

        await host.RunTurnAsync("ciao");

        seen.Should().Be("contoso");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task One_history_provider_for_every_tenant_is_reported_at_start(bool resolver)
    {
        await using var host = Host(resolver, o =>
        {
            o.ChatHistoryProvider = new InMemoryChatHistoryProvider();
            o.WarmUpAtStartup = false;
        });
        var service = new MentorWarmUpService(
            host.Get<IServiceScopeFactory>().CreateScope().ServiceProvider,
            host.Get<IOptions<MentorOptions>>(),
            host.Get<ILogger<MentorWarmUpService>>());

        await service.StartAsync(default);

        host.Logs.HasWarning("ChatHistoryProvider is one instance for the whole application").Should().Be(resolver);
    }

    // ── One tenant, as before ───────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_resolver_the_memory_key_is_as_before()
    {
        await using var host = Host(resolver: false);
        host.Auth.SignIn("ada");

        (await host.Get<MentorMemoryService>().GetUserIdAsync()).Should().Be("ada");
        (await host.Get<IMentorTenant>().GetIdAsync()).Should().BeNull();
    }
}
