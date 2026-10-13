using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The wire arity of every client → server hub method (BUG-051).
///
/// <para>
/// <c>MentorHub.SendMessage</c> is declared with a C# default —
/// <c>IReadOnlyList&lt;MentorAttachment&gt;? attachments = null</c> — and its doc-comment used to
/// claim that this let "existing 1-argument clients keep working". It does not, and cannot.
/// SignalR binds invocation arguments <b>positionally by count</b> against
/// <c>HubMethodDescriptor.OriginalParameterTypes</c>, inside the hub protocol's
/// <c>TryParseMessage</c> — before dispatch, before any <c>IHubFilter</c>, and before the method
/// body. A default value never reaches the wire, hub methods cannot be overloaded, and so no single
/// signature can accept both arities.
/// </para>
///
/// <para>
/// Every test in this project passed <c>null</c> explicitly, and <c>Novior.MentorAgent.Blazor</c>'s WASM
/// orchestrator always sends both arguments, which is why the whole suite was green while the four
/// reference clients in the server README — React, Angular, Vue and MAUI, all of them one-argument
/// — could not send a single message. This file pins the real protocol so a future signature change
/// has to break a test rather than four documentation snippets.
/// </para>
/// </summary>
public sealed class HubMethodArityTests
{
    // The exception SignalR raises, quoted so a search for it lands here:
    //   InvalidDataException: Invocation provides 1 argument(s) but target expects 2.
    // It is not asserted on: the wording belongs to the framework, and pinning someone else's
    // message would make this file fail on a harmless ASP.NET Core reword.

    [Fact]
    public async Task SendMessage_rejects_a_one_argument_invocation()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        // Exactly the call the server README's React, Angular, Vue and MAUI clients all made.
        var act = async () => await hub.InvokeAsync("SendMessage", "ciao");

        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("SendMessage",
                "the failure has to name the method, because with EnableDetailedErrors off — the " +
                "default a reader of the README will have — this message is all the client gets");
    }

    [Fact]
    public async Task SendMessage_accepts_a_null_second_argument()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        // The documented text-only form, once BUG-051 is taken into account.
        var act = async () => await hub.InvokeAsync("SendMessage", "ciao", null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendMessage_accepts_an_empty_attachment_list()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        // `[]` and `null` must behave identically: a client that always builds an array and only
        // sometimes fills it is the natural shape, and it must not need a special case.
        var act = async () => await hub.InvokeAsync(
            "SendMessage", "ciao", Array.Empty<MentorAttachment>());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_other_client_to_server_methods_take_the_arity_they_document()
    {
        await using var app = await ServerAppFactory.CreateAsync();
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        // Controls. If these had the same defect the finding would be about the hub as a whole
        // rather than about one method, and the documentation fix would be the wrong fix.
        var reset = async () => await hub.InvokeAsync("ResetSession");
        var page = async () => await hub.InvokeAsync("UpdatePageContext", new PageContextSnapshot
        {
            PageName = "Ordini",
        });

        await reset.Should().NotThrowAsync();
        await page.Should().NotThrowAsync();
    }
}
