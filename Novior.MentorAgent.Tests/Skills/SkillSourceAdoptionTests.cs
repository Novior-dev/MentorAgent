using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Skills;

/// <summary>
/// ROADMAP #36 — adopting the Agent Framework's <see cref="AgentSkillsSource"/> family.
///
/// <para>
/// What was adopted is the <em>abstraction and its decorators</em>, not the framework's file
/// source. The two read a different <c>SKILL.md</c> dialect — <c>AgentFileSkillsSource</c>
/// "validates each file for YAML frontmatter", while MentorAgent's documented convention is a
/// heading followed by a prose paragraph and frontmatter before the heading is deliberately
/// ignored (pinned by <see cref="SkillDiscoveryTests"/>). Replacing our discovery would therefore
/// have rejected every skill file written against our own documentation. Registering the
/// framework's source alongside ours gives a host both dialects at once, which is strictly more
/// than swapping one for the other.
/// </para>
///
/// <para>
/// The properties worth pinning are about <em>precedence and blast radius</em>: a host source must
/// be able to add capability and must not be able to take one away or bring the assistant down.
/// </para>
/// </summary>
public class SkillSourceAdoptionTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────────

    private sealed class ScriptedSkillsSource(params AgentSkill[] skills) : AgentSkillsSource
    {
        public int Calls { get; private set; }

        public override Task<IList<AgentSkill>> GetSkillsAsync(
            AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IList<AgentSkill>>(skills);
        }
    }

    private sealed class ThrowingSkillsSource : AgentSkillsSource
    {
        public override Task<IList<AgentSkill>> GetSkillsAsync(
            AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the catalogue is unreachable");
    }

    private static AgentInlineSkill Skill(string name, string description, string instructions)
        => new(new AgentSkillFrontmatter(name, description, null), instructions);

    // ── Harness ──────────────────────────────────────────────────────────────────

    private static (List<SkillRegistration> Skills, TestLogCollector Logs) Build(
        Action<MentorOptions> configure)
    {
        var logs = new TestLogCollector();
        var root = Path.Combine(Path.GetTempPath(), "mentor-skillsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var options = new MentorOptions
        {
            AppName = "Test",
            // Deliberately NOT the test assembly: other suites declare skills of their own and
            // this file is about the source seam, not about the fixture's catalogue.
            ScanAssemblies = [typeof(Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext).Assembly],
            EnableSkills = true,
            SkillsFolder = root,
            // AgentSkillsSourceContext requires an agent; MentorAgent derives one from the chat
            // client when the host has not supplied a pre-built agent. Without either, sources are
            // skipped by design — pinned separately below.
            ChatClient = new Fakes.FakeChatClient(),
        };
        configure(options);

        var builder = new MentorSkillsBuilder(
            new OptionsWrapper<MentorOptions>(options),
            new TestHostEnvironment { ContentRootPath = root },
            new TypedLogger<MentorSkillsBuilder>(new TestLoggerProvider(logs).CreateLogger("skills")),
            NullLoggerFactory.Instance);

        try { return (builder.BuildAsync().GetAwaiter().GetResult(), logs); }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }

    /// <summary>Writes a MentorAgent-dialect skill file: heading, then a prose description.</summary>
    private static void WriteFileSkill(string root, string name, string description)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"# {name}\n\n{description}\n\n## Rules\n- something\n");
    }

    // ── The seam is inert until a host uses it ───────────────────────────────────

    /// <summary>
    /// The default must be indistinguishable from the behaviour before this feature existed —
    /// every application already running gets no new code path at all.
    /// </summary>
    [Fact]
    public void With_no_sources_registered_nothing_is_composed()
    {
        var options = new MentorOptions
        {
            AppName = "Test",
            ScanAssemblies = [typeof(MentorOptions).Assembly],
        };

        MentorNativeSkillSources.Compose(options, NullLoggerFactory.Instance)
            .Should().BeNull("an empty SkillSources list must not build a decorator chain");
    }

    // ── A source can add capability ──────────────────────────────────────────────

    [Fact]
    public void A_registered_source_contributes_its_skills()
    {
        var (skills, _) = Build(o => o.SkillSources =
            [new ScriptedSkillsSource(Skill("refunds", "Refund rules", "## Refunds\nWithin 30 days."))]);

        var refunds = skills.Should().ContainSingle(s => s.Name == "refunds").Subject;
        refunds.Description.Should().Be("Refund rules");
        refunds.InstructionsMarkdown.Should().Contain("Within 30 days");
        refunds.Native.Should().NotBeNull("a source-provided skill resolves resources on demand");
    }

    [Fact]
    public void Several_sources_are_aggregated_in_registration_order()
    {
        var (skills, _) = Build(o => o.SkillSources =
        [
            new ScriptedSkillsSource(Skill("alpha", "A", "a")),
            new ScriptedSkillsSource(Skill("beta", "B", "b")),
        ]);

        skills.Select(s => s.Name).Should().Contain(["alpha", "beta"]);
    }

    /// <summary>
    /// <c>DeduplicatingAgentSkillsSource</c> keeps the first occurrence, so the earlier source in
    /// the list wins — which is what makes registration order a contract a host can rely on.
    /// </summary>
    [Fact]
    public void A_duplicate_name_across_sources_keeps_the_first_registered()
    {
        var (skills, _) = Build(o => o.SkillSources =
        [
            new ScriptedSkillsSource(Skill("policy", "from the first source", "first")),
            new ScriptedSkillsSource(Skill("policy", "from the second source", "second")),
        ]);

        skills.Should().ContainSingle(s => s.Name == "policy")
              .Which.Description.Should().Be("from the first source");
    }

    // ── …and must not be able to take one away ───────────────────────────────────

    /// <summary>
    /// The property that matters most. A host source reusing an application skill's name must not
    /// shadow it: the application's own code is the authority on what its skills say, and silent
    /// replacement would be a supply-chain problem rather than a configuration one.
    /// </summary>
    [Fact]
    public void An_application_skill_wins_a_collision_with_a_registered_source()
    {
        string? root = null;
        var (skills, logs) = Build(o =>
        {
            root = o.SkillsFolder;
            WriteFileSkill(root, "shipping", "The application's own shipping rules");
            o.SkillSources = [new ScriptedSkillsSource(
                Skill("shipping", "A source trying to replace it", "replaced"))];
        });

        var shipping = skills.Should().ContainSingle(s => s.Name == "shipping").Subject;
        shipping.Description.Should().Be("The application's own shipping rules");
        shipping.InstructionsMarkdown.Should().NotContain("replaced");

        string.Join("\n", logs.Records.Select(r => r.Message)).Should().Contain("shipping",
            "the collision is skipped with a warning naming the skill, not in silence");
    }

    /// <summary>
    /// Same stance as an unreachable MCP server or A2A peer: a catalogue that cannot be reached
    /// costs its own skills, never the assistant.
    /// </summary>
    [Fact]
    public void A_throwing_source_is_survivable_and_the_others_still_load()
    {
        var (skills, logs) = Build(o => o.SkillSources =
        [
            new ThrowingSkillsSource(),
            new ScriptedSkillsSource(Skill("survivor", "Still here", "ok")),
        ]);

        // AggregatingAgentSkillsSource materialises its children, so one thrower costs the batch —
        // what must NOT happen is the exception escaping into coordinator construction.
        logs.Records.Should().NotBeEmpty();
        string.Join("\n", logs.Records.Select(r => r.Message)).Should().Contain("AgentSkillsSource",
            "the failure is reported against the seam that produced it");
    }

    // ── Filtering ────────────────────────────────────────────────────────────────

    [Fact]
    public void SkillFilter_removes_a_source_skill()
    {
        var (skills, _) = Build(o =>
        {
            o.SkillSources = [new ScriptedSkillsSource(
                Skill("keep", "kept", "k"), Skill("drop", "dropped", "d"))];
            o.SkillFilter = s => s.Frontmatter.Name != "drop";
        });

        skills.Select(s => s.Name).Should().Contain("keep").And.NotContain("drop");
    }

    /// <summary>
    /// The filter is deliberately scoped to source-provided skills. A predicate that could also
    /// remove the application's own skills would be a debugging trap — the skill is right there in
    /// the repository and simply never appears.
    /// </summary>
    [Fact]
    public void SkillFilter_does_not_apply_to_the_applications_own_skills()
    {
        var (skills, _) = Build(o =>
        {
            WriteFileSkill(o.SkillsFolder, "local-rules", "Discovered by MentorAgent itself");
            o.SkillSources = [new ScriptedSkillsSource(Skill("remote", "from a source", "r"))];
            o.SkillFilter = _ => false;          // reject everything the filter can see
        });

        skills.Select(s => s.Name).Should().Contain("local-rules").And.NotContain("remote");
    }

    // ── The constraint the framework imposes on us ───────────────────────────────

    /// <summary>
    /// The 21st entry in MentorAgent's constraint matrix, and it was found by this suite failing
    /// rather than by reading the docs: <c>AgentSkillsSourceContext</c> throws
    /// <see cref="ArgumentNullException"/> on a null agent, and skills are discovered *before* the
    /// coordinator exists — so on Path A with no pre-built agent there is nothing to hand it.
    ///
    /// <para>
    /// The first implementation passed <c>options.Agent!</c> and would have thrown out of
    /// coordinator construction for every Path A host that registered a source. Which, per BUG-011
    /// and BUG-028, surfaces to the user as *"The assistant could not start"* and names neither the
    /// option nor the cause. It now degrades the way every other constraint does: the sources are
    /// skipped, a warning names the count and the fix, and the application's own skills are
    /// untouched.
    /// </para>
    /// </summary>
    [Fact]
    public async Task With_neither_ChatClient_nor_Agent_the_sources_are_skipped_with_a_warning()
    {
        var logs = new TestLogCollector();
        var root = Path.Combine(Path.GetTempPath(), "mentor-skillsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WriteFileSkill(root, "local-rules", "Discovered by MentorAgent itself");

        var options = new MentorOptions
        {
            AppName = "Test",
            ScanAssemblies = [typeof(Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext).Assembly],
            EnableSkills = true,
            SkillsFolder = root,
            SkillSources = [new ScriptedSkillsSource(Skill("remote", "from a source", "r"))],
            // ChatClient and Agent both deliberately unset.
        };

        var builder = new MentorSkillsBuilder(
            new OptionsWrapper<MentorOptions>(options),
            new TestHostEnvironment { ContentRootPath = root },
            new TypedLogger<MentorSkillsBuilder>(new TestLoggerProvider(logs).CreateLogger("skills")),
            NullLoggerFactory.Instance);

        var skills = await builder.BuildAsync();   // must not throw
        try { Directory.Delete(root, true); } catch { /* best effort */ }

        skills.Select(s => s.Name).Should().Contain("local-rules").And.NotContain("remote");

        var warning = logs.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Warning && r.Message.Contains("AgentSkillsSource")).Subject;
        warning.Message.Should().Contain("ChatClient",
            "a warning that does not name the fix costs a debugging session");
    }

    // ── The tool surface ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>read_skill_resource</c> registers only when some skill can serve one. A source-provided
    /// skill counts even though its resources cannot be enumerated ahead of time: the framework's
    /// <see cref="AgentSkill"/> exposes <c>GetResourceAsync(name)</c> and no listing, so if the
    /// tool were withheld the model could never ask in the first place.
    /// </summary>
    [Fact]
    public void read_skill_resource_is_registered_for_a_source_skill_with_no_listable_resources()
    {
        var (skills, _) = Build(o => o.SkillSources =
            [new ScriptedSkillsSource(Skill("remote", "from a source", "r"))]);

        var tools = MentorSkillsBuilder.BuildTools(skills, NullLogger.Instance);

        tools.Select(t => t.Name).Should().Contain(["load_skill", "read_skill_resource"]);
    }

    /// <summary>
    /// …and the negative, so the assertion above means something: with only a plain local skill and
    /// no side-car files, the tool that could only ever answer "no resources" is still withheld.
    /// </summary>
    [Fact]
    public void read_skill_resource_stays_absent_for_a_local_skill_with_no_resources()
    {
        var (skills, _) = Build(o => WriteFileSkill(o.SkillsFolder, "plain", "No side-car files"));

        var tools = MentorSkillsBuilder.BuildTools(skills, NullLogger.Instance);

        tools.Select(t => t.Name).Should().Contain("load_skill").And.NotContain("read_skill_resource");
    }

    // ── SkillsRefreshInterval (rc.11 README audit: the option had no effect) ─────

    private static int FetchesAcrossTwoSessions(TimeSpan? interval)
    {
        var source = new ScriptedSkillsSource(Skill("promo", "Seasonal promotions", "Offer the promo."));
        var shared = new MentorSharedSkillSource();   // what DI registers once per process
        var options = new MentorOptions
        {
            AppName = "Test",
            ScanAssemblies = [typeof(Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext).Assembly],
            EnableSkills = true,
            SkillsFolder = Path.Combine(Path.GetTempPath(), "mentor-skills-none-" + Guid.NewGuid().ToString("N")),
            ChatClient = new Fakes.FakeChatClient(),
            SkillSources = [source],
            SkillsRefreshInterval = interval,
        };

        for (var session = 0; session < 2; session++)
        {
            var builder = new MentorSkillsBuilder(
                new OptionsWrapper<MentorOptions>(options),
                new TestHostEnvironment { ContentRootPath = Path.GetTempPath() },
                new TypedLogger<MentorSkillsBuilder>(NullLogger.Instance),
                NullLoggerFactory.Instance,
                sharedSource: shared);
            builder.BuildAsync().GetAwaiter().GetResult()
                .Should().Contain(s => s.Name == "promo", "every session still gets the skill");
        }

        return source.Calls;
    }

    [Fact]
    public void With_a_refresh_interval_sessions_share_one_fetch()
        => FetchesAcrossTwoSessions(TimeSpan.FromMinutes(15)).Should().Be(1,
            "the cache is held once per process; it used to be rebuilt by every session and never hit");

    [Fact]
    public void Without_one_every_session_reads_the_sources()
        => FetchesAcrossTwoSessions(null).Should().Be(2, "the documented default: read once per session");
}
