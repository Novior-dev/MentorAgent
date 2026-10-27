using Novior.MentorAgent.Models;
using FluentAssertions;
using Novior.MentorAgent.Core.Mcp;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// One connection per shared MCP server for the whole process (C1).
///
/// <para>
/// Measured live on rc.4: two browser tabs on the Blazor Server sample meant <b>eight</b> node
/// processes and 580 MB, because every circuit started its own copy of every configured stdio
/// server — and paid the start-up before it could answer its first message (3 s on a fresh process,
/// 12 s and 50 s with other circuits already running).
/// </para>
///
/// <para>
/// Everything here runs against a fake factory: no socket, no child process, no port. What is under
/// test is the pool's arithmetic and its failure behaviour, not npx.
/// </para>
/// </summary>
public sealed class McpClientPoolTests
{
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static MentorMcpServer Url(string name = "docs") =>
        new() { Name = name, ServerUrl = "http://127.0.0.1:59999/mcp" };

    private static MentorMcpServer Stdio(string name = "files", bool? shared = null) =>
        new() { Name = name, Command = "npx", Arguments = ["-y", "server"], Shared = shared };

    private static McpClientPool Pool(FakeMcpSessionFactory factory, TimeProvider? clock = null, TestLogCollector? logs = null)
        => new(factory, logs is null ? NullLogger<McpClientPool>.Instance
                                     : LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logs))).CreateLogger<McpClientPool>(), clock);

    // ── What "shared" means ─────────────────────────────────────────────────────

    [Fact]
    public void A_url_server_is_shared_by_default_and_a_command_server_is_not()
    {
        McpServerIdentity.IsShared(Url()).Should().BeTrue("a remote endpoint is shared by nature");
        McpServerIdentity.IsShared(Stdio()).Should().BeFalse(
            "a local child process can hold state for the person who started it");
    }

    [Fact]
    public void The_host_can_say_otherwise_in_either_direction()
    {
        McpServerIdentity.IsShared(Stdio(shared: true)).Should().BeTrue();
        McpServerIdentity.IsShared(new MentorMcpServer
        { Name = "x", ServerUrl = "http://x", Shared = false }).Should().BeFalse();
    }

    [Fact]
    public void Two_servers_that_differ_only_in_their_command_are_two_entries()
    {
        // The key is the transport signature: an edited entry must never be served the session
        // built from the old command line.
        var a = new MentorMcpServer { Name = "files", Command = "npx", Arguments = ["a"] };
        var b = new MentorMcpServer { Name = "files", Command = "npx", Arguments = ["b"] };

        McpServerIdentity.KeyOf(a).Should().NotBe(McpServerIdentity.KeyOf(b));
    }

    // ── One connection ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_sessions_share_one_connection()
    {
        var factory = new FakeMcpSessionFactory();
        var pool = Pool(factory);

        var first = await pool.GetOrConnectAsync(Url(), default);
        var second = await pool.GetOrConnectAsync(Url(), default);

        factory.Connects.Should().Be(1);
        second.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Eight_callers_arriving_together_still_open_one()
    {
        // Deploy time: everyone reconnects at once. Without the Lazy this is eight child processes.
        var factory = new FakeMcpSessionFactory { Gate = new TaskCompletionSource() };
        var pool = Pool(factory);

        var callers = Enumerable.Range(0, 8)
            .Select(_ => pool.GetOrConnectAsync(Url(), default)).ToList();
        factory.Gate.SetResult();
        var sessions = await Task.WhenAll(callers);

        factory.Connects.Should().Be(1);
        sessions.Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_take_the_others_with_it()
    {
        // A circuit dropping during a 50-second start must not cancel the connection everybody else
        // is waiting on.
        var factory = new FakeMcpSessionFactory { Gate = new TaskCompletionSource() };
        var pool = Pool(factory);

        using var quitter = new CancellationTokenSource();
        var abandoned = pool.GetOrConnectAsync(Url(), quitter.Token);
        var patient = pool.GetOrConnectAsync(Url(), default);

        quitter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        factory.Gate.SetResult();
        (await patient).ServerName.Should().Be("docs");
        factory.Connects.Should().Be(1);
    }

    // ── Failure ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_server_that_is_down_is_dialled_once_per_backoff_window()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var factory = new FakeMcpSessionFactory { ThrowOnConnect = new HttpRequestException("refused") };
        var pool = Pool(factory, clock);
        pool.FailureBackoff = TimeSpan.FromSeconds(30);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAnyAsync<Exception>(() => pool.GetOrConnectAsync(Url(), default));

        factory.Connects.Should().Be(1, "three visitors, one timeout");
    }

    [Fact]
    public async Task After_the_backoff_the_next_session_tries_again()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var factory = new FakeMcpSessionFactory { ThrowOnConnect = new HttpRequestException("refused") };
        var pool = Pool(factory, clock);
        pool.FailureBackoff = TimeSpan.FromSeconds(30);

        await Assert.ThrowsAnyAsync<Exception>(() => pool.GetOrConnectAsync(Url(), default));
        clock.Advance(TimeSpan.FromMinutes(1));
        factory.ThrowOnConnect = null;

        (await pool.GetOrConnectAsync(Url(), default)).ServerName.Should().Be("docs");
        factory.Connects.Should().Be(2);
    }

    // ── Liveness ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_session_that_died_is_replaced_rather_than_handed_out()
    {
        // The risk pooling introduces: the child process exits and every later visitor inherits a
        // corpse. The MCP ping is what makes that detectable.
        var factory = new FakeMcpSessionFactory();
        var pool = Pool(factory);

        var first = (FakeMcpSession)await pool.GetOrConnectAsync(Url(), default);
        first.Alive = false;

        var second = await pool.GetOrConnectAsync(Url(), default);

        second.Should().NotBeSameAs(first);
        factory.Connects.Should().Be(2);
        first.Disposed.Should().BeTrue("the dead one is closed, not leaked");
    }

    // ── A server that connects but does not answer ping (BUG-198) ───────────────

    /// <summary>
    /// The call, with a deadline: before BUG-198 a session that never answered ping was reopened in a loop, for as
    /// long as the caller waited.
    /// </summary>
    private static Task<IMcpSession> CallWithDeadline(McpClientPool pool)
        => pool.GetOrConnectAsync(Url(), new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

    [Fact]
    public async Task A_server_that_connects_but_never_answers_ping_is_left_out_like_one_that_is_down()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var logs = new TestLogCollector();
        var factory = new FakeMcpSessionFactory { NewSessionsAlive = false };
        var pool = Pool(factory, clock, logs);
        pool.FailureBackoff = TimeSpan.FromSeconds(30);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<McpSessionUnavailableException>(() => CallWithDeadline(pool));

        factory.Connects.Should().Be(1, "a session opened by this call that does not answer is a failed connect");
        factory.Sessions.Single().Disposed.Should().BeTrue("the session that does not answer is closed");
        logs.HasWarning("'docs' connected but does not answer ping").Should().BeTrue(logs.WarningText);
    }

    [Fact]
    public async Task A_cached_session_that_died_is_reopened_once_then_the_server_is_left_out()
    {
        var factory = new FakeMcpSessionFactory();
        var pool = Pool(factory, new TestClock(DateTimeOffset.UtcNow));

        var first = (FakeMcpSession)await pool.GetOrConnectAsync(Url(), default);
        first.Alive = false;
        factory.NewSessionsAlive = false;

        await Assert.ThrowsAsync<McpSessionUnavailableException>(() => CallWithDeadline(pool));

        factory.Connects.Should().Be(2, "the session that died is reopened once, and the new one does not answer either");
    }

    [Fact]
    public async Task After_the_backoff_a_server_that_did_not_answer_ping_is_tried_again()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var factory = new FakeMcpSessionFactory { NewSessionsAlive = false };
        var pool = Pool(factory, clock);
        pool.FailureBackoff = TimeSpan.FromSeconds(30);

        await Assert.ThrowsAsync<McpSessionUnavailableException>(() => CallWithDeadline(pool));
        clock.Advance(TimeSpan.FromMinutes(1));
        factory.NewSessionsAlive = true;

        (await CallWithDeadline(pool)).ServerName.Should().Be("docs");
        factory.Connects.Should().Be(2);
    }

    // ── Housekeeping ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_entry_that_is_no_longer_configured_is_closed()
    {
        var factory = new FakeMcpSessionFactory();
        var pool = Pool(factory);
        var session = (FakeMcpSession)await pool.GetOrConnectAsync(Url("old"), default);

        await pool.PruneAsync([Url("new")]);

        session.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Disposing_the_pool_closes_every_session()
    {
        var factory = new FakeMcpSessionFactory();
        var pool = Pool(factory);
        var a = (FakeMcpSession)await pool.GetOrConnectAsync(Url("a"), default);
        var b = (FakeMcpSession)await pool.GetOrConnectAsync(Url("b"), default);

        await pool.DisposeAsync();

        a.Disposed.Should().BeTrue();
        b.Disposed.Should().BeTrue();
    }
}
