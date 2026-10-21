using System.Reflection;
using FluentAssertions;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Levels;

/// <summary>
/// Level 3 — the group-chat team, as the coordinator and the user experience it.
///
/// <para>
/// A team is a workflow of several agents talking to each other, exposed to the coordinator as a
/// single tool. Two things about it leak if they are not handled: the internal coordination tokens
/// the members use to agree (<c>[TEAM:APPROVED]</c>) and the identity of whoever is currently
/// speaking. The first must never reach the user — it is protocol, not prose — and the second must
/// reach them, because a team turn is slow and silence looks like a hang.
/// </para>
///
/// <para>
/// The teams here are built by hand rather than discovered. The shared test application deliberately
/// has exactly one team, and several suites assert that; constructing <c>TeamDiscoveryInfo</c>
/// directly is what makes handoffs, iteration limits and custom termination testable without
/// changing what every other suite sees.
/// </para>
/// </summary>
public sealed class TeamTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────

    /// <summary>Termination predicates a team can be given, including a broken one.</summary>
    private static class Terminations
    {
        public static bool AfterApprover(string lastMessage, string lastSpeaker)
            => lastSpeaker == "Approver" && lastMessage.Contains("[TEAM:APPROVED]");

        public static bool Never(string lastMessage, string lastSpeaker) => false;

        public static bool Throws(string lastMessage, string lastSpeaker)
            => throw new InvalidOperationException("the host's predicate is broken");

        public static MethodInfo Method(string name)
            => typeof(Terminations).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
    }

    private static TeamMemberInfo Member(string role, string instructions = "Do your part.")
        => new(typeof(object),
               new TeamMemberAttribute { Role = role, Instructions = instructions },
               []);

    private static TeamDiscoveryInfo Team(
        string name = "Business Analysis Team",
        string description = "Analyses business performance",
        string[]? handoffTo = null,
        int maxIterations = 2,
        MethodInfo? termination = null,
        params string[] roles)
        => new(typeof(TeamTests),
               new MentorTeamAttribute
               {
                   Name = name,
                   Description = description,
                   HandoffTo = handoffTo ?? [],
                   MaxIterations = maxIterations,
               },
               (roles.Length > 0 ? roles : ["Analyst", "Approver"]).Select(r => Member(r)).ToList(),
               termination);

    private static AIFunction ToolFor(MentorHostFixture host, TeamDiscoveryInfo team)
        => (AIFunction)host.Get<GroupChatBuilder>().BuildTeamTool(team, host.Chat!);

    /// <summary>
    /// Runs the team tool under a hard deadline.
    ///
    /// <para>
    /// The deadline is not defensive padding: an unbounded group chat is exactly the defect this
    /// file found (BUG-018), and without it a regression does not fail — it runs hundreds of model
    /// calls and takes eleven minutes to say so. A test for a runaway loop has to be able to stop
    /// the loop.
    /// </para>
    /// </summary>
    private static Task<string> RunAsync(MentorHostFixture host, TeamDiscoveryInfo team,
                                         string task = "analyse last quarter")
        => InvokeAsync(ToolFor(host, team), task);

    /// <summary>One activation of a team tool already built, as the coordinator makes it.</summary>
    private static async Task<string> InvokeAsync(AIFunction tool, string task = "analyse last quarter")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await tool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["task"] = task }),
            deadline.Token);

        return result?.ToString() ?? "";
    }

    // ── The tool the coordinator is offered ─────────────────────────────────────

    [Fact]
    public async Task The_team_is_exposed_under_a_snake_case_name()
    {
        // A team name is prose written by a developer — "Business Analysis Team". Most providers
        // reject a tool name with spaces outright, so the name has to be derived, not used.
        await using var host = MentorHostFixture.Create();

        ToolFor(host, Team()).Name.Should().Be("business_analysis_team");
    }

    [Fact]
    public async Task The_tool_description_is_the_teams_own()
    {
        // It is the only thing the coordinator has to decide when to activate the team.
        await using var host = MentorHostFixture.Create();

        ToolFor(host, Team()).Description.Should().Contain("Analyses business performance");
    }

    [Fact]
    public async Task A_team_that_hands_off_says_so_in_its_description()
    {
        await using var host = MentorHostFixture.Create();

        ToolFor(host, Team(handoffTo: ["ShippingAgent"])).Description
            .Should().Contain("After approval, hand off to: ShippingAgent");
    }

    [Fact]
    public async Task A_team_that_does_not_hand_off_says_nothing_about_it()
    {
        await using var host = MentorHostFixture.Create();

        ToolFor(host, Team()).Description.Should().NotContain("hand off to");
    }

    // ── The coordination sentinels never reach the user ─────────────────────────

    [Fact]
    public async Task The_approval_sentinel_is_stripped_from_the_result()
    {
        // [TEAM:APPROVED] is how members signal agreement to each other. It is protocol, and the
        // coordinator relays what the team returns almost verbatim — so leaving it in puts a
        // bracketed machine token in the middle of the user's answer.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Revenue is up 12%. [TEAM:APPROVED]");

        var result = await RunAsync(host, Team());

        result.Should().NotContain("[TEAM:APPROVED]");
        result.Should().Contain("Revenue is up 12%.");
    }

    [Fact]
    public async Task The_rejection_sentinel_is_stripped_too()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("The numbers do not add up. [TEAM:REJECTED]");

        var result = await RunAsync(host, Team());

        result.Should().NotContain("[TEAM:REJECTED]").And.Contain("The numbers do not add up.");
    }

    [Fact]
    public async Task The_sentinel_is_stripped_whatever_its_case()
    {
        // A model asked to emit a token will sometimes lower-case it. The strip is
        // case-insensitive; the approval *check* must be too, or the two disagree.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Looks good. [team:approved]");

        (await RunAsync(host, Team())).ToLowerInvariant().Should().NotContain("team:approved");
    }

    [Fact]
    public async Task The_result_is_trimmed_so_a_stripped_sentinel_leaves_no_gap()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("[TEAM:APPROVED]");

        (await RunAsync(host, Team())).Should().BeEmpty();
    }

    // ── Handoff after approval ──────────────────────────────────────────────────

    [Fact]
    public async Task An_approved_team_with_a_handoff_tells_the_coordinator_to_proceed()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Plan accepted. [TEAM:APPROVED]");

        var result = await RunAsync(host, Team(handoffTo: ["ShippingAgent"]));

        result.Should().Contain("ready to proceed");
        // BUG-144: the actions run through a specialist, and one that changes data asks the user first. Told
        // only "now execute", a coordinator whose action was then declined reported a problem that never happened.
        result.Should().Contain("if the user declines, it did not run, and that is the user's decision, not a problem");
    }

    [Fact]
    public async Task A_team_that_did_not_approve_is_not_reported_as_approved()
    {
        // The check is on the bracketed sentinel rather than on the word "approved", precisely so
        // that "the plan has NOT been approved" cannot be read as consent. That is the whole reason
        // the token is structured.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("The plan has not been approved — the margins are wrong.");

        var result = await RunAsync(host, Team(handoffTo: ["ShippingAgent"]));

        result.Should().NotContain("ready to proceed");
    }

    // ── The verdict (BUG-175) ───────────────────────────────────────────────────
    //
    // The sentinel was looked for anywhere in the discussion, so an Analyst who agreed and an Approver
    // who then said no still sent the coordinator "Now execute the approved actions". The rule chosen
    // for 1.1.0: approved only if the final message carries [TEAM:APPROVED] and no member wrote
    // [TEAM:REJECTED]. Without a condition of the host's the first verdict ends the discussion, so that
    // the message carrying it is the final one.

    private const string Execute = "Now execute the approved actions";

    [Fact]
    public async Task Without_a_condition_the_first_yes_ends_the_discussion_and_approves()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Revenue is up 12%.").Say("The margins hold.")
            .Say("Plan accepted. [TEAM:APPROVED]").Say("One more thought.");

        var result = await RunAsync(host, Team(handoffTo: ["ShippingAgent"], maxIterations: 10));

        host.Chat!.Calls.Should().HaveCount(3,
            "up to 1.0.0 the round robin ran all ten rounds, and the yes was no longer the final message");
        result.Should().Contain(Execute).And.NotContain("One more thought");
    }

    [Fact]
    public async Task Without_a_condition_the_first_no_ends_the_discussion_and_is_not_approved()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Revenue is up 12%.").Say("No: the margins are wrong. [TEAM:REJECTED]")
            .Say("Fine by me. [TEAM:APPROVED]");

        var result = await RunAsync(host, Team(handoffTo: ["ShippingAgent"], maxIterations: 10));

        host.Chat!.Calls.Should().HaveCount(2, "a definitive no is a verdict too");
        result.Should().NotContain(Execute).And.NotContain("Fine by me");
    }

    [Fact]
    public async Task Without_a_verdict_the_discussion_is_not_approved()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Revenue is up 12%.").Say("The margins hold.");

        var result = await RunAsync(host, Team(handoffTo: ["ShippingAgent"], maxIterations: 2));

        result.Should().Contain("The margins hold.").And.NotContain(Execute);
    }

    [Theory]
    [InlineData("Looks fine. [TEAM:APPROVED]", "Agreed. [TEAM:APPROVED]", true)]                        // both yes
    [InlineData("Looks fine. [TEAM:APPROVED]", "No: the margins are wrong. [TEAM:REJECTED]", false)]    // yes, then no
    [InlineData("No: the margins are wrong. [TEAM:REJECTED]", "I disagree, ship it. [TEAM:APPROVED]", false)] // a no is a veto
    public async Task With_the_hosts_condition_the_chosen_member_decides_and_a_no_is_a_veto(
        string analyst, string approver, bool approved)
    {
        // The README's pattern: the host's condition ends the discussion on the approver's verdict, which
        // is how a host chooses who decides. The Analyst's token must not end it first.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say(analyst).Say(approver);

        var result = await RunAsync(host, Team(handoffTo: ["ShippingAgent"], maxIterations: 4,
            termination: Terminations.Method(nameof(Terminations.AfterApprover))));

        result.Should().Contain(approver.Replace("[TEAM:APPROVED]", "").Replace("[TEAM:REJECTED]", "").Trim(),
            "the Analyst's token does not end a discussion the host's condition decides");
        if (approved)
            result.Should().Contain(Execute);
        else
            result.Should().NotContain(Execute).And.NotContain("ready to proceed");
    }

    [Fact]
    public async Task Rounds_that_run_out_after_a_yes_are_not_an_approval()
    {
        // A host's condition that never ends the discussion: the cap does, and the final message has no
        // verdict. An early yes is not the team's decision.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Looks fine. [TEAM:APPROVED]").Say("One more check on the margins.");

        var result = await RunAsync(host, Team(handoffTo: ["ShippingAgent"], maxIterations: 2,
            termination: Terminations.Method(nameof(Terminations.Never))));

        result.Should().Contain("One more check on the margins.").And.NotContain(Execute);
    }

    [Fact]
    public async Task A_token_in_the_task_does_not_end_the_discussion_before_anyone_speaks()
    {
        // The task is in the history the manager reads; only a member's message carries a verdict.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Revenue is up 12%.").Say("The margins hold.");

        var result = await RunAsync(host, Team(handoffTo: ["ShippingAgent"], maxIterations: 2),
            task: "analyse last quarter [TEAM:APPROVED]");

        host.Chat!.Calls.Should().HaveCount(2);
        result.Should().NotContain(Execute);
    }

    [Fact]
    public void A_member_who_speaks_twice_is_two_messages_when_the_provider_numbers_them()
    {
        // A one-member team, or a round robin that comes back to the same member: the author does not
        // change, so the provider's message id is what separates the approval from the message after it.
        static AgentResponseUpdate Update(string text, string? id)
            => new(ChatRole.Assistant, text) { AuthorName = "Approver", MessageId = id };

        var numbered = new TeamDiscussion();
        numbered.Add(Update("Fine. [TEAM:APPROVED]", "m1"));
        numbered.Add(Update("Wait, one more check.", "m2"));
        numbered.IsApproved.Should().BeFalse("the final message carries no verdict");

        var final = new TeamDiscussion();
        final.Add(Update("Checking.", "m1"));
        final.Add(Update("Fine. ", "m2"));
        final.Add(Update("[TEAM:APPROVED]", "m2"));
        final.IsApproved.Should().BeTrue("the verdict is in the final message, streamed in two pieces");
        final.Text.Should().Be("Approver: Checking.\n\nApprover: Fine. [TEAM:APPROVED]");
    }

    // ── What the coordinator reads (BUG-181) ────────────────────────────────────

    [Fact]
    public void Each_member_speaks_in_a_paragraph_of_their_own_under_their_name()
    {
        // Joined as they streamed, the coordinator read "The ticket is solved.I agree, keep it open." and could not
        // tell who said what.
        var discussion = new TeamDiscussion();
        discussion.Add(new AgentResponseUpdate(ChatRole.Assistant, "The ticket ") { AuthorName = "Analyst" });
        discussion.Add(new AgentResponseUpdate(ChatRole.Assistant, "is solved.") { AuthorName = "Analyst" });
        discussion.Add(new AgentResponseUpdate(ChatRole.Assistant, "I agree, keep it open.") { AuthorName = "Approver" });

        discussion.Text.Should().Be("Analyst: The ticket is solved.\n\nApprover: I agree, keep it open.");
    }

    [Theory]
    [InlineData("Decides whether the ticket is ready to be closed")]
    [InlineData("Decides whether the ticket is ready to be closed.")]
    public void The_handoff_note_follows_the_description_as_a_sentence_of_its_own(string description)
        => GroupChatBuilder.TeamToolDescription(description, ["ShippingAgent"])
            .Should().Be("Decides whether the ticket is ready to be closed. After approval, hand off to: ShippingAgent.");

    [Fact]
    public void Without_a_handoff_the_description_is_left_as_written()
        => GroupChatBuilder.TeamToolDescription("Analyses business performance", [])
            .Should().Be("Analyses business performance");

    [Fact]
    public async Task The_protocol_keeps_the_rejection_token_for_a_definitive_no()
    {
        // With a REJECTED anywhere vetoing the plan, a member who asked for rework with the token would
        // block it for good. The instruction says so, and asks for changes without a token.
        await using var host = MentorHostFixture.Create();
        var instructions = host.Get<MentorPromptBuilder>().BuildTeamMemberInstructions(Member("Approver"));

        instructions.Should().Contain("REJECTED for good and must not go ahead");
        instructions.Should().Contain("If it only needs changes, say what to change and write neither token");
        instructions.Should().Contain("approves only if the final message contains `[TEAM:APPROVED]` and no member wrote `[TEAM:REJECTED]`");
        instructions.Should().NotContain("needs rework: your message MUST contain");
    }

    [Fact]
    public async Task Approval_without_a_handoff_target_adds_nothing()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("All good. [TEAM:APPROVED]");

        (await RunAsync(host, Team())).Should().NotContain("ready to proceed");
    }

    // ── Who is speaking ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_member_that_starts_speaking_is_announced()
    {
        // A team turn runs several model calls back to back. Without this the widget shows nothing
        // for the whole discussion and the user assumes it has hung.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Say("my contribution");

        await RunAsync(host, Team(maxIterations: 2, roles: ["Analyst", "Approver"]));

        var speakers = host.Recorder!.Log
            .Where(e => e.Event == nameof(RecordingStateService.NotifyTeamMemberSpeaking))
            .Select(e => (string)e.Args[1]!)
            .ToList();

        string.Join(",", speakers.Distinct()).Should().Contain("Analyst").And.Contain("Approver");
    }

    [Fact]
    public async Task The_event_carries_the_team_name_as_well_as_the_member()
    {
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Say("my contribution");

        await RunAsync(host, Team(name: "Risk Committee"));

        host.Recorder!.Log
            .Where(e => e.Event == nameof(RecordingStateService.NotifyTeamMemberSpeaking))
            .Select(e => (string)e.Args[0]!)
            .Should().OnlyContain(t => t == "Risk Committee");
    }

    [Fact]
    public async Task A_member_speaking_several_chunks_is_announced_once()
    {
        // The event fires on a *change* of speaker, not per update — the fake streams word-sized
        // deltas, so a per-update notification would fire for every word.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.SayRepeatedly("one two three four five", 20);

        await RunAsync(host, Team(maxIterations: 1, roles: ["Solo"]));

        host.Recorder!.Log
            .Count(e => e.Event == nameof(RecordingStateService.NotifyTeamMemberSpeaking))
            .Should().Be(1);
    }

    // ── Termination ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_custom_condition_the_team_stops_at_MaxIterations()
    {
        // The safety net. A round-robin with no exit condition is an unbounded bill.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("still discussing", 50);

        await RunAsync(host, Team(maxIterations: 2, roles: ["A", "B"]));

        host.Chat.CallCount.Should().BeLessThanOrEqualTo(4,
            "two members over two iterations is the ceiling");
    }

    [Fact]
    public async Task A_lower_iteration_limit_really_is_lower()
    {
        await using var shortRun = MentorHostFixture.Create();
        shortRun.Chat!.SayRepeatedly("still discussing", 50);
        await using var longRun = MentorHostFixture.Create();
        longRun.Chat!.SayRepeatedly("still discussing", 50);

        await RunAsync(shortRun, Team(maxIterations: 1, roles: ["A", "B"]));
        await RunAsync(longRun, Team(maxIterations: 3, roles: ["A", "B"]));

        longRun.Chat.CallCount.Should().BeGreaterThan(shortRun.Chat.CallCount);
    }

    [Fact]
    public async Task A_custom_termination_condition_ends_the_discussion_early()
    {
        // [TeamTerminationCondition] is reached through DelegateTerminationManager. The signature is
        // fixed — bool(string lastMessage, string lastSpeaker) — and it is invoked reflectively, so
        // nothing checks it at compile time and only a test can say it is wired.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("verdict: [TEAM:APPROVED]", 50);

        await RunAsync(host, Team(
            maxIterations: 10,
            termination: Terminations.Method(nameof(Terminations.AfterApprover)),
            roles: ["Analyst", "Approver"]));

        host.Chat.CallCount.Should().BeLessThan(20,
            "the condition must have stopped it well before ten round-robin iterations");
    }

    [Fact]
    public async Task A_condition_that_never_fires_still_stops_at_MaxIterations()
    {
        // BUG-018. DelegateTerminationManager overrode ShouldTerminateAsync without consulting the
        // base implementation — which is the one that enforces MaximumIterationCount. Supplying a
        // [TeamTerminationCondition] therefore *removed* the iteration cap instead of adding to it,
        // and a predicate that never fires ran the group chat with no bound at all. Observed on the
        // first run of this test: 770 model calls against a MaxIterations of 2.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("no verdict yet", 50);

        await RunAsync(host, Team(
            maxIterations: 2,
            termination: Terminations.Method(nameof(Terminations.Never)),
            roles: ["A", "B"]));

        host.Chat.CallCount.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task A_termination_condition_that_throws_does_not_take_the_team_down()
    {
        // The predicate is the host's own code, invoked reflectively on every message. Letting an
        // exception out of it would abort a turn the user is watching; treating it as "do not
        // terminate" degrades to the iteration limit, which is bounded.
        await using var host = MentorHostFixture.Create();
        host.Chat!.SayRepeatedly("still discussing", 50);

        var act = async () => await RunAsync(host, Team(
            maxIterations: 2,
            termination: Terminations.Method(nameof(Terminations.Throws)),
            roles: ["A", "B"]));

        await act.Should().NotThrowAsync();
        host.Chat.CallCount.Should().BeLessThanOrEqualTo(4, "it fell back to the iteration limit");
    }

    // ── Several activations in one session ──────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_team_discusses_at_every_activation_in_the_same_session(bool customTermination)
    {
        // BUG-146. The team tool is built once per session, and its workflow was handed the same
        // group-chat manager for every run. The manager counts the iterations and nothing reset
        // the count, so once a session had spent MaxIterations in total the team was "activated"
        // with no member speaking and an empty result: PricingReviewTeam (MaxIterations 6) on
        // MentorAgentNuget discussed twice, and from then on the assistant said the team was
        // still deciding. Both managers are covered: the round-robin one and the one a
        // [TeamTerminationCondition] gets.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.SayRepeatedly("still discussing", 200);
        var tool = ToolFor(host, Team(
            maxIterations: 2,
            termination: customTermination ? Terminations.Method(nameof(Terminations.Never)) : null,
            roles: ["A", "B"]));

        var calls = new List<int>();
        var speakers = new List<int>();
        for (var activation = 1; activation <= 4; activation++)
        {
            var callsBefore = host.Chat.CallCount;
            var speakersBefore = SpeakingEvents(host);

            var result = await InvokeAsync(tool, $"proposal {activation}");

            calls.Add(host.Chat.CallCount - callsBefore);
            speakers.Add(SpeakingEvents(host) - speakersBefore);
            result.Should().Contain("still discussing", $"activation {activation} must hear the members");
        }

        calls.Should().OnlyContain(n => n > 0 && n == calls[0],
            "every activation gets its own MaxIterations: 4 activations of a 2-iteration team are 8 " +
            "iterations in the session, far past the limit of one discussion");
        speakers.Should().OnlyContain(n => n > 0, "the widget shows the members speaking every time");
    }

    private static int SpeakingEvents(MentorHostFixture host)
        => host.Recorder!.Log.Count(e => e.Event == nameof(RecordingStateService.NotifyTeamMemberSpeaking));

    // ── Member tools ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_member_whose_tool_class_is_missing_from_DI_is_reported_and_the_team_still_builds()
    {
        // The tools come from [TeamMember(Tools = [typeof(X)])], and nothing checks that X was ever
        // registered. Failing the whole team over one unregistered class would take down a feature
        // over a typo in an attribute.
        await using var host = MentorHostFixture.Create();

        var member = new TeamMemberInfo(
            typeof(object),
            new TeamMemberAttribute
            {
                Role = "Analyst",
                Instructions = "Interpret the numbers.",
                Tools = [typeof(UnregisteredTools)],
            },
            [new MethodDiscoveryInfo(
                DeclaringType: typeof(UnregisteredTools),
                Method: typeof(UnregisteredTools).GetMethod(nameof(UnregisteredTools.Report))!,
                ToolName: "report",
                Description: "Reports",
                Category: "Tool",
                RequiresConfirmation: false,
                ProactiveHint: null,
                RequiredRoles: [],
                NavigateTo: null)]);

        var team = new TeamDiscoveryInfo(
            typeof(TeamTests),
            new MentorTeamAttribute { Name = "Broken Team", Description = "Has an unregistered tool" },
            [member, Member("Approver")],
            null);

        var act = () => ToolFor(host, team);

        act.Should().NotThrow();
        host.Logs.HasWarning("not found in DI").Should().BeTrue(host.Logs.WarningText);
    }

    /// <summary>A tool class deliberately never registered in the container.</summary>
    private sealed class UnregisteredTools
    {
        public string Report() => "report";
    }
}
