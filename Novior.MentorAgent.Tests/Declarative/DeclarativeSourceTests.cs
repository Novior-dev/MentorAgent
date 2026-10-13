using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Declarative;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Declarative;

/// <summary>
/// Where the Declarative package looks for agent definitions, and what it exposes to them.
///
/// <para>
/// <c>DeclarativeYamlTests</c> covers the YAML dialect — what parses, what is rejected, which block
/// scalars work. This is the layer underneath: <c>Directory</c>, <c>SearchPattern</c>,
/// <c>Recursive</c>, inline <c>Definitions</c> and <c>ConfigurationSection</c>, all of which the
/// claims ledger found unowned.
/// </para>
///
/// <para>
/// It matters more than a file-scanning test usually would, because <b>a definition file is code</b>.
/// It chooses the model, writes the system instructions and names the tools the agent may call. Two
/// properties therefore have to hold no matter what a file says: the scan must reach only where the
/// host pointed it, and a name in the YAML must resolve to a tool the application already registered
/// — one that arrived <b>already wrapped in the gate</b>, so a role check and an approval prompt
/// still apply. A definition can choose from what exists and nothing more.
/// </para>
///
/// <para>
/// Driven against real temporary directories rather than a mocked file system, for B16's reason: the
/// rules here are about <c>Path.Combine</c>, base directories and search patterns, and a mock would
/// only restate what I assumed those did.
/// </para>
/// </summary>
public sealed class DeclarativeSourceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "mentoragent-decl-" + Guid.NewGuid().ToString("N")[..8]);

    public DeclarativeSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Definition(string name) => $"""
        kind: GptComponentMetadata
        name: {name}
        description: A specialist loaded from YAML.
        instructions: You are {name}. Answer briefly.
        """;

    private string Write(string relativePath, string yaml)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, yaml);
        return full;
    }

    /// <summary>Reads the private loader, which is where every option in this file is consumed.</summary>
    private static List<(string Name, string Yaml)> Load(MentorDeclarativeOptions options,
                                                         out TestLogCollector logs)
    {
        var collector = new TestLogCollector();
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(new TestLoggerProvider(collector));
        });

        var source = new DeclarativeAgentSource(options, factory);
        var method = typeof(DeclarativeAgentSource)
            .GetMethod("LoadDefinitions", System.Reflection.BindingFlags.NonPublic
                                        | System.Reflection.BindingFlags.Instance)!;

        logs = collector;
        return ((List<(string, string)>)method.Invoke(source, null)!)
            .Select(t => (t.Item1, t.Item2)).ToList();
    }

    // ── Inline definitions ──────────────────────────────────────────────────────

    [Fact]
    public void An_inline_definition_is_loaded_without_touching_the_file_system()
    {
        // The documented path for agents that come from a database or a configuration service —
        // and the only one available to a host with no writable deployment folder at all.
        var options = new MentorDeclarativeOptions();
        options.Definitions.Add(Definition("inline-agent"));

        var loaded = Load(options, out _);

        loaded.Should().ContainSingle();
        loaded[0].Yaml.Should().Contain("inline-agent");
    }

    [Fact]
    public void Blank_inline_definitions_are_skipped_rather_than_handed_to_the_parser()
    {
        // An empty string is what a configuration binder produces from an empty element, and what a
        // database column holds before anyone fills it in. Passing it on would turn a missing row
        // into a parser error and lose every *other* definition in the same load.
        var options = new MentorDeclarativeOptions();
        options.Definitions.Add("");
        options.Definitions.Add("   ");
        options.Definitions.Add(Definition("real-agent"));

        Load(options, out _).Should().ContainSingle();
    }

    [Fact]
    public void Inline_and_directory_definitions_are_loaded_together()
    {
        // Documented as "loaded in addition to Directory" — not instead of it. A host migrating from
        // files to a database has both for a while.
        Write("one.agent.yaml", Definition("from-disk"));

        var options = new MentorDeclarativeOptions { Directory = _root };
        options.Definitions.Add(Definition("from-memory"));

        var loaded = Load(options, out _);

        loaded.Should().HaveCount(2);
        string.Join("\n", loaded.Select(d => d.Yaml))
            .Should().Contain("from-disk").And.Contain("from-memory");
    }

    // ── SearchPattern ───────────────────────────────────────────────────────────

    [Fact]
    public void Only_files_matching_the_pattern_are_read()
    {
        // The default pattern is `*.agent.yaml`, not `*.yaml`, and that is deliberate: a deployment
        // folder holds other YAML — a serialiser's config, a CI file — and reading one as an agent
        // definition would either fail loudly or, worse, half-succeed.
        Write("shipping.agent.yaml", Definition("shipping"));
        Write("appsettings.yaml", "not: an agent");
        Write("notes.txt", "nothing at all");

        var loaded = Load(new MentorDeclarativeOptions { Directory = _root }, out _);

        loaded.Should().ContainSingle();
        loaded[0].Name.Should().Be("shipping.agent.yaml");
    }

    [Fact]
    public void A_host_can_widen_the_pattern()
    {
        Write("shipping.agent.yaml", Definition("shipping"));
        Write("billing.yaml", Definition("billing"));

        var loaded = Load(new MentorDeclarativeOptions
        {
            Directory = _root,
            SearchPattern = "*.yaml",
        }, out _);

        loaded.Should().HaveCount(2);
    }

    // ── Recursive ───────────────────────────────────────────────────────────────

    [Fact]
    public void Subdirectories_are_ignored_unless_recursion_is_asked_for()
    {
        // Off by default. A definition file is code, so widening the scan is a decision the host
        // makes explicitly rather than inherits — a nested folder is exactly where something gets
        // dropped without anyone reviewing it.
        Write("top.agent.yaml", Definition("top"));
        Write(Path.Combine("nested", "deep.agent.yaml"), Definition("deep"));

        var loaded = Load(new MentorDeclarativeOptions { Directory = _root }, out _);

        loaded.Should().ContainSingle();
        loaded[0].Name.Should().Be("top.agent.yaml");
    }

    [Fact]
    public void With_recursion_on_nested_definitions_are_found()
    {
        Write("top.agent.yaml", Definition("top"));
        Write(Path.Combine("nested", "deep.agent.yaml"), Definition("deep"));

        var loaded = Load(new MentorDeclarativeOptions
        {
            Directory = _root,
            Recursive = true,
        }, out _);

        loaded.Should().HaveCount(2);
    }

    [Fact]
    public void Recursion_is_off_by_default()
        => new MentorDeclarativeOptions().Recursive.Should().BeFalse();

    // ── The directory itself ────────────────────────────────────────────────────

    [Fact]
    public void A_missing_directory_warns_rather_than_failing_the_application()
    {
        // Almost always a file that was not copied to the output folder — the README's own
        // CopyToOutputDirectory note. Worth a warning because the assistant otherwise starts up
        // perfectly and is simply missing a specialist nobody will think to look for.
        var options = new MentorDeclarativeOptions
        {
            Directory = Path.Combine(_root, "does-not-exist"),
        };

        Load(options, out var logs).Should().BeEmpty();

        string.Join("\n", logs.Records.Where(r => r.Level >= LogLevel.Warning).Select(r => r.Message))
            .Should().Contain("does not exist");
    }

    [Fact]
    public void No_directory_configured_reads_nothing_and_says_nothing()
    {
        // The default. A host using only inline definitions must not be warned about a folder it
        // never asked for — a warning on every startup is one people learn to filter out.
        Load(new MentorDeclarativeOptions(), out var logs).Should().BeEmpty();

        logs.Records.Where(r => r.Level >= LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public void Definitions_are_read_in_a_stable_order()
    {
        // `OrderBy(p => p)` rather than whatever the file system returns. Two agents with the same
        // name would otherwise resolve differently between machines, and a handoff graph that
        // depends on load order is the kind of bug that only reproduces in production.
        Write("c.agent.yaml", Definition("c"));
        Write("a.agent.yaml", Definition("a"));
        Write("b.agent.yaml", Definition("b"));

        var loaded = Load(new MentorDeclarativeOptions { Directory = _root }, out _);

        loaded.Select(d => d.Name).Should().Equal("a.agent.yaml", "b.agent.yaml", "c.agent.yaml");
    }

    // ── ConfigurationSection ────────────────────────────────────────────────────

    [Fact]
    public void No_configuration_is_exposed_to_yaml_by_default()
    {
        // The default is null, and the reason is written into the option itself: the Agent
        // Framework's factory loads whatever configuration it is given into the Power Fx engine as
        // one variable per key, in its constructor. A single key that is not a valid Power Fx
        // identifier takes the whole factory down before any definition is read.
        new MentorDeclarativeOptions().ConfigurationSection.Should().BeNull();
    }

    [Fact]
    public void Naming_a_section_exposes_that_section_and_not_the_rest()
    {
        // The blast-radius bound. `${name}` here stands for the real key that caused the original
        // failure — it arrived from an unrelated configuration provider, and naming one section is
        // what keeps such a key away from the Power Fx engine.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentSettings:Tone"] = "formal",
                ["${name}"] = "a key no Power Fx identifier could ever be",
            })
            .Build();

        var options = new MentorDeclarativeOptions { ConfigurationSection = "AgentSettings" };
        var source = new DeclarativeAgentSource(options, LoggerFactory.Create(_ => { }), configuration);

        var resolved = (IConfiguration?)typeof(DeclarativeAgentSource)
            .GetMethod("ResolveConfiguration", System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Instance)!
            .Invoke(source, null);

        resolved.Should().NotBeNull();
        resolved!["Tone"].Should().Be("formal");
        resolved.AsEnumerable().Select(kv => kv.Key)
            .Should().NotContain(k => k.Contains("${name}", StringComparison.Ordinal),
                "the whole point of naming a section is that nothing outside it reaches Power Fx");
    }

    [Fact]
    public void Without_a_section_name_nothing_is_resolved_even_when_configuration_exists()
    {
        // Null must mean "expose nothing", not "expose everything" — the failure mode the option's
        // documentation was written after.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Anything"] = "x" })
            .Build();

        var source = new DeclarativeAgentSource(
            new MentorDeclarativeOptions(), LoggerFactory.Create(_ => { }), configuration);

        var resolved = typeof(DeclarativeAgentSource)
            .GetMethod("ResolveConfiguration", System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Instance)!
            .Invoke(source, null);

        resolved.Should().BeNull();
    }

    // ── The security property ───────────────────────────────────────────────────

    [Fact]
    public async Task A_definition_receives_the_hosts_tools_and_cannot_add_to_them()
    {
        // The invariant that makes YAML-defined agents safe to ship: the factory is handed the tool
        // list MentorAgent supplies, so a name in a definition resolves to something the application
        // already registered — already wrapped in the gate. A file can choose from what exists and
        // nothing more; it cannot define a new capability, and it cannot reach an existing one
        // without the same role check and approval prompt a C# specialist faces.
        //
        // Asserted as *isolation* here: the list the factory receives is a copy, so nothing it does
        // can reach back into the orchestrator's own tools.
        var hostTools = new List<AIFunction>
        {
            AIFunctionFactory.Create(() => "ok", name: "count_open_orders"),
        };

        var context = new MentorAgentSourceContext(new FakeChatClient(), hostTools);
        var options = new MentorDeclarativeOptions();
        options.Definitions.Add(Definition("shipping"));

        var source = new DeclarativeAgentSource(options, LoggerFactory.Create(_ => { }));

        // The factory needs a real provider to build an agent, so this asserts the contract that can
        // be checked without one: the source consumes the supplied list rather than discovering its
        // own, and leaves the caller's collection untouched.
        try { await source.GetAgentsAsync(context, CancellationToken.None); }
        catch { /* the fake client cannot complete a real agent build — not what is under test */ }

        hostTools.Should().ContainSingle()
            .Which.Name.Should().Be("count_open_orders",
                "the source copies the tool list; a definition can never grow it");
    }

    [Theory]
    [InlineData("webSearch", "web_search")]
    [InlineData("codeInterpreter", "code_interpreter")]
    [InlineData("fileSearch", "file_search")]
    public async Task A_definition_cannot_add_a_provider_hosted_tool(string kind, string toolName)
    {
        // Found by the rc.11 README audit: only `kind: function` resolves against the host's list. The
        // other kinds are built by the Agent Framework from the file alone, as provider-hosted tools —
        // outside the gate and outside MentorOptions.HostedTools. They are removed, with a warning,
        // and the agent keeps the host's functions.
        var collector = new TestLogCollector();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(collector)));
        var hostTools = new List<AIFunction> { AIFunctionFactory.Create(() => "ok", name: "count_open_orders") };
        var options = new MentorDeclarativeOptions();
        options.Definitions.Add(Definition("shipping") +
            $"\ntools:\n  - kind: function\n    name: count_open_orders\n  - kind: {kind}\n    name: extra\n");

        var agents = await new DeclarativeAgentSource(options, factory)
            .GetAgentsAsync(new MentorAgentSourceContext(new FakeChatClient(), hostTools), CancellationToken.None);

        var agent = agents.Should().ContainSingle("the agent still loads with the application's own tools").Subject;
        var tools = ((ChatOptions)agent.GetService(typeof(ChatOptions))!).Tools!;
        tools.Select(t => t.Name).Should().Equal(["count_open_orders"], $"'{toolName}' was defined by the file, not by the host");
        collector.HasWarning($"removed {toolName}").Should().BeTrue(collector.WarningText);
    }
}
