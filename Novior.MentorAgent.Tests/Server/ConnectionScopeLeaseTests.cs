using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Server.Auth;
using Novior.MentorAgent.Server.Hubs;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// A request that works on a connection's scope does not race the connection's close (BUG-254).
///
/// <para>
/// Up to 1.4.0 the endpoints of approve, cancel, recipe-result and session took the scope with
/// <c>MentorHubScopes.TryGet</c> and resolved services from it a moment later. A connection that closed in
/// between had its scope disposed under the request, which answered 500 with an
/// <c>ObjectDisposedException</c> in the log, where a closed connection is a 404. It failed closed: nothing was
/// approved and nothing was read.
/// </para>
/// </summary>
public sealed class ConnectionScopeLeaseTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_session_restore_whose_connection_closes_while_its_body_comes_is_a_404_and_not_a_500()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        var hub = app.CreateHubConnection();
        await hub.StartAsync();
        await hub.InvokeAsync("SendMessage", "ciao", null);
        var connectionScope = app.Services.GetRequiredService<MentorHubScopes>().TryGet(hub.ConnectionId!)!;

        // The body starts with more blank space than the request's pipe holds, so its first write completes
        // only once the endpoint reads it: past the owner's check, inside the body. The rest waits for the close.
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var post = app.Client.PostAsync($"/mentor/session?connectionId={hub.ConnectionId}",
            new LateBody(reading, closed.Task));

        (await Task.WhenAny(reading.Task, Task.Delay(Patience))).Should().BeSameAs(reading.Task,
            "the endpoint reads the body once the owner is checked");
        await hub.DisposeAsync();   // the owner closes the tab while the body comes
        await Eventually(() => IsDisposed(connectionScope), "the connection's close disposes its scope");
        closed.SetResult();

        (await post).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the connection closed before the restore: nothing is left to restore into");
    }

    [Fact]
    public async Task A_request_inside_a_connection_scope_keeps_it_until_it_leaves_and_none_enters_once_it_closes()
    {
        await using var services = new ServiceCollection().AddScoped<Tracked>().BuildServiceProvider();
        var scopes = new MentorHubScopes(services.GetRequiredService<IServiceScopeFactory>());
        var tracked = scopes.GetOrCreate("c1").GetRequiredService<Tracked>();

        var request = scopes.TryEnter("c1");
        request.Should().NotBeNull();
        var removing = scopes.RemoveAsync("c1").AsTask();

        removing.IsCompleted.Should().BeFalse("the close waits for the request inside the scope");
        tracked.Disposed.Should().BeFalse();
        request!.Services.GetRequiredService<Tracked>().Should().BeSameAs(tracked,
            "the request still resolves from the live scope, where it used to meet a disposed one");
        scopes.TryEnter("c1").Should().BeNull("once the close has begun no request enters the scope");
        scopes.TryGet("c1").Should().BeNull();

        request.Dispose();
        request.Dispose();   // a second dispose lets nothing else go
        await removing.WaitAsync(Patience);
        tracked.Disposed.Should().BeTrue("the last request out, the scope is disposed");
        scopes.TryEnter("c1").Should().BeNull();
    }

    [Fact]
    public async Task A_connection_with_no_request_inside_closes_at_once()
    {
        await using var services = new ServiceCollection().AddScoped<Tracked>().BuildServiceProvider();
        var scopes = new MentorHubScopes(services.GetRequiredService<IServiceScopeFactory>());
        var tracked = scopes.GetOrCreate("c1").GetRequiredService<Tracked>();
        scopes.TryEnter("c1")!.Dispose();

        await scopes.RemoveAsync("c1").AsTask().WaitAsync(Patience);

        tracked.Disposed.Should().BeTrue();
        scopes.TryEnter("unknown").Should().BeNull();
    }

    [Fact]
    public async Task A_request_that_never_leaves_holds_the_close_only_up_to_its_limit_and_says_so()
    {
        await using var services = new ServiceCollection().AddScoped<Tracked>().BuildServiceProvider();
        var log = new ListLogger();
        var scopes = new MentorHubScopes(services.GetRequiredService<IServiceScopeFactory>())
        {
            Logger = log,
            CloseWait = TimeSpan.FromMilliseconds(200),
        };
        var tracked = scopes.GetOrCreate("c1").GetRequiredService<Tracked>();
        _ = scopes.TryEnter("c1");   // a defect: never disposed

        await scopes.RemoveAsync("c1").AsTask().WaitAsync(Patience);

        tracked.Disposed.Should().BeTrue("past the limit the scope is disposed anyway");
        log.Warnings.Should().ContainSingle().Which.Should().Contain("closed connection's scope")
            .And.NotContain("c1", "the connection id is the key to its endpoints");
    }

    [Fact]
    public async Task A_request_that_found_the_connection_just_before_its_close_does_not_enter_once_the_close_has_begun()
    {
        // The registry finds the connection, and the close begins before the request enters it: the request must
        // not enter a scope the close no longer waits for.
        await using var services = new ServiceCollection().BuildServiceProvider();
        var connection = new MentorHubScopes.Connection(services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope());

        await connection.CloseAsync().WaitAsync(Patience);

        connection.TryEnter().Should().BeFalse("the close has begun, and found nobody inside to wait for");
    }

    private static bool IsDisposed(IServiceProvider scope)
    {
        try
        {
            scope.GetService<MentorServerUserContext>();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, because);
            await Task.Delay(20);
        }
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                lock (Warnings) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class Tracked : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>A JSON body whose first part is read before the rest is sent.</summary>
    private sealed class LateBody : HttpContent
    {
        private readonly TaskCompletionSource _reading;
        private readonly Task _rest;

        public LateBody(TaskCompletionSource reading, Task rest)
        {
            _reading = reading;
            _rest = rest;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[256 * 1024].Select(_ => (byte)' ').ToArray());
            await stream.FlushAsync();
            _reading.TrySetResult();
            await _rest;
            await stream.WriteAsync("{}"u8.ToArray());
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
