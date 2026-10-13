using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Cards;

/// <summary>
/// <see cref="IMentorStructured"/> — a typed result from the model, outside a conversation.
///
/// <para>
/// This is the seam a host uses when it wants data rather than prose: extract an order from a
/// sentence, plan a tour, classify something. It is deliberately best-effort — every failure
/// returns <c>default</c> rather than throwing — which makes it the kind of API that can be broken
/// for a long time without anyone noticing, because "the model could not do it" and "we never asked
/// the model" produce the same <see langword="null"/>.
/// </para>
///
/// <para>
/// The Path A/B constraint (no <c>ChatClient</c> ⇒ default plus a warning) lives with the other
/// nineteen constraints; what is pinned here is the request it builds and every way it can fail.
/// </para>
/// </summary>
public sealed class StructuredOutputTests
{
    private sealed record ExtractedOrder(string Product, int Quantity);

    private static string Json(string product, int quantity)
        => $$"""{"product":"{{product}}","quantity":{{quantity}}}""";

    // ── The happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_typed_result_comes_back_deserialised()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say(Json("iPad", 2));

        var result = await host.Get<IMentorStructured>()
                               .GenerateAsync<ExtractedOrder>("Two iPads please");

        result.Should().NotBeNull();
        result!.Product.Should().Be("iPad");
        result.Quantity.Should().Be(2);
    }

    [Fact]
    public async Task The_prompt_is_sent_as_the_users_message()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say(Json("iPad", 2));

        await host.Get<IMentorStructured>().GenerateAsync<ExtractedOrder>("Two iPads please");

        host.Chat.LastCall!.LastUserText.Should().Be("Two iPads please");
    }

    [Fact]
    public async Task Instructions_are_sent_as_a_system_message()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say(Json("iPad", 2));

        await host.Get<IMentorStructured>()
                  .GenerateAsync<ExtractedOrder>("Two iPads please", "Extract the order.");

        host.Chat.LastCall!.SystemText.Should().Contain("Extract the order.");
    }

    [Fact]
    public async Task Without_instructions_no_system_message_is_invented()
    {
        // An empty system message costs tokens on every call and can shift a model's behaviour for
        // no reason at all.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say(Json("iPad", 2));

        await host.Get<IMentorStructured>().GenerateAsync<ExtractedOrder>("Two iPads please");

        host.Chat.LastCall!.Messages.Should().OnlyContain(m => m.Role != ChatRole.System);
    }

    [Fact]
    public async Task Blank_instructions_are_treated_as_none()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say(Json("iPad", 2));

        await host.Get<IMentorStructured>()
                  .GenerateAsync<ExtractedOrder>("Two iPads please", "   ");

        host.Chat.LastCall!.Messages.Should().OnlyContain(m => m.Role != ChatRole.System);
    }

    [Fact]
    public async Task The_call_is_a_single_round_trip()
    {
        // No tools, no history, no middleware — this is a one-shot extraction, and anything else
        // would make it cost more than the conversation it is meant to stay out of.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say(Json("iPad", 2));

        await host.Get<IMentorStructured>().GenerateAsync<ExtractedOrder>("Two iPads please");

        host.Chat.CallCount.Should().Be(1);
        host.Chat.LastCall!.ToolNames.Should().BeEmpty("a structured extraction offers no tools");
    }

    // ── Every way it can fail ───────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_that_is_not_json_returns_default_rather_than_throwing()
    {
        // The caller is usually rendering the result. A parse exception thrown out of here would
        // surface as a crash in a page, for an answer that was only ever advisory.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Certainly! You want two iPads.");

        var act = async () => await host.Get<IMentorStructured>()
                                        .GenerateAsync<ExtractedOrder>("Two iPads please");

        (await act.Should().NotThrowAsync()).Which.Should().BeNull();
    }

    [Fact]
    public async Task A_provider_failure_returns_default_and_is_reported()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.ThrowOnNextCall = new HttpRequestException("429 Too Many Requests");

        var result = await host.Get<IMentorStructured>()
                               .GenerateAsync<ExtractedOrder>("Two iPads please");

        result.Should().BeNull();
        host.Logs.HasWarning("Structured output generation failed").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task An_empty_reply_returns_default()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("");

        (await host.Get<IMentorStructured>().GenerateAsync<ExtractedOrder>("anything"))
            .Should().BeNull();
    }

    [Fact]
    public async Task Json_of_the_wrong_shape_returns_default()
    {
        // Valid JSON, wrong object. Deserialising it into a half-populated record would be worse
        // than nothing: the caller cannot tell a real extraction from a guess.
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("""[1, 2, 3]""");

        (await host.Get<IMentorStructured>().GenerateAsync<ExtractedOrder>("anything"))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_extraction_returns_default_rather_than_throwing()
    {
        // Pressing Stop while the tour planner or an extraction is in flight. Every other failure
        // here degrades to default, and cancellation taking the caller down instead would make the
        // one method that never throws throw on the most ordinary interruption there is.
        await using var host = MentorHostFixture.Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await host.Get<IMentorStructured>()
                                        .GenerateAsync<ExtractedOrder>("anything", ct: cts.Token);

        (await act.Should().NotThrowAsync()).Which.Should().BeNull();
    }

    [Fact]
    public async Task The_token_reaches_the_provider()
    {
        // Best-effort must not mean fire-and-forget: an extraction that ignored the token would
        // keep a paid call running after the user has already moved on.
        await using var host = MentorHostFixture.Create();
        using var cts = new CancellationTokenSource();
        host.Chat!.BeforeRespond = async () => { await cts.CancelAsync(); };

        var result = await host.Get<IMentorStructured>()
                               .GenerateAsync<ExtractedOrder>("anything", ct: cts.Token);

        result.Should().BeNull("the call was cancelled while in flight");
    }
}
