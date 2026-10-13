using FluentAssertions;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Discovery;

/// <summary>
/// <c>[MentorAction(ProactiveHint = …)]</c> reaches the model.
///
/// <para>
/// Found by the rc.11 README audit: discovery stored the hint and nothing ever read it, while the
/// attribute's documentation ("Tells the AI what to suggest to the user before/after this action")
/// and both READMEs ("Hint injected into the AI prompt") said it guided the assistant. It now travels
/// in the tool's description — where the model reads it at the moment it is choosing that tool.
/// </para>
/// </summary>
public sealed class ProactiveHintTests
{
    [Fact]
    public async Task The_hint_is_in_the_description_of_the_tool_the_model_sees()
    {
        // The fixture's cancel_order sets ProactiveHint = "Suggest checking active orders first".
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("annulla l'ordine 7");

        var tool = host.Chat.Calls.Select(c => c.Tool("cancel_order")).FirstOrDefault(t => t is not null);
        tool.Should().NotBeNull();
        tool!.Description.Should().Be("Cancels an existing order Guidance: Suggest checking active orders first");
    }

    [Fact]
    public async Task A_tool_without_a_hint_keeps_its_description_as_it_was()
    {
        await using var host = MentorHostFixture.Create(o => o.MemoryAutoCapture = false);
        host.Chat!.Say("ok");

        await host.RunTurnAsync("che tempo fa?");

        var tool = host.Chat.Calls.Select(c => c.Tool("get_weather")).FirstOrDefault(t => t is not null);
        tool.Should().NotBeNull();
        tool!.Description.Should().NotContain("Guidance:");
    }
}
