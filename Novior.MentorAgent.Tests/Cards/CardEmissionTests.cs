using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Cards;

/// <summary>
/// Generative UI Level 2 — a tool returns a <see cref="MentorCard"/> and the widget renders it,
/// instead of the model describing it in prose.
///
/// <para>
/// The interesting half is not the rendering, it is the <b>substitution</b>. Once the card is on
/// screen the model must not be handed the card back: it would pay for every label and every button
/// twice — once to render, once to read — and would then dutifully repeat the whole thing in prose
/// underneath, which is the exact duplication cards exist to remove. What it gets instead is the
/// data plus one line saying the card is already displayed.
/// </para>
///
/// <para>
/// The second thing worth pinning is ordering: <c>OnToolResult</c> runs <em>before</em> the card is
/// extracted, on purpose, so a redaction hook can strip a field before it reaches the screen and not
/// merely before it reaches the model. That is a security property, and it is invisible from either
/// end on its own.
/// </para>
/// </summary>
public sealed class CardEmissionTests
{
    /// <summary>Runs a turn in which the model calls the fixture's card-returning tool.</summary>
    private static async Task<MentorHostFixture> CardTurnAsync(
        Action<Novior.MentorAgent.Options.MentorOptions>? configure = null,
        string reply = "Here is the order.")
    {
        var host = MentorHostFixture.Create(configure, withRecorder: true);
        host.Chat!.CallTool("show_order_card", new { orderId = 7 }).Say(reply);

        await host.RunTurnAsync("show me order 7");
        return host;
    }

    /// <summary>The cards the widget was told to render on the last notification.</summary>
    private static IReadOnlyList<MentorCard> CardsShown(MentorHostFixture host)
        => host.Recorder!.Log
            .Where(e => e.Event == nameof(RecordingStateService.NotifyCardsReady))
            .Select(e => (IReadOnlyList<MentorCard>)e.Args[0]!)
            .LastOrDefault() ?? [];

    /// <summary>Everything the tools sent back into the conversation, concatenated.</summary>
    private static string ToolResults(MentorHostFixture host)
        => string.Join("\n", host.Chat!.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Select(r => r.Result?.ToString() ?? ""));

    // ── The card reaches the screen ─────────────────────────────────────────────

    [Fact]
    public async Task A_tool_that_returns_a_card_gets_it_rendered()
    {
        await using var host = await CardTurnAsync();

        var cards = CardsShown(host);
        cards.Should().ContainSingle();
        cards[0].Title.Should().Be("Order #7");
        cards[0].Kind.Should().Be("order");
    }

    [Fact]
    public async Task The_card_keeps_the_fields_and_buttons_the_tool_built()
    {
        // The whole safety argument for cards: the structure comes from the host's code, so nothing
        // the conversation says can add a button or change where one points.
        await using var host = await CardTurnAsync();

        var card = CardsShown(host).Single();
        card.Fields.Should().HaveCount(2);
        card.Actions.Should().ContainSingle();
        card.Actions![0].Kind.Should().Be(MentorCardActionKind.Navigate);
        card.Actions[0].Value.Should().Be("/orders/7");
    }

    [Fact]
    public async Task The_card_is_rendered_before_the_reply_that_comments_on_it()
    {
        // Order matters on screen: the model's sentence refers to a card the user must already be
        // looking at, so a card that arrives after the prose reads as a non-sequitur.
        await using var host = await CardTurnAsync();

        var sequence = host.Recorder!.Sequence;
        var cardAt = sequence.ToList().IndexOf(nameof(RecordingStateService.NotifyCardsReady));
        var lastChunkAt = sequence.ToList().FindLastIndex(
            e => e == nameof(RecordingStateService.NotifyStreamingChunk));

        cardAt.Should().BeGreaterThanOrEqualTo(0, "the card must have been sent at all");
        cardAt.Should().BeLessThan(lastChunkAt, "the card comes before the sentence about it");
    }

    // ── What the model is given instead ─────────────────────────────────────────

    [Fact]
    public async Task The_model_is_told_the_card_is_already_on_screen()
    {
        await using var host = await CardTurnAsync();

        ToolResults(host).Should().Contain("ALREADY DISPLAYED");
    }

