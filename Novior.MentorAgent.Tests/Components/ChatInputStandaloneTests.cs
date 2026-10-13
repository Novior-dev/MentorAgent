using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// <see cref="ChatInput"/> on a surface composed without <see cref="ChatWidget"/>.
///
/// <para>
/// Found by the rc.11 README audit. ChatWidget is what puts MentorAgent.css and MentorAgent.js on the
/// page; the Abstractions README shows how to compose your own chat surface from the parts, and a
/// surface built that way has neither. Every JS call in ChatInput tolerated that except the one
/// after a send: <c>MentorAgent.resetTextarea</c> threw a <see cref="JSException"/> on the first
/// message, and on Blazor Server an exception in an event handler ends the circuit.
/// </para>
/// </summary>
public sealed class ChatInputStandaloneTests : ComponentTestBase
{
    [Fact]
    public void Sending_without_MentorAgent_js_on_the_page_delivers_the_message_and_does_not_throw()
    {
        JSInterop.SetupVoid("MentorAgent.resetTextarea", _ => true)
                 .SetException(new JSException("Could not find 'MentorAgent.resetTextarea' ('MentorAgent' was undefined)."));

        MentorUserMessage? sent = null;
        var cut = Render<ChatInput>(p => p.Add(x => x.OnSend,
            EventCallback.Factory.Create<MentorUserMessage>(this, m => sent = m)));

        cut.Find("textarea").Input("quanti prodotti ci sono?");
        var send = () => cut.Find(".bm-send-btn").Click();

        send.Should().NotThrow("a missing cosmetic script must not end the circuit");
        sent.Should().NotBeNull();
        sent!.Text.Should().Be("quanti prodotti ci sono?");
    }
}
