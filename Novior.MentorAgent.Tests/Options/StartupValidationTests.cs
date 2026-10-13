using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using MentorOptions = Novior.MentorAgent.Options.MentorOptions;

namespace Novior.MentorAgent.Tests.Options;

/// <summary>
/// When a configuration error reaches the developer, and what a configure callback that throws costs.
///
/// <para>
/// Up to rc.14 <c>MentorOptions.Validate()</c> ran in <c>PostConfigure</c> with nothing asking for the
/// options at start-up: a host with an invalid configuration started, listened, and failed on the first
/// visitor. The READMEs said "setting both throws at startup", which was not true. The options are now
/// validated on start (<c>ValidateOnStart</c>). Separately, the warm-up was registered only when the
/// registration-time snapshot said <c>WarmUpAtStartup</c>, and that snapshot swallowed whatever the
/// callback threw: a callback that failed before the line enabling it lost the warm-up without a word.
/// </para>
///
/// <para>
/// These tests start a real generic host — what a web app or a worker runs. <c>MentorHostFixture</c>
/// builds a bare container, which validates on first read (see <c>MentorHostFixtureTests</c>).
/// </para>
/// </summary>
public sealed class StartupValidationTests
{
    private static void Valid(MentorOptions o)
    {
        o.AppName = "TestApp";
        o.ChatClient = new FakeChatClient();
        o.ScanAssemblies = [typeof(TestOrderService).Assembly];
    }

    private static IHost BuildHost(Action<MentorOptions> configure, TestLogCollector? logs = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        if (logs is not null)
            builder.Logging.AddProvider(new TestLoggerProvider(logs));
        builder.Services.AddMentorAgent(configure);
        return builder.Build();
    }

