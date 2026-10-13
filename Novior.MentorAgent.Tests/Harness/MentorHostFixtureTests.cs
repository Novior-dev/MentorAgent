using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// Smoke tests for the DI fixture. If the container does not boot, or discovery does not see the
/// test application, every suite built on top would fail for reasons that have nothing to do with
/// the behaviour under test — so this is checked once, here.
/// </summary>
public sealed class MentorHostFixtureTests
{
    [Fact]
    public async Task Boots_a_real_container_and_resolves_the_orchestrator()
    {
        await using var host = MentorHostFixture.Create();

        host.Orchestrator.Should().NotBeNull();
        host.Options.AppName.Should().Be("TestApp");
    }

    [Fact]
    public async Task Discovers_the_test_application_surface()
    {
        await using var host = MentorHostFixture.Create();
        var discovery = host.Get<MentorDiscoveryService>();

        // Discovery is the input to almost everything else, so it is worth proving the fixture is
        // actually seen rather than assuming an empty scan silently succeeded.
        var result = discovery.Discover();

        result.Level1Methods.Should().NotBeEmpty(
            "TestOrderService carries [Description] and [MentorAction] methods");
        result.Level2Agents.Should().ContainSingle(a => a.Attribute.Name == "ShippingAgent");
        result.Level3Teams.Should().Contain(t => t.Attribute.Name == "Business Analysis Team");
        result.Pages.Select(p => p.Url).Should().Contain(["/orders", "/reports"]);
    }

    [Fact]
    public async Task Applies_the_test_supplied_options_on_top_of_the_defaults()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseMemoryContext = true;
            o.RateLimitPerUser = 7;
        });

        host.Options.UseMemoryContext.Should().BeTrue();
        host.Options.RateLimitPerUser.Should().Be(7);
        host.Options.ScanAssemblies.Should().NotBeEmpty("the fixture supplies its own assembly");
    }

    [Fact]
    public async Task Registers_a_scripted_chat_client_by_default()
    {
        await using var host = MentorHostFixture.Create();

        host.Chat.Should().NotBeNull();
        host.Options.ChatClient.Should().BeSameAs(host.Chat);
        host.Options.Agent.Should().BeNull("Path A and Path B are mutually exclusive");
    }

    [Fact]
    public async Task Supplies_an_embedding_generator_only_when_asked()
    {
        await using var withoutEmbeddings = MentorHostFixture.Create();
        withoutEmbeddings.Options.EmbeddingGenerator.Should().BeNull(
            "the absence of an embedding generator is itself a documented case");

        await using var withEmbeddings = MentorHostFixture.Create(withEmbeddings: true);
        withEmbeddings.Options.EmbeddingGenerator.Should().BeSameAs(withEmbeddings.Embeddings);
    }

    [Fact]
    public async Task Honours_a_service_registered_before_AddMentorAgent()
    {
        // The READMEs promise this position matters: a store registered first must win over the
        // built-in default, which is TryAdd-ed.
        var custom = new CountingMetricsStore();

        await using var host = MentorHostFixture.Create(
            configureServices: s => s.AddSingleton<IMentorMetricsStore>(custom));

        host.Get<IMentorMetricsStore>().Should().BeSameAs(custom,
            "registering before AddMentorAgent() is the documented way to replace a default");
    }

    [Fact]
    public async Task Captures_log_records_so_degradation_warnings_are_assertable()
    {
        // Many documented behaviours are "warn and carry on", so the constraint suite needs to read
        // MentorAgent's warnings. This proves the wiring — a logger resolved from the container
        // reaches the collector.
        //
        // It deliberately does not assert a *specific* MentorAgent warning: those are emitted while
        // the chat pipeline is assembled, which happens on the first turn rather than at
        // resolution. Driving a turn belongs in the constraint suite, not in a fixture smoke test.
        await using var host = MentorHostFixture.Create(o => o.EnableToolFiltering = true);

        host.Get<ILoggerFactory>()
            .CreateLogger("probe")
            .LogWarning("[MentorAgent] probe warning");

        host.Logs.Records.Should().NotBeEmpty();
        host.Logs.HasWarning("probe warning").Should().BeTrue();
        host.Logs.WarningText.Should().Contain("probe warning",
            "the failure message has to name what was actually logged, or a red constraint test " +
            "tells you nothing about why");
    }

    [Fact]
    public async Task Auth_provider_starts_anonymous_and_can_sign_in()
    {
        await using var host = MentorHostFixture.Create();

        var anonymous = await host.Auth.GetAuthenticationStateAsync();
        anonymous.User.Identity?.IsAuthenticated.Should().BeFalse(
            "anonymous is the case where role-gated tools must fail closed");

        host.Auth.SignIn("u-1", "Manager");

        var signedIn = await host.Auth.GetAuthenticationStateAsync();
        signedIn.User.Identity!.IsAuthenticated.Should().BeTrue();
        signedIn.User.IsInRole("Manager").Should().BeTrue();
        signedIn.User.IsInRole("Admin").Should().BeFalse();
    }

    [Fact]
    public async Task Navigation_manager_records_instead_of_navigating()
    {
        await using var host = MentorHostFixture.Create();

        host.Navigation.NavigateTo("/orders");

        host.Navigation.Navigations.Should().Equal(["/orders"]);
        host.Navigation.Uri.Should().EndWith("/orders");
    }

    [Fact]
    public async Task Rejects_an_invalid_configuration_on_first_read()
    {
        // Validate() runs in PostConfigure. A generic host also validates on start (ValidateOnStart —
        // StartupValidationTests); this fixture is a bare container, like a MAUI app, where nothing
        // starts, so the failure surfaces the first time the options are read. Pinning that keeps a
        // "helpful" future change from swallowing it.
        await using var host = MentorHostFixture.Create(o => o.ScanAssemblies = []);

        var act = () => _ = host.Options;

        act.Should().Throw<Exception>().WithMessage("*ScanAssemblies*");
    }

    private sealed class CountingMetricsStore : IMentorMetricsStore;
}