    [Fact]
    public async Task The_model_is_asked_not_to_repeat_the_card()
    {
        // Without this instruction the model narrates every field under the card it can see, which
        // is the duplication the feature exists to remove.
        await using var host = await CardTurnAsync();

        var results = ToolResults(host);
        results.Should().Contain("do NOT repeat every field");
        results.Should().Contain("do not describe the buttons");
    }

    [Fact]
    public async Task The_model_still_receives_the_data_so_it_can_comment_on_it()
    {
        // Substitution, not suppression. A model given nothing would have to answer "here you are"
        // and could not follow up on what the card says.
        await using var host = await CardTurnAsync();

        var results = ToolResults(host);
        results.Should().Contain("Order #7").And.Contain("Mario Rossi");
        results.Should().Contain("Total: €2,599.98").And.Contain("Status: Delivered");
    }

    [Fact]
    public async Task The_model_is_not_sent_the_buttons()
    {
        // Buttons are the part it must not describe and the part it can never influence, so paying
        // tokens to show them to it buys nothing at all.
        await using var host = await CardTurnAsync();

        var results = ToolResults(host);
        results.Should().NotContain("/orders/7", "the button target is not the model's business");
        results.Should().NotContain("Navigate");
    }

    [Fact]
    public async Task The_model_is_not_sent_the_raw_card_json()
    {
        await using var host = await CardTurnAsync();

        var results = ToolResults(host);
        results.Should().NotContain("\"kind\"").And.NotContain("accent");
    }

    [Fact]
    public async Task The_notice_counts_the_cards()
    {
        await using var host = await CardTurnAsync();

        ToolResults(host).Should().Contain("1 card(s)");
    }

    // ── Switched off ────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_cards_off_nothing_is_rendered()
    {
        await using var host = await CardTurnAsync(o => o.EnableGenerativeCards = false);

        host.Recorder!.Count(nameof(RecordingStateService.NotifyCardsReady)).Should().Be(0);
    }

    [Fact]
    public async Task With_cards_off_the_model_gets_the_data_and_keeps_answering()
    {
        // The documented fallback: "the cards are still returned to the model, so the assistant
        // keeps answering, it just describes the data instead of showing it". A silent nothing here
        // would turn an opt-out into a broken tool.
        await using var host = await CardTurnAsync(o => o.EnableGenerativeCards = false);

        var results = ToolResults(host);
        results.Should().Contain("Order #7");
        results.Should().NotContain("ALREADY DISPLAYED");
    }

    // ── Not everything is a card ────────────────────────────────────────────────

    [Fact]
    public async Task An_ordinary_tool_result_is_passed_through_untouched()
    {
        // Detection is structural, and a false positive would swallow a real answer and render an
        // empty card in its place. count_open_orders returns a bare number.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.CallTool("count_open_orders").Say("There are two.");

        await host.RunTurnAsync("how many open orders");

        host.Recorder!.Count(nameof(RecordingStateService.NotifyCardsReady)).Should().Be(0);
        ToolResults(host).Should().Contain("2");
    }

