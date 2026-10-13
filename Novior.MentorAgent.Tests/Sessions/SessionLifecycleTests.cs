using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Agents.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Sessions;

/// <summary>
/// The conversation's own lifetime: creating a session, resetting it, and the serialize/restore
/// pair both READMEs publish under "Session serialize and restore".
///
/// <para>
/// <see cref="AgentSession"/> is where the Agent Framework keeps the history, and
/// <see cref="IMentorSessionManager"/> is the only thing in the package that owns one. Everything
/// else in the library reads history through it, so the properties worth pinning are ownership
/// properties: exactly one session exists at a time, a reset really replaces it, and a restore
/// really installs the one handed in rather than quietly keeping the current one.
/// </para>
/// </summary>
public sealed class SessionLifecycleTests
{
    private static AIAgent Agent(MentorHostFixture host)
        => Coordinator(host) ?? throw new InvalidOperationException("no coordinator was built");

    /// <summary>
    /// The coordinator, read out of the orchestrator by reflection.
    ///
    /// <para>
    /// Reflection because there is no public way to get it — which is the subject of the last two
    /// tests in this file. Every test that needs an agent to drive the session manager with has to
    /// reach for a private field, and that is itself the finding.
    /// </para>
    /// </summary>
    private static AIAgent? Coordinator(MentorHostFixture host)
    {
        var orchestrator = host.Orchestrator;
        var field = orchestrator.GetType()
            .GetField("_coordinator", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(orchestrator) as AIAgent;
    }

    private static IMentorSessionManager Sessions(MentorHostFixture host)
        => host.Get<IMentorSessionManager>();

    // ── One session, created once ───────────────────────────────────────────────

    [Fact]
    public async Task The_session_is_created_on_first_use_and_reused_after_that()
    {
        // The whole point of the manager. A second session would silently start a second
        // conversation: the model would answer the follow-up with no memory of the first question,
        // and nothing would look broken.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);

        var first = await sessions.GetOrCreateSessionAsync(agent);
        var second = await sessions.GetOrCreateSessionAsync(agent);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Twenty_callers_racing_for_the_session_all_get_the_same_one()
    {
        // The double-checked lock in GetOrCreateSessionAsync. On Blazor Server the manager is
        // scoped to the circuit, and a page that fires several actions on load reaches it
        // concurrently — losing that race splits one user's conversation in half.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);

        var all = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => sessions.GetOrCreateSessionAsync(agent))));

        all.Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task Asking_for_the_session_does_not_announce_a_change()
    {
        // OnSessionChanged means "the conversation you were showing is gone" — the widget clears
        // its message list on it. Firing it on ordinary use would wipe the transcript mid-turn.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var changed = 0;
        sessions.OnSessionChanged += () => changed++;

        await sessions.GetOrCreateSessionAsync(Agent(host));
        await sessions.GetOrCreateSessionAsync(Agent(host));

        changed.Should().Be(0);
    }

    // ── Reset ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reset_installs_a_different_session_and_says_so()
    {
        // The widget's reset button. Both halves matter: a new session (or the model still
        // remembers the conversation the user just abandoned) and the event (or the transcript
        // stays on screen while the model no longer knows about it).
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);
        var before = await sessions.GetOrCreateSessionAsync(agent);

        var changed = 0;
        sessions.OnSessionChanged += () => changed++;
        await sessions.ResetAsync(agent);

