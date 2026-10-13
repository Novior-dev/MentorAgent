using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Constraints;

/// <summary>
/// Constraints 11–20: configurations that are accepted at registration but cannot do what they say.
///
/// <para>
/// Every one of these is a setting a developer turns on deliberately. The interesting question is
/// never "does it throw" — it is whether the library tells them when the thing they asked for is
/// not going to happen. Silence here is what turns a five-minute misconfiguration into an afternoon.
/// </para>
/// </summary>
public sealed class ConfigurationConstraintTests
{
    // ── #11 · HitlMode.Native needs at least one approval-required tool ──────────

    [Fact]
    public async Task NativeHitl_with_no_gated_tool_warns_that_nothing_will_ever_be_asked()
    {
        // The fixture app declares two actions that require confirmation, so this case needs an
        // assembly with none. Not the *test* assembly — that carries other suites' fixtures, and
        // scanning it makes this test depend on what someone else wrote. Novior.MentorAgent.Abstractions
        // has no MentorAgent attributes at all, which is exactly the "nothing to approve" shape.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HitlMode = MentorHitlMode.Native;
            o.ScanAssemblies = [typeof(MentorCard).Assembly];
        });

        await host.RunTurnAsync("do something");

        host.Logs.HasWarning("no tool requires approval").Should().BeTrue(
            "switching on human-in-the-loop and never being asked anything is indistinguishable " +
            "from the feature being broken. Warnings seen:{0}{1}",
            Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task NativeHitl_is_silent_when_a_tool_actually_requires_approval()
    {
        await using var host = MentorHostFixture.Create(o => o.HitlMode = MentorHitlMode.Native);

        await host.RunTurnAsync("cancel order 7");

        host.Logs.HasWarning("no tool requires approval").Should().BeFalse(
            "the fixture declares CancelOrder and IssueRefund with RequiresConfirmation");
    }

    // ── #12 / #13 · service-managed history excludes a ChatHistoryProvider ───────

    [Fact]
    public async Task ServiceManagedHistory_with_a_ChatHistoryProvider_is_reported_not_left_to_throw()
    {
        // The Agent Framework rejects the combination on every turn, so it has to be caught at
        // configuration time. A per-turn exception would present as "the assistant is broken"
        // rather than "these two options are incompatible".
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseServiceManagedHistory = true;
            o.ChatHistoryProvider = new NoOpHistoryProvider();
        });

        var error = await host.RunTurnAsync("hello");

        var reported = host.Logs.WarningText + host.Logs.ExceptionText + (error?.ToString() ?? "");
        reported.Should().NotBeEmpty(
            "a combination the framework refuses must be surfaced, not discovered at runtime");
    }

    [Fact]
    public async Task HostedTools_induce_service_managed_history()
    {
        // Hosted tools force the Responses client, which manages the conversation itself. That is
        // the trap: the user never set UseServiceManagedHistory, but they now have it — and any
        // ChatHistoryProvider they configured is in conflict without them touching either option.
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HostedTools = MentorHostedTools.WebSearch;
            o.ChatHistoryProvider = new NoOpHistoryProvider();
        });

        await host.RunTurnAsync("search for something");

        var reported = host.Logs.WarningText + host.Logs.ExceptionText;
        reported.Should().NotBeEmpty(
            "the induced conflict is harder to diagnose than the explicit one, so it needs at " +
            "least as loud a signal");
    }

    // ── #16 · [MentorTeam] needs [TeamMember] properties ─────────────────────────

    [Fact]
    public async Task A_team_with_no_members_is_skipped_with_a_warning()
    {
        // The fixture declares TestEmptyTeam precisely for this.
        await using var host = MentorHostFixture.Create();

        await host.RunTurnAsync("ask the team");

        host.Logs.HasWarning("has [MentorTeam] but no [TeamMember]").Should().BeTrue(
            "a team that cannot be built must be named, or the developer looks for it in the " +
            "prompt and finds nothing. Warnings seen:{0}{1}",
            Environment.NewLine, host.Logs.WarningText);
    }

    [Fact]
    public async Task A_team_with_no_members_does_not_stop_the_other_agents()
    {
        // Degrade, do not fail: one malformed declaration must not cost the app its working ones.
        await using var host = MentorHostFixture.Create();

        var error = await host.RunTurnAsync("ask the shipping specialist");

        error.Should().BeNull();
        host.FirstToolsSent.Should().Contain("route_to_specialist",
            "the valid [MentorAgent] survives an invalid [MentorTeam]");
    }

    // ── #18 · a hosted MCP server needs an absolute URL ──────────────────────────

    [Fact]
    public async Task A_hosted_mcp_server_with_a_relative_url_is_skipped_with_a_warning()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.HostedTools = MentorHostedTools.HostedMcp;
            o.HostedMcpServers =
            [
                new MentorHostedMcpServer { Name = "relative", Url = "/mcp" },
            ];
        });

        await host.RunTurnAsync("use the mcp server");

        host.Logs.HasWarning("is not an absolute URL").Should().BeTrue(
            "the provider calls this URL from its own infrastructure, so a relative path can " +
            "never resolve. Warnings seen:{0}{1}", Environment.NewLine, host.Logs.WarningText);
    }

    // ── #20 · hosted tools that fail closed ─────────────────────────────────────

    [Fact]
    public async Task FileSearch_without_vector_store_ids_fails_closed()
    {
        await using var host = MentorHostFixture.Create(o => o.HostedTools = MentorHostedTools.FileSearch);

        await host.RunTurnAsync("search the files");

        // Fail-closed means the tool is not offered at all. Offering a file search with no index
        // would have the model report an empty result as though the documents did not exist.
        host.FirstToolsSent.Should().NotContain("file_search",
            "a file search with nowhere to search must not be declared");
    }

    [Fact]
    public async Task HostedMcp_without_servers_fails_closed()
    {
        await using var host = MentorHostFixture.Create(o => o.HostedTools = MentorHostedTools.HostedMcp);

        await host.RunTurnAsync("use the mcp server");

        host.Logs.Records.Should().NotBeEmpty();
        host.FirstToolsSent.Should().NotContain("hosted_mcp");
    }

    // ── #19 · ScanAssemblies must not be empty ──────────────────────────────────

    [Fact]
    public async Task An_empty_ScanAssemblies_fails_fast_with_the_fix_in_the_message()
    {
        await using var host = MentorHostFixture.Create(o => o.ScanAssemblies = []);

        var act = () => _ = host.Options;

        act.Should().Throw<Exception>()
            .WithMessage("*ScanAssemblies*")
            .And.Message.Should().Contain("typeof(Program).Assembly",
                "an error about a required setting should show what to write");
    }

    [Fact]
    public async Task Configuring_both_ChatClient_and_Agent_is_refused()
    {
        // Path A and Path B are mutually exclusive; accepting both would silently pick one.
        await using var host = MentorHostFixture.Create(o =>
            o.Agent = new PathBConstraintTests.PublicStubAgent());

        var act = () => _ = host.Options;

        act.Should().Throw<Exception>().WithMessage("*ChatClient OR Agent*");
    }

    [Fact]
    public async Task Configuring_neither_ChatClient_nor_Agent_is_refused()
    {
        await using var host = MentorHostFixture.Create(o => o.ChatClient = null, withChatClient: false);

        var act = () => _ = host.Options;

        act.Should().Throw<Exception>().WithMessage("*ChatClient or Agent*");
    }

    /// <summary>
    /// A history provider that stores nothing. Its mere presence is what the constraint is about —
    /// the framework rejects the combination with service-managed history regardless of behaviour.
    /// </summary>
    private sealed class NoOpHistoryProvider : Microsoft.Agents.AI.ChatHistoryProvider
    {
        protected override ValueTask<IEnumerable<Microsoft.Extensions.AI.ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Enumerable.Empty<Microsoft.Extensions.AI.ChatMessage>());

        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