    [Fact]
    public async Task A_hosts_own_record_with_a_kind_property_is_drawn_as_a_card()
    {
        // The other side of structural detection, pinned as it stands: a card is recognised by its shape, not
        // by its .NET type, so an application record that happens to carry a non-empty string `kind` is one.
        // Its card properties reach the screen, URL included, and every other field is lost to the model,
        // which is told a card is displayed instead. Kept for 1.0.0, documented in the README with the way
        // out (rename the property, or return the records inside an object without a `kind`); the result
        // arrives as JSON, as AIFunctionFactory marshals a tool's return value.
        await using var host = MentorHostFixture.Create(
            o => o.OnToolResult = (_, _) => System.Text.Json.JsonSerializer.SerializeToElement(
                new { kind = "invoice", title = "Invoice 42", imageUrl = "https://files.example/invoice-42.png", amount = 120.5 },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            withRecorder: true);
        host.Chat!.CallTool("count_open_orders").Say("ok");

        await host.RunTurnAsync("show invoice 42");

        var card = CardsShown(host).Should().ContainSingle().Which;
        card.Kind.Should().Be("invoice");
        card.ImageUrl.Should().Be("https://files.example/invoice-42.png", "the record's URL is drawn on the card");
        var results = ToolResults(host);
        results.Should().Contain("ALREADY DISPLAYED");
        results.Should().NotContain("120.5", "a field that is not a card property never reaches the model");
    }

    // ── Ordering against the redaction hook ─────────────────────────────────────

    [Fact]
    public async Task A_result_hook_runs_before_the_card_is_built()
    {
        // Documented and deliberate: "Runs AFTER OnToolResult on purpose: a redaction hook must be
        // able to strip a field before it reaches the screen, not just before it reaches the
        // model." If the order were reversed, a hook that removed a customer name would still have
        // let the widget render it.
        // The hook hands back a real MentorCard. That is the documented direct-type path — "a
        // custom middleware or an OnToolResult hook can hand us real objects" — and it is what a
        // host writes; re-serialising to JSON by hand would be doing the framework's job badly.
        await using var host = await CardTurnAsync(o => o.OnToolResult = (tool, result) =>
            tool == "show_order_card"
                ? new MentorCard("order")
                {
                    Title = "Order #7",
                    Subtitle = "[redacted]",
                    Fields = [new("Total", "€2,599.98")],
                }
                : result);

        var card = CardsShown(host).Single();
        card.Subtitle.Should().Be("[redacted]",
            "the hook's version is the one that reaches the screen");
        ToolResults(host).Should().NotContain("Mario Rossi");
    }

    [Fact]
    public async Task A_result_hook_can_stop_something_being_a_card_at_all()
    {
        // The other direction of the same ordering. A hook that replaces the payload with prose
        // must produce prose, not a card built from what the tool originally returned.
        await using var host = await CardTurnAsync(o => o.OnToolResult = (tool, result) =>
            tool == "show_order_card" ? "the order exists" : result);

        host.Recorder!.Count(nameof(RecordingStateService.NotifyCardsReady)).Should().Be(0);
        ToolResults(host).Should().Contain("the order exists");
    }

    // ── Shapes the reader has to get right ──────────────────────────────────────

    [Fact]
    public async Task A_card_with_no_title_is_announced_by_its_kind()
    {
        // The compact notice is one line per card. A card whose title is null would open with an
        // empty bullet, which tells the model nothing about what is on screen.
        await using var host = MentorHostFixture.Create(
            o => o.OnToolResult = (_, _) => new MentorCard("shipment"),
            withRecorder: true);
        host.Chat!.CallTool("count_open_orders").Say("ok");

        await host.RunTurnAsync("anything");

        CardsShown(host).Should().ContainSingle().Which.Kind.Should().Be("shipment");
        ToolResults(host).Should().Contain("- shipment");
    }

    [Fact]
    public async Task Several_cards_from_one_call_are_all_rendered_and_counted()
    {
        await using var host = MentorHostFixture.Create(
            o => o.OnToolResult = (_, _) => new List<MentorCard>
            {
                new("order") { Title = "Order #1" },
                new("order") { Title = "Order #2" },
            },
            withRecorder: true);
        host.Chat!.CallTool("count_open_orders").Say("ok");

        await host.RunTurnAsync("show my orders");

        CardsShown(host).Should().HaveCount(2);
        ToolResults(host).Should().Contain("2 card(s)").And.Contain("Order #1").And.Contain("Order #2");
    }

    [Fact]
    public async Task A_card_that_arrives_as_camelCase_json_is_recognised()
    {
        // The shape a tool result really has: AIFunctionFactory marshals return values through
        // JSON with web (camelCase) naming. Detection reads the `kind` property by that exact
        // name, so this is the contract between the two — and the reason a method declared as
        // returning MentorCard works at all.
        await using var host = MentorHostFixture.Create(
            o => o.OnToolResult = (_, _) => System.Text.Json.JsonSerializer.SerializeToElement(
                new MentorCard("order") { Title = "Order #9" },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            withRecorder: true);
        host.Chat!.CallTool("count_open_orders").Say("ok");

        await host.RunTurnAsync("show order 9");

        CardsShown(host).Should().ContainSingle().Which.Title.Should().Be("Order #9");
    }
}
