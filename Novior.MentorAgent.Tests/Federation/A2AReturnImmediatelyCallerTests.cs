using System.Diagnostics;
using System.Security.Claims;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-226 — a caller told the task at once (<c>ReturnImmediately</c>) could lose who it is. The handler read the caller
/// from the HTTP request only after it had submitted the task, and the SDK answers such a caller as soon as the task is
/// submitted: the request ended while the turn ran on, and the turn read a request that was closing ("Collection was
/// modified") or gone. An authenticated peer became anonymous: its roles refused, its memory and rate limit under
/// another key. The caller is now read when the task starts, before the SDK can answer anything.
/// </summary>
public sealed class A2AReturnImmediatelyCallerTests
{
    /// <summary>
    /// The request's principal, read as by a turn whose thread is scheduled late: the read waits for the request to end
    /// (half a second at most, for a read the request cannot end without) and answers what the request says then. After
    /// the end that is nobody, because the accessor forgets a request once it has ended.
    /// </summary>
    private sealed class LateHttpUser(IHttpContextAccessor accessor) : IMentorHttpUser
    {
        public ClaimsPrincipal? User
        {
            get
            {
                var waited = Stopwatch.StartNew();
                while (accessor.HttpContext is not null && waited.ElapsedMilliseconds < 500) Thread.Sleep(5);
                return accessor.HttpContext?.User;
            }
        }
    }

    private static SendMessageRequest Request(string text) => new()
    {
        Message = new Message
        {
            Role      = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts     = [Part.FromText(text)],
        },
        Configuration = new SendMessageConfiguration { ReturnImmediately = true },
    };

    private static async Task<TaskState> EndedAsync(IA2AClient client, string taskId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var state = (await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, deadline.Token)).Status.State;
            if (state is TaskState.Completed or TaskState.Failed or TaskState.Canceled or TaskState.Rejected) return state;
            await Task.Delay(20, deadline.Token);
        }
    }

    [Fact]
    public async Task An_authenticated_caller_told_the_task_at_once_keeps_its_roles()
    {
        await using var app = await ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.A2AServerEnabled = true;
                o.A2AServerUrl = "http://localhost/a2a";
                o.MemoryAutoCapture = false;
            },
            userId: "peer-agent",
            roles: ["Admin"],
            // Before AddMentorAgentServer(), whose own reader is added only if there is none.
            configureServicesFirst: s => s.AddSingleton<IMentorHttpUser, LateHttpUser>(),
            configureServices: s => s.AddSingleton<TestOrderService>(),   // one instance, so the test reads what each task ran
            // Authentication before the endpoint, as in a host that protects /a2a.
            configureApp: a =>
            {
                a.UseAuthentication();
                a.UseEndpoints(e => e.MapMentorAgentA2A());
            });
        var orders = app.Services.GetRequiredService<TestOrderService>();
        var client = A2AClientFactory.Create(
            await new A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync(), app.Client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        for (var round = 1; round <= 3; round++)
        {
            app.Chat.CallTool("export_ledger").Say("Exported.");

            var task = (await client.SendMessageAsync(Request("export the order ledger"), deadline.Token)).Task;
            task.Should().NotBeNull("a caller told the task at once is answered with the task");
            var state = await EndedAsync(client, task!.Id);

            orders.Invoked.Should().HaveCount(round,
                "round {0}: the caller authenticated and holds Admin, and its request ended before its turn did. Results: {1}",
                round, string.Join(" | ", app.Chat.Calls
                    .SelectMany(c => c.Messages)
                    .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                    .Select(r => r.Result?.ToString())));
            state.Should().Be(TaskState.Completed);
        }

        app.Logs.ExceptionText.Should().BeEmpty();
    }
}
