using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// A UI action registered by a hub client reaches the model with its parameter hint.
///
/// <para>
/// Found by the rc.11 README audit. The model learns a UI action's parameter only from the text
/// appended to its description — "(parameter: integer)". The hub rebuilt every client action with
/// the three-argument <c>RegisterUIAction</c> overload, which has no hint, so a WebAssembly, React or
/// MAUI client's hints never arrived; and the WebAssembly page context produced only the bare type
/// name ("Int32", "List`1") where Blazor Server produced "integer" or "{ id: integer, … }".
/// </para>
/// </summary>
public sealed class HubUIActionHintTests
{
    private sealed class TurnRecorder
    {
        private readonly TaskCompletionSource _completed = new();

        internal void Attach(HubConnection hub)
        {
            hub.On("StreamingCompleted", () => _completed.TrySetResult());
            hub.On<string>("Error", _ => _completed.TrySetResult());
        }

        internal Task WaitAsync() => _completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_client_actions_parameter_hint_reaches_the_tool_the_model_sees()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        app.Chat.Say("ok");

        var hub = app.CreateHubConnection();
        var turn = new TurnRecorder();
        turn.Attach(hub);
        await hub.StartAsync();
        await using var _ = hub;

        await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot
        {
            PageName = "Orders",
            UIActions = [new UIActionInfo { Name = "select_order", Description = "Selects an order in the grid", ParameterHint = "integer: order id" }],
        });
        await hub.InvokeAsync("SendMessage", "seleziona l'ordine 1002", null);
        await turn.WaitAsync();

        var tool = app.Chat.Calls.Select(c => c.Tool("select_order")).FirstOrDefault(t => t is not null);
        tool.Should().NotBeNull("the page's UI action is offered to the model");
        tool!.Description.Should().Contain("(parameter: integer: order id)",
            "the hint is the only place the model reads the parameter's shape from");
    }

    [Theory]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(List<string>), "array<string>")]
    public void The_webassembly_page_context_generates_the_same_hints_as_blazor_server(Type type, string expected)
    {
        var page = new WasmMentorPageContext();
        typeof(WasmMentorPageContext).GetMethods()
            .Single(m => m.Name == nameof(WasmMentorPageContext.RegisterUIAction) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(type)
            .Invoke(page, ["act", "does something", CreateHandler(type), null]);

        page.BuildSnapshot().UIActions.Single().ParameterHint.Should().Be(expected,
            "not the bare type name the WebAssembly context used to send");
    }

    private static Delegate CreateHandler(Type type)
        => (Delegate)typeof(HubUIActionHintTests).GetMethod(nameof(Noop), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, null)!;

    private static Action<T> Noop<T>() => _ => { };
}