        var after = await sessions.GetOrCreateSessionAsync(agent);
        after.Should().NotBeSameAs(before);
        changed.Should().Be(1);
    }

    [Fact]
    public async Task Resetting_before_anything_was_created_still_leaves_a_usable_session()
    {
        // Reachable: the reset button is enabled before the first message is sent.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var fresh = MentorHostFixture.Create();
        await using var _ = fresh;
        fresh.Chat!.Say("ok");
        await fresh.RunTurnAsync("hello");

        var sessions = Sessions(fresh);
        await sessions.ResetAsync(Agent(fresh));

        (await sessions.GetOrCreateSessionAsync(Agent(fresh))).Should().NotBeNull();
    }

    [Fact]
    public async Task The_orchestrators_reset_goes_through_the_same_manager()
    {
        // ResetSessionAsync is the public entry point the widget and the hub both call. If it
        // reset something else, the button would look like it worked and change nothing.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var before = await sessions.GetOrCreateSessionAsync(Agent(host));

        var changed = 0;
        sessions.OnSessionChanged += () => changed++;
        await host.Orchestrator.ResetSessionAsync();

        (await sessions.GetOrCreateSessionAsync(Agent(host))).Should().NotBeSameAs(before);
        changed.Should().Be(1);
    }

    // ── Serialize and restore ───────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_to_serialize_reports_null_rather_than_an_empty_snapshot()
    {
        // The caller stores what comes back. An empty-but-present snapshot would be written over a
        // good one and restore to a blank conversation; null lets the caller keep what it has.
        //
        // Built directly rather than through the fixture: the manager answers null before it ever
        // touches the agent, and reaching this state through a host would mean building a
        // coordinator and then not using it.
        var snapshot = await new MentorSessionManager().SerializeCurrentSessionAsync(null!);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task A_live_session_serializes_to_something_restorable()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);
        await sessions.GetOrCreateSessionAsync(agent);

        var snapshot = await sessions.SerializeCurrentSessionAsync(agent);

        snapshot.Should().NotBeNull();
        snapshot!.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task Restoring_replaces_the_current_session_and_announces_it()
    {
        // The documented use: save, start a new conversation, come back to the old one.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);
        await sessions.GetOrCreateSessionAsync(agent);
        var snapshot = await sessions.SerializeCurrentSessionAsync(agent);

        await sessions.ResetAsync(agent);
        var afterReset = await sessions.GetOrCreateSessionAsync(agent);

        var changed = 0;
        sessions.OnSessionChanged += () => changed++;
        await sessions.RestoreSessionAsync(agent, snapshot!.Value);

        (await sessions.GetOrCreateSessionAsync(agent)).Should().NotBeSameAs(afterReset);
        changed.Should().Be(1);
    }

    [Fact]
    public async Task A_snapshot_that_is_not_a_session_fails_loudly_rather_than_silently()
    {
        // The snapshot comes back from localStorage, a cookie or a database — all of which can
        // hold something written by an older version, or by something else entirely. Whatever
        // happens, it must not be "restored successfully" into a session that is not the one asked
        // for: the user would be shown a transcript the model has never seen.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);
        var before = await sessions.GetOrCreateSessionAsync(agent);

        var junk = JsonSerializer.SerializeToElement(new { not = "a session" });

        var act = async () => await sessions.RestoreSessionAsync(agent, junk);

        (await act.Should().ThrowAsync<ArgumentException>()).Which
            .Message.Should().Contain("MentorAgent session snapshot");

        (await sessions.GetOrCreateSessionAsync(agent)).Should().BeSameAs(before,
            "a snapshot that could not be read must leave the live conversation alone");
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("{\"session\":{}}")]
    public async Task Every_shape_that_is_not_a_snapshot_is_refused(string json)
    {
        // The snapshot arrives from wherever the host put it, so "an object with the right two
        // properties" is the only thing that may be trusted. The Agent Framework's own deserializer
        // accepts most of these and answers with an empty session, which is what made the failure
        // silent.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);
        var before = await sessions.GetOrCreateSessionAsync(agent);

        using var doc = JsonDocument.Parse(json);
        var act = async () => await sessions.RestoreSessionAsync(agent, doc.RootElement.Clone());

        await act.Should().ThrowAsync<ArgumentException>();
        (await sessions.GetOrCreateSessionAsync(agent)).Should().BeSameAs(before);
    }

    [Fact]
    public async Task A_snapshot_from_an_incompatible_version_is_refused_by_version_rather_than_by_luck()
    {
        // The case the marker exists for. A future format change must be a clear refusal, not a
        // deserialiser that happens to cope and produces a subtly wrong conversation.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);
        await sessions.GetOrCreateSessionAsync(agent);
        var good = await sessions.SerializeCurrentSessionAsync(agent);

        var fromTheFuture = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["mentorAgentSession"] = 99,
            ["session"] = good!.Value.GetProperty("session"),
        });

        var act = async () => await sessions.RestoreSessionAsync(agent, fromTheFuture);

        (await act.Should().ThrowAsync<ArgumentException>()).Which
            .Message.Should().Contain("99");
    }

    [Fact]
    public async Task A_snapshot_carries_a_marker_so_it_can_be_told_apart_from_any_other_json()
    {
        // The property that makes every refusal above possible, pinned in its own right: without a
        // marker of our own there is no way to distinguish a real snapshot from arbitrary JSON,
        // because the framework's deserializer does not reject anything.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var sessions = Sessions(host);
        var agent = Agent(host);
        await sessions.GetOrCreateSessionAsync(agent);

        var snapshot = await sessions.SerializeCurrentSessionAsync(agent);

        snapshot!.Value.TryGetProperty("mentorAgentSession", out var marker).Should().BeTrue();
        marker.GetInt32().Should().Be(1);
        snapshot.Value.TryGetProperty("session", out _).Should().BeTrue();
    }

    // ── The round trip a host actually writes ───────────────────────────────────

    [Fact]
    public async Task The_orchestrator_saves_and_resumes_a_conversation_without_ever_seeing_the_agent()
    {
        // The documented feature, end to end, through the only API a host can reach.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("ok");
        await host.RunTurnAsync("hello");

        var snapshot = await host.Orchestrator.SerializeSessionAsync();
        snapshot.Should().NotBeNull();

        var sessions = Sessions(host);
        await host.Orchestrator.ResetSessionAsync();
        var afterReset = await sessions.GetOrCreateSessionAsync(Agent(host));

        await host.Orchestrator.RestoreSessionAsync(snapshot!.Value);

        (await sessions.GetOrCreateSessionAsync(Agent(host))).Should().NotBeSameAs(afterReset);
    }

    [Fact]
    public async Task Saving_before_the_first_message_reports_nothing_rather_than_building_a_coordinator()
    {
        // A host that saves on every page unload would otherwise pay for the whole discovery pass
        // on pages where nobody opened the widget.
        await using var host = MentorHostFixture.Create();

        (await host.Orchestrator.SerializeSessionAsync()).Should().BeNull();
        host.Chat!.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_restore_works_as_the_very_first_thing_a_reconnecting_host_does()
    {
        // The documented "restore on reconnect (e.g. after server restart)" case: a brand new scope
        // that has never sent a message and therefore has no coordinator yet.
        await using var source = MentorHostFixture.Create();
        source.Chat!.Say("ok");
        await source.RunTurnAsync("hello");
        var snapshot = await source.Orchestrator.SerializeSessionAsync();

        await using var reconnected = MentorHostFixture.Create();
        var act = async () => await reconnected.Orchestrator.RestoreSessionAsync(snapshot!.Value);

        await act.Should().NotThrowAsync();
    }

    // ── The API a host can actually call ────────────────────────────────────────

    [Fact]
    public void The_coordinator_agent_is_not_handed_out_to_application_code()
    {
        // Deliberate, and worth pinning: running the coordinator directly would bypass the
        // guardrails, the rate limiter, the role gate and the confirmation gate — every protection
        // the orchestrator applies around it. The only public AIAgent is MentorOptions.Agent, which
        // is an input the host supplies rather than the coordinator the library builds.
        var exposed = new[]
            {
                typeof(IMentorOrchestrator), typeof(IMentorSessionManager),
                typeof(IMentorStateService),
            }
            .SelectMany(t => t.GetMembers())
            .Where(m => ReturnTypeOf(m) == typeof(AIAgent))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}");

        string.Join(", ", exposed).Should().BeEmpty();
    }

    [Fact]
    public void The_session_snapshot_api_is_reachable_from_a_service_a_host_can_inject()
    {
        // Both READMEs publish a worked example of saving and restoring a session. Every method on
        // IMentorSessionManager takes the AIAgent as its first argument, and — as the test above
        // pins — nothing public hands that agent out. So the documented example cannot be written:
        // there is no `agent` to pass.
        //
        // The session API a host can actually reach has to live on the service that already owns
        // the coordinator. IMentorOrchestrator is that service: it is injectable, it is what the
        // widget and the hub both use, and it already carries the third session operation
        // (ResetSessionAsync) for exactly this reason.
        var members = typeof(IMentorOrchestrator).GetMethods().Select(m => m.Name).ToList();

        members.Should().Contain(m => m.Contains("Serialize", StringComparison.Ordinal),
            "the documented 'save the conversation' step must be callable without the coordinator");
        members.Should().Contain(m => m.Contains("Restore", StringComparison.Ordinal),
            "and so must the 'resume it' step");
    }

    [Fact]
    public void Both_readmes_show_the_session_example_against_an_api_a_host_can_call()
    {
        // A documentation claim rather than a code one, and the one that was wrong: both READMEs
        // published a worked example calling IMentorSessionManager with an `agent` argument that no
        // reader could obtain. The two packages describe the same feature and a reader following
        // either has to land on something they can actually compile.
        foreach (var name in new[] { "core", "server" })
        {
            var readme = RepoFiles.Readme(name);
            var section = readme[readme.IndexOf("## Session serialize and restore", StringComparison.Ordinal)..];
            section = section[..section.IndexOf("\n---", StringComparison.Ordinal)];

            section.Should().Contain("IMentorOrchestrator",
                $"the {name} README must point at the service that owns the coordinator");
            section.Should().Contain("SerializeSessionAsync",
                $"the {name} README documents saving a session");
            section.Should().Contain("RestoreSessionAsync",
                $"the {name} README documents restoring one");

            // The old example, which cannot be written. Mentioning the manager is fine — both
            // READMEs now explain why it is not the one to use — but not as the call to copy.
            section.Should().NotContain("SessionManager.SerializeCurrentSessionAsync",
                $"the {name} README must not show a call that needs an agent nobody can get");
            section.Should().NotContain("sessionManager.SerializeCurrentSessionAsync",
                $"the {name} README must not show a call that needs an agent nobody can get");
        }
    }

    private static Type? ReturnTypeOf(MemberInfo m) => m switch
    {
        MethodInfo method => method.ReturnType,
        PropertyInfo property => property.PropertyType,
        _ => null,
    };

}