    private static bool HasWarmUp(IServiceCollection services) => services.Any(d =>
        d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MentorWarmUpService));

    // ── ValidateOnStart ─────────────────────────────────────────────────────────

    [Fact]
    public async Task An_invalid_configuration_stops_the_host_when_it_starts()
    {
        using var host = BuildHost(o =>
        {
            Valid(o);
            o.ScanAssemblies = [];
        });

        var start = () => host.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ScanAssemblies*");
    }

    [Fact]
    public async Task Setting_both_history_providers_stops_the_host_when_it_starts()
    {
        // The READMEs' "setting both throws at startup", made true.
        using var host = BuildHost(o =>
        {
            Valid(o);
            o.ChatHistoryProvider = new Microsoft.Agents.AI.InMemoryChatHistoryProvider();
            o.ChatHistoryProviderFactory = _ => new Microsoft.Agents.AI.InMemoryChatHistoryProvider();
        });

        var start = () => host.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ChatHistoryProvider*ChatHistoryProviderFactory*");
    }

    [Fact]
    public async Task A_valid_configuration_starts()
    {
        // The control, and the warm-up service resolved by the real container with its optional
        // dependencies (the host lifetime, the registration snapshot).
        var logs = new TestLogCollector();
        using var host = BuildHost(Valid, logs);

        await host.StartAsync();
        await host.StopAsync();

        logs.WarningText.Should().BeEmpty();
    }

    // ── The warm-up and a callback that throws ──────────────────────────────────

    [Fact]
    public void The_warm_up_is_registered_whatever_the_options_say()
    {
        var services = new ServiceCollection();

        services.AddMentorAgent(o =>
        {
            Valid(o);
            o.WarmUpAtStartup = false;
        });

        HasWarmUp(services).Should().BeTrue("the decision is taken on the real options when the host starts");
    }

    [Fact]
    public async Task A_callback_that_throws_before_enabling_the_warm_up_stops_the_host_with_its_error()
    {
        // Before: the snapshot swallowed the exception, WarmUpAtStartup stayed false there, the warm-up
        // was never registered, and the error waited for the first page.
        var fail = true;
        var services = new ServiceCollection();
        void Configure(MentorOptions o)
        {
            Valid(o);
            if (fail) throw new InvalidOperationException("tenant lookup failed");
            o.WarmUpAtStartup = true;
        }

        services.AddMentorAgent(Configure);
        HasWarmUp(services).Should().BeTrue();

        using var host = BuildHost(Configure);
        var start = () => host.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*tenant lookup failed*");
    }

    [Fact]
    public async Task A_callback_that_throws_only_on_the_snapshot_is_reported_when_the_host_starts()
    {
        // The callback runs twice. One that works only once (a one-shot read, a counter) throws on the
        // snapshot and succeeds on the real options: the registration-time options kept their defaults,
        // and that is now said at start instead of nowhere.
        var calls = 0;
        var logs = new TestLogCollector();
        using var host = BuildHost(o =>
        {
            if (calls++ == 0) throw new InvalidOperationException("first read only");
            Valid(o);
        }, logs);

        await host.StartAsync();
        await host.StopAsync();

        logs.HasWarning("configuration callback threw").Should().BeTrue(logs.WarningText);
        logs.HasWarning("McpServerEnabled").Should().BeTrue("the warning names what was decided without the callback");
        logs.HasWarning("AppName as the MCP server's name").Should().BeTrue(logs.WarningText);
        logs.HasWarning("AgentCard").Should().BeFalse(
            "the A2A card is built from the real options when MapMentorAgentA2A() runs, not from the snapshot (BUG-156)");
        logs.Exceptions.Should().Contain(e => e.Message == "first read only");
    }

    // ── The message for a missing model (BUG-151) ───────────────────────────────

    [Fact]
    public void The_missing_model_message_names_the_package_behind_every_type_it_builds()
    {
        // The message is copied into Program.cs as it stands. Up to rc.14 it suggested
        // `new OllamaChatClient(...)`, a type in no package the library brings, and since rc.13 the same
        // held for the Microsoft Foundry client; since rc.15 no provider SDK comes with the library at
        // all. Every type the message builds must be in the library's own graph or have its package named.
        var validate = () => new MentorOptions
        {
            AppName = "TestApp",
            ScanAssemblies = [typeof(TestOrderService).Assembly],
        }.Validate();
        var message = validate.Should().Throw<InvalidOperationException>().Which.Message;

        // The provider packages the README tells a host to add. The types themselves are not named here:
        // the docs ledger counts a README claim as tested when a test names its symbol, and this test
        // checks the message, not the providers.
        string[] packages =
        [
            "Azure.AI.OpenAI", "Microsoft.Extensions.AI.OpenAI",
            "Microsoft.Agents.AI.Foundry", "Microsoft.Agents.AI.Anthropic",
        ];
        // Built from the BCL or System.ClientModel, which MentorAgent references: no package to add.
        var inTheGraph = new[] { typeof(Uri), typeof(System.ClientModel.ApiKeyCredential) }.Select(t => t.Name).ToList();

        string? comment = null;
        var examples = 0;
        foreach (var line in message.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("//", StringComparison.Ordinal)) { comment = line; continue; }
            if (!line.StartsWith("options.", StringComparison.Ordinal)) continue;

            examples++;
            var outside = Regex.Matches(line, @"\bnew\s+(\w+)\s*[({]").Select(m => m.Groups[1].Value)
                               .Except(inTheGraph).ToList();
            if (outside.Count == 0) continue;

            packages.Where(p => comment?.Contains(p, StringComparison.Ordinal) == true).Should().NotBeEmpty(
                $"'{line}' builds {string.Join(", ", outside)}, so the comment above it must name the package");
        }

        examples.Should().BeGreaterThanOrEqualTo(4, "the message shows the IChatClient and the AIAgent providers");
        message.Should().ContainAll(packages);
    }

    [Fact]
    public void No_package_source_names_OllamaChatClient()
    {
        // The package that holds it was never a dependency of the library. Ollama is reached through its
        // OpenAI-compatible endpoint, as the README shows.
        RepoFiles.SourceText.Should().NotContain("OllamaChatClient");
    }
}
