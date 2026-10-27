using System.Text.RegularExpressions;
using A2A;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-199 — what makes a cancelled task's call read <c>stop</c>: MentorAgent's request handler in front of the A2A
/// SDK's, which cancels the task's turn before it tells anyone why. A preview SDK can change how it registers its
/// handler, so the wrapping is checked, and the host is warned when it cannot be done.
/// </summary>
public sealed class A2ARequestHandlerTests
{
    private static Task<ServerAppFactory> HostAsync(Action<IServiceCollection>? services = null)
        => ServerAppFactory.CreateAsync(
            configure: o =>
            {
                o.A2AServerEnabled = true;
                o.A2AServerUrl = "http://localhost/a2a";
                o.MemoryAutoCapture = false;
            },
            configureServices: services,
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

    [Fact]
    public async Task After_AddMentorAgentA2A_the_SDKs_request_handler_is_behind_MentorAgents()
    {
        await using var app = await HostAsync();

        app.Services.GetRequiredService<IA2ARequestHandler>().Should().BeOfType<MentorA2ARequestHandler>();
        app.Services.GetRequiredService<IAgentHandler>().Should().BeOfType<MentorAgentA2AHandler>();
        app.Logs.HasWarning("could not be wrapped").Should().BeFalse();
    }

    [Fact]
    public async Task A_request_handler_that_cannot_be_wrapped_is_a_warning_when_the_endpoint_is_mapped()
    {
        // What a version of the SDK that registers its handler some other way leaves: a handler that is not MentorAgent's.
        await using var app = await HostAsync(s => s.AddSingleton(Substitute.For<IA2ARequestHandler>()));

        app.Logs.HasWarning("The A2A SDK's request handler could not be wrapped").Should().BeTrue(app.Logs.WarningText);
    }

    [Fact]
    public async Task The_wrapper_passes_on_every_request_and_the_disposal()
    {
        var sdk = Substitute.For<IA2ARequestHandler, IAsyncDisposable>();
        var agent = new MentorAgentA2AHandler(Substitute.For<IServiceScopeFactory>(), NullLogger<MentorAgentA2AHandler>.Instance);
        var wrapper = new MentorA2ARequestHandler(sdk, agent);

        var methods = typeof(IA2ARequestHandler).GetMethods();
        methods.Should().NotBeEmpty();
        foreach (var method in methods)
        {
            var args = method.GetParameters()
                .Select(p => p.ParameterType == typeof(CancellationToken) ? CancellationToken.None : Activator.CreateInstance(p.ParameterType))
                .ToArray();
            if (args.FirstOrDefault() is CancelTaskRequest cancel) cancel.Id = "task-1";
            method.Invoke(wrapper, args);
        }
        await wrapper.DisposeAsync();

        sdk.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Should().Contain(methods.Select(m => m.Name),
            "every request goes on to the SDK");
        await ((IAsyncDisposable)sdk).Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Every_task_leaves_the_running_turns_when_it_ends_completed_failed_or_cancelled()
    {
        await using var app = await HostAsync();
        var handler = (MentorAgentA2AHandler)app.Services.GetRequiredService<IAgentHandler>();
        var client = new A2AClient(new Uri("http://localhost/a2a"), app.Client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SendMessageRequest Request(string text) => new()
        {
            Message = new Message { Role = Role.User, MessageId = Guid.NewGuid().ToString("N"), Parts = [Part.FromText(text)] },
        };

        // Completed.
        app.Chat.CallTool("count_open_orders").Say("Two open orders.");
        (await client.SendMessageAsync(Request("how many open orders?"), deadline.Token)).Task!.Status.State
            .Should().Be(TaskState.Completed);

        // Failed: the model's provider throws, and the turn writes nothing.
        app.Chat.ThrowOnNextCall = new InvalidOperationException("HTTP 500");
        (await client.SendMessageAsync(Request("and the delivered ones?"), deadline.Token)).Task!.Status.State
            .Should().Be(TaskState.Failed);

        // Cancelled, while its model thinks.
        var model = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Chat.BeforeRespond = () => model.Task;
        var calls = app.Chat.CallCount;
        var sent = client.SendMessageAsync(Request("how many open orders now?"), deadline.Token);
        string? taskId = null;
        while (taskId is null || app.Chat.CallCount == calls)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(20);
            taskId = app.Logs.Records.Select(r => Regex.Match(r.Message, @"Task '([0-9a-f]+)' received")).LastOrDefault(m => m.Success)?.Groups[1].Value;
        }
        var whileItRuns = handler.RunningTasks;   // asserted below: the model has to be released first, or the host never stops
        await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, deadline.Token);
        model.SetResult();
        await sent;
        whileItRuns.Should().Be(1, "the turn is known while it runs, and the two that ended are gone");

        while (handler.RunningTasks > 0)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(20);
        }
        handler.RunningTasks.Should().Be(0, "no task leaves its turn behind");
    }
}
