using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Options;

/// <summary>
/// Story 1275 — the options that can be changed while the application runs. Every property of <see cref="MentorOptions"/>
/// declares when MentorAgent reads it; <see cref="MentorRuntimeOptions.Changeable"/> is made of the ones read again for
/// every session or at every turn, and each of those is a promise with a test, named by
/// <c>[Trait("RuntimeOption", name)]</c>, that changes the option on a running application and sees the change apply.
/// An option without a declaration fails here, so none ships without the decision; one that is not promised is read
/// before the application starts, and nothing says a later change works.
/// </summary>
public sealed class RuntimeOptionsTests
{
    private const string TraitName = "RuntimeOption";

    private static IReadOnlyList<string> OptionNames { get; } =
        [.. typeof(MentorOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)];

    /// <summary>Every [Fact] and [Theory] of this assembly with the options it names, its own and its class's.</summary>
    private static IReadOnlyList<(string Test, string Option)> OptionTests { get; } =
        [.. typeof(RuntimeOptionsTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                .SelectMany(m => OptionsNamedBy(m).Concat(OptionsNamedBy(type)).Select(o => ($"{type.Name}.{m.Name}", o))))];

    private static IEnumerable<string> OptionsNamedBy(MemberInfo member)
        => member.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(TraitAttribute)
                        && a.ConstructorArguments.Count == 2
                        && (string?)a.ConstructorArguments[0].Value == TraitName)
            .Select(a => (string)a.ConstructorArguments[1].Value!);

    // ── The list ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_option_declares_when_it_is_read()
    {
        OptionNames.Should().HaveCountGreaterThan(100, "the reflection reads MentorOptions");
        OptionNames.Except(MentorRuntimeOptions.Declared.Keys).Should().BeEmpty(
            "an option without [MentorOptionReadAt] has not been decided: Startup if it is read before the application " +
            "starts, Session or Turn — with a test — if a change made while it runs applies");
        MentorRuntimeOptions.Declared.Keys.Should().BeSubsetOf(OptionNames);
    }

    [Fact]
    public void The_changeable_options_are_the_ones_read_for_every_session_or_at_every_turn()
    {
        MentorRuntimeOptions.Changeable.Should().BeEquivalentTo(
            MentorRuntimeOptions.Declared.Where(d => d.Value != MentorOptionReadAt.Startup));
        MentorRuntimeOptions.Changeable.Should().NotBeEmpty();
        MentorRuntimeOptions.Changeable.Values.Should().OnlyContain(v => v == MentorOptionReadAt.Session || v == MentorOptionReadAt.Turn);

        MentorRuntimeOptions.IsChangeable(nameof(MentorOptions.Language)).Should().BeTrue();
        MentorRuntimeOptions.IsChangeable(nameof(MentorOptions.ScanAssemblies)).Should().BeFalse();
        MentorRuntimeOptions.IsChangeable("language").Should().BeFalse("the name is the property's, case and all");
        FluentActions.Invoking(() => MentorRuntimeOptions.IsChangeable(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Every_changeable_option_has_a_test_that_changes_it_while_the_application_runs()
    {
        var tested = OptionTests.Select(t => t.Option).ToHashSet(StringComparer.Ordinal);

        MentorRuntimeOptions.Changeable.Keys.Except(tested).Should().BeEmpty(
            "a Session or Turn option is a promise, and a promise has a test with [Trait(\"RuntimeOption\", name)]");
        OptionTests.Where(t => !MentorRuntimeOptions.IsChangeable(t.Option)).Should().BeEmpty(
            "a test names only an option that is promised; the others are read before the application starts");
    }

    [Fact]
    public void An_option_read_as_the_application_is_registered_is_never_promised()
    {
        // What AddMentorAgent reads off the configure callback's own copy, what MentorRegistrationSnapshot says is read at
        // registration, and what it copies once into MentorWidgetOptions: a change made later reaches none of them.
        var source = File.ReadAllText(Path.Combine(
            RepoFiles.Root, "Novior.MentorAgent", "Extensions", "ServiceCollectionExtensions.cs"));
        var widgetCopy = Regex.Match(source, @"AddOptions<MentorWidgetOptions>\(\)(.*?)\n\s*\}\);", RegexOptions.Singleline);
        widgetCopy.Success.Should().BeTrue("the copy into MentorWidgetOptions is where the test looks");

        var registered = new HashSet<string>(StringComparer.Ordinal);
        registered.UnionWith(Regex.Matches(source, @"\bsnapshot\.([A-Z]\w+)").Select(m => m.Groups[1].Value));
        registered.UnionWith(Regex.Matches(widgetCopy.Groups[1].Value, @"\bo\.([A-Z]\w+)").Select(m => m.Groups[1].Value));
        registered.UnionWith(OptionNames.Where(n => Regex.IsMatch(MentorRegistrationSnapshot.RegistrationTimeOptions, $@"\b{n}\b")));
        registered.Should().Contain(["EnableObservability", "McpServerEnabled", "BotName", "MaxImageBytes"],
            "the three places are read");

        // Language is copied too, but the widget never speaks the copy: its texts come from the localizer, which reads
        // MentorOptions.Language at every use (MentorWidgetOptions.Language says so).
        registered.Should().Contain(nameof(MentorOptions.Language));
        registered.Remove(nameof(MentorOptions.Language));

        registered.Where(MentorRuntimeOptions.IsChangeable).Should().BeEmpty(
            "an option read as the application is registered cannot be changed while it runs");
    }

    [Fact]
    public void The_readme_lists_the_changeable_options_as_the_code_does()
    {
        // The README's table is what someone who changes options from outside reads: it cannot fall behind the code.
        var text = File.ReadAllText(Path.Combine(RepoFiles.Root, "Novior.MentorAgent", "README.md")).Replace("\r\n", "\n");
        var section = Regex.Match(text, @"\n#{2,4} Changing options while the application runs\n(.*?)(?=\n#{1,4} )",
            RegexOptions.Singleline);
        section.Success.Should().BeTrue("the README has a section «Changing options while the application runs»");

        var listed = Regex.Matches(section.Groups[1].Value, @"^\|\s*`(?<name>\w+)`\s*\|\s*(?<when>Turn|Session)\s*\|",
                RegexOptions.Multiline)
            .ToDictionary(m => m.Groups["name"].Value, m => Enum.Parse<MentorOptionReadAt>(m.Groups["when"].Value));

        listed.Should().BeEquivalentTo(MentorRuntimeOptions.Changeable);
    }

    // ── Turn: the next turn of the same session sees the change ──────────────────

    private static MentorHostFixture Host(Action<MentorOptions>? configure = null, bool withRecorder = false)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, withRecorder: withRecorder);

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.RateLimitPerUser))]
    public async Task A_rate_limit_turned_off_while_the_application_runs_lets_the_next_turn_through()
    {
        await using var host = Host(o => { o.RateLimitPerUser = 1; o.RateLimitWindowSecs = 3600; });
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("first");
        await host.RunTurnAsync("second");
        host.Chat.CallCount.Should().Be(1, "the second message is over the limit");

        host.Options.RateLimitPerUser = 0;
        await host.RunTurnAsync("third");

        host.Chat.CallCount.Should().Be(2, "with the limit off, the next message reaches the model");
    }

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.RateLimitWindowSecs))]
    public async Task A_rate_limit_window_shortened_while_the_application_runs_applies_to_the_next_turn()
    {
        await using var host = Host(o => { o.RateLimitPerUser = 1; o.RateLimitWindowSecs = 3600; });
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("first");
        await host.RunTurnAsync("second");
        host.Chat.CallCount.Should().Be(1, "the second message is inside the hour");

        host.Options.RateLimitWindowSecs = 1;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await host.RunTurnAsync("third");

        host.Chat.CallCount.Should().Be(2, "the window of one second has passed since the first message");
    }

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.EnableActionFeedback))]
    public async Task Action_feedback_turned_off_while_the_application_runs_stops_from_the_next_turn()
    {
        await using var host = Host(withRecorder: true);
        host.Chat!.CallTool("count_open_orders").Say("There are 3.").CallTool("count_open_orders").Say("Still 3.");

        await host.RunTurnAsync("how many open orders?");
        host.Recorder!.Count(nameof(RecordingStateService.NotifyActionExecuting)).Should().Be(1);

        host.Options.EnableActionFeedback = false;
        await host.RunTurnAsync("and now?");

        host.Recorder.Count(nameof(RecordingStateService.NotifyActionExecuting)).Should().Be(1,
            "the second call runs without the bar of the running action");
    }

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.EnableSafetyCheck))]
    public async Task The_safety_check_turned_on_while_the_application_runs_checks_the_next_turn()
    {
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("SAFE", 4);
        await using var host = Host(o => o.ClassifierChatClient = classifier);
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("how many open orders?");
        classifier.CallCount.Should().Be(0, "the check is off");

        host.Options.EnableSafetyCheck = true;
        await host.RunTurnAsync("and the closed ones?");

        classifier.CallCount.Should().Be(1, "the next message is checked");
    }

    // ── Session: a session that begins after the change sees it ──────────────────

    /// <summary>A turn of a new session of the same application: another scope, as another circuit or hub connection is.</summary>
    private static async Task NewSessionTurnsAsync(MentorHostFixture host, params string[] messages)
    {
        await using var session = host.Get<IServiceScopeFactory>().CreateAsyncScope();
        var orchestrator = session.ServiceProvider.GetRequiredService<IMentorOrchestrator>();
        foreach (var message in messages)
        {
            try { await orchestrator.SendMessageAsync(message); }
            catch { /* a scripted model may not see a turn through; what it was sent is what is asserted */ }
        }
    }

    /// <summary>The instructions of the coordinator's last request: the last one that carried tools.</summary>
    private static string CoordinatorInstructions(MentorHostFixture host)
        => host.Chat!.Calls.Last(c => c.Tools.Count > 0).Instructions ?? "";

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.MentorshipLevel))]
    public async Task A_mentorship_level_changed_while_the_application_runs_applies_to_the_next_session()
    {
        await using var host = Host(o => o.MentorshipLevel = MentorshipLevel.Minimal);
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");
        CoordinatorInstructions(host).Should().Contain("direct and efficient").And.NotContain("Proactive Mentorship");

        host.Options.MentorshipLevel = MentorshipLevel.Proactive;
        await NewSessionTurnsAsync(host, "ciao");

        CoordinatorInstructions(host).Should().Contain("Proactive Mentorship");
    }

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.ProactiveNextSteps))]
    public async Task Next_steps_turned_on_while_the_application_runs_apply_to_the_next_session()
    {
        await using var host = Host(o => o.MentorshipLevel = MentorshipLevel.Proactive);
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");
        CoordinatorInstructions(host).Should().Contain("Do not close with suggested next steps");

        host.Options.ProactiveNextSteps = true;
        await NewSessionTurnsAsync(host, "ciao");

        CoordinatorInstructions(host).Should().Contain("ONLY things you can actually do")
            .And.NotContain("Do not close with suggested next steps");
    }

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.EnableRichResponses))]
    public async Task Rich_responses_turned_off_while_the_application_runs_apply_to_the_next_session()
    {
        await using var host = Host();
        host.Chat!.SayRepeatedly("ok", 4);

        await host.RunTurnAsync("ciao");
        CoordinatorInstructions(host).Should().Contain("## Response Formatting");

        host.Options.EnableRichResponses = false;
        await NewSessionTurnsAsync(host, "ciao");

        CoordinatorInstructions(host).Should().NotContain("## Response Formatting");
    }

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.RefuseOutOfScope))]
    public async Task Refusing_out_of_scope_turned_on_while_the_application_runs_applies_to_the_next_session()
    {
        var classifier = new FakeChatClient();
        classifier.SayRepeatedly("REFUSE", 4);
        await using var host = Host(o => o.ClassifierChatClient = classifier);
        host.Chat!.SayRepeatedly("una poesia", 4);

        await host.RunTurnAsync("scrivimi una poesia sul mare");
        var answered = host.Chat.CallCount;
        answered.Should().BeGreaterThan(0, "with the option off the message is answered");

        host.Options.RefuseOutOfScope = true;
        await NewSessionTurnsAsync(host, "scrivimi una poesia sul mare");

        classifier.CallCount.Should().Be(1, "the new session asks whether the message is in scope");
        host.Chat.CallCount.Should().Be(answered, "and refuses it without reaching the model");
    }

    [Fact]
    [Trait(TraitName, nameof(MentorOptions.MaxSessionMessages))]
    public async Task A_history_budget_changed_while_the_application_runs_applies_to_the_next_session()
    {
        await using var host = Host();
        host.Chat!.SayRepeatedly("ok", 12);

        foreach (var message in new[] { "question 1", "question 2", "question 3" })
            await host.RunTurnAsync(message);
        var underTheDefault = Conversation(host.Chat.Calls[^1]);
        underTheDefault.Should().BeGreaterThanOrEqualTo(5, "three questions and two answers fit in 50 messages");

        host.Options.MaxSessionMessages = 2;
        await NewSessionTurnsAsync(host, "question 1", "question 2", "question 3");

        Conversation(host.Chat.Calls[^1]).Should().BeLessThan(underTheDefault, "the new session keeps at most 2 messages");
    }

    private static int Conversation(FakeChatClient.RecordedCall call)
        => call.Messages.Count(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant);
}
