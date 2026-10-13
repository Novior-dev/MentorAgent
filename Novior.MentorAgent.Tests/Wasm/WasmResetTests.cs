using FluentAssertions;
using Novior.MentorAgent.Blazor.Hub;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Wasm;

/// <summary>
/// BUG-109 — "New conversation" on WebAssembly before the first message.
///
/// <para>
/// The hub connects lazily, on the first message. The reset invoked it anyway, the SignalR client threw
/// "The 'InvokeCoreAsync' method cannot be called if the connection is not active", and the exception
/// reached the Blazor error bar. With no live connection there is no server conversation: every
/// connection gets its own scope, so the next message starts a fresh one without being asked to.
/// </para>
/// </summary>
public sealed class WasmResetTests
{
    private static WasmMentorOrchestrator Orchestrator(MentorAgentBlazorOptions options)
        => new(new MentorHubClient(options, NullLogger<MentorHubClient>.Instance),
               new WasmMentorPageContext(), NullLogger<WasmMentorOrchestrator>.Instance,
               new HttpClient(), options);

    [Fact]
    public async Task A_reset_before_the_first_message_does_not_touch_the_hub()
    {
        // An address nothing answers on: the reset must neither throw nor try to connect, because
        // connecting only to reset would build a coordinator for an empty conversation.
        var orchestrator = Orchestrator(new MentorAgentBlazorOptions { HubUrl = "http://localhost:59998/mentor-hub" });

        var act = () => orchestrator.ResetSessionAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_reset_on_a_live_connection_starts_a_new_conversation()
    {
        // Control: the early return is for a connection that does not exist, not a way to skip the
        // reset on one that does.
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.SayRepeatedly("ok", 4);
        var options = new MentorAgentBlazorOptions
        {
            HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
            ConfigureConnection = o => o.HttpMessageHandlerFactory = _ => app.CreateHandler(),
        };
        await using var orchestrator = Orchestrator(options);

        await orchestrator.SendMessageAsync("il mio codice è ZEPHYR-4417");
        await WaitForCallsAsync(app, 1);
        await orchestrator.ResetSessionAsync();
        await orchestrator.SendMessageAsync("qual è il mio codice?");
        await WaitForCallsAsync(app, 2);

        app.Chat.Calls[^1].Messages.Select(m => m.Text).Should().NotContain(t => t.Contains("ZEPHYR-4417"),
            "the reset reached the server: the second turn starts a new conversation");
    }

    private static async Task WaitForCallsAsync(ServerAppFactory app, int count)
    {
        // SendMessage returns when the hub accepted the invocation; the turn itself runs server-side.
        for (var i = 0; i < 100 && app.Chat.Calls.Count < count; i++)
            await Task.Delay(50);
        app.Chat.Calls.Count.Should().BeGreaterThanOrEqualTo(count);
    }
}
