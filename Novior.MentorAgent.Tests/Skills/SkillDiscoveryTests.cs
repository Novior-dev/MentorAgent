using FluentAssertions;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Skills;

/// <summary>
/// Agent Skills — progressive disclosure of domain knowledge the coordinator loads on demand.
///
/// <para>
/// A skill is prose the model is given when it asks for it, so the failure modes are all quiet
/// ones: a folder that is not found, a description that resolves to the folder name, a class-based
/// skill that does or does not win over a file of the same name. None of them throw and none of
/// them are visible in an answer — the assistant simply knows less than the developer thinks it
/// does.
/// </para>
///
/// <para>
/// File-based skills are exercised against real directories in the temp folder rather than a mocked
/// file system: the resolution rules are about <c>Path.Combine</c>, content roots and what counts as
/// a side-car file, and a mock would only re-state whatever I assumed those did.
/// </para>
/// </summary>
public sealed class SkillDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mentoragent-skills-" + Guid.NewGuid().ToString("N"));

    public SkillDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Writes <c>{root}/{skill}/SKILL.md</c> plus any side-car files.</summary>
    private string WriteSkill(string name, string skillMd, params (string File, string Content)[] resources)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), skillMd);
        foreach (var (file, content) in resources)
            File.WriteAllText(Path.Combine(dir, file), content);
        return dir;
    }

    private (List<SkillRegistration> Skills, TestLogCollector Logs) Build(
        Action<MentorOptions>? configure = null, string? contentRoot = null)
    {
        var logs = new TestLogCollector();
        var options = new MentorOptions
        {
            AppName = "TestApp",
            // Novior.MentorAgent.Abstractions declares nothing, so class-based discovery finds nothing
            // unless a test opts in — the shared test app has skills of its own and this suite is
            // about the rules, not about how many the fixture happens to declare.
            ScanAssemblies = [typeof(Novior.MentorAgent.Abstractions.Interfaces.IMentorPageContext).Assembly],
            EnableSkills = true,
            SkillsFolder = _root,
        };
        configure?.Invoke(options);

        var builder = new MentorSkillsBuilder(
            new OptionsWrapper<MentorOptions>(options),
            new TestHostEnvironment { ContentRootPath = contentRoot ?? _root },
            new TypedLogger<MentorSkillsBuilder>(new TestLoggerProvider(logs).CreateLogger("skills")),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        return (builder.BuildAsync().GetAwaiter().GetResult(), logs);
    }

    private static SkillRegistration Named(IEnumerable<SkillRegistration> skills, string name)
        => skills.Single(s => s.Name == name);

    // ── The switch ──────────────────────────────────────────────────────────────

    [Fact]
    public void Skills_are_off_until_they_are_switched_on()
    {
        // Off by default, and the option gates *discovery* — not just the tools. A host that never
        // enabled skills must not pay for a directory walk on startup.
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.");

        var (skills, _) = Build(o => o.EnableSkills = false);

        skills.Should().BeEmpty();
    }

    // ── File-based discovery ────────────────────────────────────────────────────

    [Fact]
    public void A_folder_with_a_skill_file_becomes_a_skill_named_after_the_folder()
    {
        WriteSkill("expense-report", "# Expenses\n\nHow to file an expense report.");

        var (skills, _) = Build();

        Named(skills, "expense-report").Name.Should().Be("expense-report");
    }

    [Fact]
    public void The_whole_skill_file_is_what_the_model_gets()
    {
        // Progressive disclosure means the file is the payload — trimming it to a summary would
        // defeat the point of keeping the detail out of the system prompt.
        const string content = "# Expenses\n\nMeals are capped at €50.\n\n## Rules\n\n- Receipt required";
        WriteSkill("expense-report", content);

        var (skills, _) = Build();

        Named(skills, "expense-report").InstructionsMarkdown.Should().Be(content);
    }

    [Fact]
    public void The_description_is_the_first_prose_line_after_the_heading()
    {
        // The description is all the coordinator sees before deciding whether to load the skill, so
        // it has to be the sentence the author wrote — not the heading, which is usually a title.
        WriteSkill("expense-report", "# Expense Report\n\nHow to file an expense and what the limits are.");

        var (skills, _) = Build();

        Named(skills, "expense-report").Description
            .Should().Be("How to file an expense and what the limits are.");
    }

    [Fact]
    public void Blank_lines_between_the_heading_and_the_prose_are_skipped()
    {
        WriteSkill("expense-report", "# Expense Report\n\n\n\n   The real description.");

        var (skills, _) = Build();

        Named(skills, "expense-report").Description.Should().Be("The real description.");
    }

    [Fact]
    public void A_file_with_no_prose_falls_back_to_the_skill_name()
    {
        // Better than an empty description, which tells the coordinator nothing about when to load
        // it — the folder name at least carries the author's own word for the subject.
        WriteSkill("shipping-policy", "# Shipping Policy\n## Rules\n### Details");

        var (skills, _) = Build();

        Named(skills, "shipping-policy").Description.Should().Be("shipping-policy");
    }

    [Fact]
    public void Prose_before_any_heading_is_not_mistaken_for_the_description()
    {
        // Front matter, a licence line, an editor's note. The rule is "after a heading", and
        // relaxing it would put whatever happens to be at the top of the file in the catalogue.
        WriteSkill("expense-report", "Draft, do not publish.\n\n# Expenses\n\nThe real description.");

        var (skills, _) = Build();

        Named(skills, "expense-report").Description.Should().Be("The real description.");
    }

    [Fact]
    public void A_folder_without_a_skill_file_is_not_a_skill()
    {
        Directory.CreateDirectory(Path.Combine(_root, "not-a-skill"));
        File.WriteAllText(Path.Combine(_root, "not-a-skill", "notes.txt"), "just a folder");

        var (skills, _) = Build();

        skills.Should().BeEmpty();
    }

    [Fact]
    public void Several_skill_folders_are_all_found_and_ordered_by_name()
    {
        // Stable order so the catalogue block in the prompt is byte-identical between restarts —
        // the same reason the cacheable prefix exists at all.
        WriteSkill("zebra", "# Z\n\nLast alphabetically.");
        WriteSkill("alpha", "# A\n\nFirst alphabetically.");
        WriteSkill("middle", "# M\n\nIn between.");

        var (skills, _) = Build();

        skills.Select(s => s.Name).Should().Equal("alpha", "middle", "zebra");
    }

    [Fact]
    public void A_skills_folder_that_does_not_exist_is_not_an_error()
    {
        // The default `SkillsFolder` is "Skills", which most applications will not have. Warning
        // about it on every startup would train developers to ignore the log.
        var (skills, logs) = Build(o => o.SkillsFolder = Path.Combine(_root, "nowhere"));

        skills.Should().BeEmpty();
        logs.WarningText.Should().BeEmpty();
    }

    [Fact]
    public void A_blank_skills_folder_turns_file_discovery_off()
    {
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.");

        var (skills, _) = Build(o => o.SkillsFolder = "   ");

        skills.Should().BeEmpty();
    }

    [Fact]
    public void A_relative_skills_folder_resolves_against_the_content_root()
    {
        // The documented shape: `options.SkillsFolder = "Skills"` next to the application, not an
        // absolute path nobody can check into source control.
        var appRoot = Path.Combine(_root, "app");
        Directory.CreateDirectory(Path.Combine(appRoot, "Skills", "expense-report"));
        File.WriteAllText(
            Path.Combine(appRoot, "Skills", "expense-report", "SKILL.md"),
            "# Expenses\n\nHow to file one.");

        var (skills, _) = Build(o => o.SkillsFolder = "Skills", contentRoot: appRoot);

        Named(skills, "expense-report").Description.Should().Be("How to file one.");
    }

    // ── Side-car resources ──────────────────────────────────────────────────────

    [Fact]
    public void Every_other_file_in_the_folder_becomes_a_resource()
    {
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.",
            ("policy.md", "The full policy."),
            ("examples.txt", "Worked examples."));

        var (skills, _) = Build();

        var skill = Named(skills, "expense-report");
        skill.Resources.Should().NotBeNull();
        skill.Resources!.Should().HaveCount(2);
        skill.Resources["policy.md"].Should().Be("The full policy.");
    }

    [Fact]
    public void The_skill_file_itself_is_not_offered_as_a_resource()
    {
        // It is already the instructions. Offering it twice invites the model to spend a second
        // tool call fetching what it was just given.
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.", ("policy.md", "The policy."));

        var (skills, _) = Build();

        Named(skills, "expense-report").Resources!.Keys.Should().NotContain("SKILL.md");
    }

    [Fact]
    public void A_skill_with_no_side_car_files_has_no_resources_at_all()
    {
        // Null rather than an empty dictionary: it is what decides whether read_skill_resource is
        // registered, and an empty dictionary would register a tool that can only ever fail.
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.");

        var (skills, _) = Build();

        Named(skills, "expense-report").Resources.Should().BeNull();
    }

    [Fact]
    public void A_resource_can_be_asked_for_in_any_casing()
    {
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.", ("Policy.MD", "The policy."));

        var (skills, _) = Build();

        Named(skills, "expense-report").Resources!["policy.md"].Should().Be("The policy.");
    }

    // ── Class-based skills ──────────────────────────────────────────────────────

    [Fact]
    public void A_class_skill_uses_its_inline_instructions()
    {
        var (skills, _) = Build(o => o.ScanAssemblies = [typeof(SkillDiscoveryTests).Assembly]);

        Named(skills, "inline-skill").InstructionsMarkdown.Should().Be("The inline body.");
    }

    [Fact]
    public void A_class_skill_can_point_at_a_file()
    {
        File.WriteAllText(Path.Combine(_root, "external.md"), "The external body.");

        var (skills, _) = Build(o => o.ScanAssemblies = [typeof(SkillDiscoveryTests).Assembly]);

        Named(skills, "file-skill").InstructionsMarkdown.Should().Be("The external body.");
    }

    [Fact]
    public void A_missing_instructions_file_is_reported_and_the_skill_still_exists()
    {
        // Degrading to the description keeps the catalogue honest — the coordinator still knows the
        // skill is there, it just has nothing detailed to load. Failing outright would take the
        // whole skills feature down over one path typo.
        var (skills, logs) = Build(o => o.ScanAssemblies = [typeof(SkillDiscoveryTests).Assembly]);

        var skill = Named(skills, "broken-file-skill");
        skill.InstructionsMarkdown.Should().Contain("broken-file-skill").And.Contain("Points at nothing.");
        logs.HasWarning("Instructions file not found").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public void A_class_skill_with_no_instructions_falls_back_to_its_description()
    {
        var (skills, _) = Build(o => o.ScanAssemblies = [typeof(SkillDiscoveryTests).Assembly]);

        Named(skills, "bare-skill").InstructionsMarkdown
            .Should().Be("# bare-skill\n\nNothing but a description.");
    }

    [Fact]
    public void A_class_skill_picks_up_a_matching_folder_without_being_told()
    {
        // The convention that makes the two mechanisms one feature: declare the skill in code for
        // discoverability, write the prose in a file that a non-developer can edit.
        WriteSkill("bare-skill", "# Bare\n\nWritten in a file after all.");

        var (skills, _) = Build(o => o.ScanAssemblies = [typeof(SkillDiscoveryTests).Assembly]);

        Named(skills, "bare-skill").InstructionsMarkdown.Should().Contain("Written in a file after all.");
    }

    [Fact]
    public void An_abstract_class_carrying_the_attribute_is_ignored()
    {
        // Same guard as the discovery service. A base class decorated once would otherwise register
        // a skill for every application that inherits from it.
        var (skills, _) = Build(o => o.ScanAssemblies = [typeof(SkillDiscoveryTests).Assembly]);

        skills.Select(s => s.Name).Should().NotContain("abstract-skill");
    }

    // ── Precedence ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_class_skill_wins_over_a_file_of_the_same_name()
    {
        // Declared order in the source: files first, then classes. The attribute is the more
        // specific statement — somebody wrote it deliberately — so it overrides a folder that
        // happens to share the name.
        WriteSkill("inline-skill", "# From the file\n\nThe file body.");

        var (skills, _) = Build(o => o.ScanAssemblies = [typeof(SkillDiscoveryTests).Assembly]);

        Named(skills, "inline-skill").InstructionsMarkdown.Should().Be("The inline body.");
    }

    [Fact]
    public void A_name_that_differs_only_in_case_is_the_same_skill()
    {
        // Otherwise a folder named `Inline-Skill` and a class named `inline-skill` both register,
        // and load_skill resolves to whichever the model happened to spell.
        WriteSkill("Inline-Skill", "# From the file\n\nThe file body.");

        var (skills, _) = Build(o => o.ScanAssemblies = [typeof(SkillDiscoveryTests).Assembly]);

        skills.Count(s => string.Equals(s.Name, "inline-skill", StringComparison.OrdinalIgnoreCase))
              .Should().Be(1);
    }

    // ── The tools ───────────────────────────────────────────────────────────────

    [Fact]
    public void With_no_skills_there_are_no_skill_tools()
    {
        MentorSkillsBuilder.BuildTools([], NullLogger()).Should().BeEmpty();
    }

    [Fact]
    public void One_skill_is_enough_for_load_skill()
    {
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.");
        var (skills, _) = Build();

        ToolNames(skills).Should().Equal("load_skill");
    }

    [Fact]
    public void Read_skill_resource_appears_only_when_something_can_be_read()
    {
        // A tool whose every invocation could only answer "no resources" is worse than an absent
        // one: it costs schema tokens on every call and invites a wasted round trip.
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.", ("policy.md", "The policy."));
        var (skills, _) = Build();

        ToolNames(skills).Should().Contain("read_skill_resource");
    }

    [Fact]
    public async Task Loading_a_skill_returns_its_instructions()
    {
        WriteSkill("expense-report", "# Expenses\n\nMeals are capped at €50.");
        var (skills, _) = Build();

        var result = await Invoke(skills, "load_skill", ("skillName", "expense-report"));

        result.Should().Contain("Meals are capped at €50.");
    }

    [Fact]
    public async Task Loading_a_skill_by_the_wrong_name_lists_the_ones_that_exist()
    {
        // The model guessed. Telling it what is available turns a dead end into a retry it can get
        // right, without another round trip to find out.
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.");
        WriteSkill("shipping-policy", "# Shipping\n\nHow long it takes.");
        var (skills, _) = Build();

        var result = await Invoke(skills, "load_skill", ("skillName", "expenses"));

        result.Should().Contain("expense-report").And.Contain("shipping-policy");
    }

    [Fact]
    public async Task Skill_names_are_matched_case_insensitively_at_call_time()
    {
        WriteSkill("expense-report", "# Expenses\n\nMeals are capped at €50.");
        var (skills, _) = Build();

        var result = await Invoke(skills, "load_skill", ("skillName", "Expense-Report"));

        result.Should().Contain("Meals are capped at €50.");
    }

    [Fact]
    public async Task Reading_a_resource_returns_its_content()
    {
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.", ("policy.md", "The full policy text."));
        var (skills, _) = Build();

        var result = await Invoke(skills, "read_skill_resource",
            ("skillName", "expense-report"), ("resourceName", "policy.md"));

        result.Should().Be("The full policy text.");
    }

    [Fact]
    public async Task Asking_for_a_resource_that_is_not_there_lists_the_ones_that_are()
    {
        WriteSkill("expense-report", "# Expenses\n\nHow to file one.",
            ("policy.md", "The policy."), ("examples.txt", "Examples."));
        var (skills, _) = Build();

        var result = await Invoke(skills, "read_skill_resource",
            ("skillName", "expense-report"), ("resourceName", "faq.md"));

        result.Should().Contain("policy.md").And.Contain("examples.txt");
    }

    [Fact]
    public async Task Asking_a_resourceless_skill_for_a_resource_says_there_are_none()
    {
        // Reachable whenever one skill has side-car files and another does not — the tool is
        // registered for the whole catalogue, not per skill.
        WriteSkill("with-files", "# With\n\nHas resources.", ("policy.md", "The policy."));
        WriteSkill("without-files", "# Without\n\nHas none.");
        var (skills, _) = Build();

        var result = await Invoke(skills, "read_skill_resource",
            ("skillName", "without-files"), ("resourceName", "policy.md"));

        result.ToLowerInvariant().Should().Contain("none");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static Microsoft.Extensions.Logging.ILogger NullLogger()
        => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static IReadOnlyList<string> ToolNames(IReadOnlyList<SkillRegistration> skills)
        => MentorSkillsBuilder.BuildTools(skills, NullLogger()).OfType<AIFunction>().Select(t => t.Name).ToList();

    private static async Task<string> Invoke(
        IReadOnlyList<SkillRegistration> skills, string toolName, params (string Key, object? Value)[] args)
    {
        var tool = MentorSkillsBuilder.BuildTools(skills, NullLogger())
            .OfType<AIFunction>().Single(t => t.Name == toolName);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await tool.InvokeAsync(
            new AIFunctionArguments(args.ToDictionary(a => a.Key, a => a.Value)), deadline.Token);

        return result?.ToString() ?? "";
    }

    // ── Skill classes, declared here so the shared test application is untouched ──

    [MentorSkill(Name = "inline-skill", Description = "Has an inline body", Instructions = "The inline body.")]
    private sealed class InlineSkill;

    [MentorSkill(Name = "file-skill", Description = "Points at a file", InstructionsFile = "external.md")]
    private sealed class FileSkill;

    [MentorSkill(Name = "broken-file-skill", Description = "Points at nothing.", InstructionsFile = "missing.md")]
    private sealed class BrokenFileSkill;

    [MentorSkill(Name = "bare-skill", Description = "Nothing but a description.")]
    private sealed class BareSkill;

    [MentorSkill(Name = "abstract-skill", Description = "Should never register")]
    private abstract class AbstractSkill;
}
