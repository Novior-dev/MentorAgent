using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// Tests for generative UI Level 2 (roadmap #14) — a tool that returns
/// <see cref="MentorCard"/>s instead of prose.
/// </summary>
public sealed class GenerativeCardTests
{
    private sealed class CardTools
    {
        public IEnumerable<MentorCard> ShowOrders() =>
        [
            new MentorCard("order")
            {
                Title = "Ordine #1001",
                Subtitle = "Mario Rossi",
                Accent = MentorCardAccent.Success,
                Fields = [new("Stato", "Delivered"), new("Totale", "2.599,98 €")],
                Actions = [new("Apri", MentorCardActionKind.Navigate, "/data/orders")],
            },
        ];

        public MentorCard ShowOne() => new("product") { Title = "MacBook Pro" };

        public string ShowText() => "plain";
    }

    /// <summary>
    /// The behaviour the card feature has to be built around, and the reason the first
    /// implementation silently did nothing.
    /// <para>
    /// <c>AIFunctionFactory</c> marshals a tool's return value through JSON, so by the time any
    /// middleware sees it the object is a <see cref="JsonElement"/> — not the
    /// <see cref="MentorCard"/> the method returned. A type check against <c>MentorCard</c> can
    /// therefore never match, and the cards fall through to the model as raw JSON: the assistant
    /// then dutifully describes them in a Markdown table, which is exactly what cards exist to
    /// replace. It looks like nothing is wrong — the answer is correct, just not a card.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ToolResult_ReachesMiddlewareAsJson_NotAsTheReturnedType()
    {
        var fn = AIFunctionFactory.Create(
            typeof(CardTools).GetMethod(nameof(CardTools.ShowOrders))!,
            new CardTools(),
            name: "show_orders",
            description: "cards");

        var result = await fn.InvokeAsync(new AIFunctionArguments());

        result.Should().BeOfType<JsonElement>(
            "AIFunctionFactory serialises tool results — anything reading them must decode JSON");
    }

    [Fact]
    public async Task JsonResult_RoundTripsBackIntoCards()
    {
        var fn = AIFunctionFactory.Create(
            typeof(CardTools).GetMethod(nameof(CardTools.ShowOrders))!,
            new CardTools(), name: "show_orders", description: "cards");

        var json = (JsonElement)(await fn.InvokeAsync(new AIFunctionArguments()))!;
        var cards = Novior.MentorAgent.Middleware.MentorCardReader.TryRead(json);

        cards.Should().NotBeNull();
        cards!.Should().ContainSingle();
        cards[0].Kind.Should().Be("order");
        cards[0].Title.Should().Be("Ordine #1001");
        cards[0].Accent.Should().Be(MentorCardAccent.Success);
        cards[0].Fields.Should().HaveCount(2);
        cards[0].Actions![0].Kind.Should().Be(MentorCardActionKind.Navigate);
    }

    [Fact]
    public async Task ASingleCard_IsReadAsAOneItemList()
    {
        var fn = AIFunctionFactory.Create(
            typeof(CardTools).GetMethod(nameof(CardTools.ShowOne))!,
            new CardTools(), name: "show_one", description: "card");

        var json = (JsonElement)(await fn.InvokeAsync(new AIFunctionArguments()))!;
        var cards = Novior.MentorAgent.Middleware.MentorCardReader.TryRead(json);

        cards.Should().NotBeNull();
        cards!.Should().ContainSingle();
        cards[0].Title.Should().Be("MacBook Pro");
    }

    /// <summary>
    /// The important negative: every ordinary tool result also arrives as a <see cref="JsonElement"/>,
    /// so the reader has to reject them. A false positive here would swallow a normal answer and
    /// replace it with an empty card.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryToolResult_IsNotMistakenForACard()
    {
        var fn = AIFunctionFactory.Create(
            typeof(CardTools).GetMethod(nameof(CardTools.ShowText))!,
            new CardTools(), name: "show_text", description: "text");

        var json = (JsonElement)(await fn.InvokeAsync(new AIFunctionArguments()))!;

        Novior.MentorAgent.Middleware.MentorCardReader.TryRead(json).Should().BeNull();
    }

    [Theory]
    // Shapes a business tool really returns — none of them is a card.
    [InlineData("""{ "id": 1, "name": "MacBook" }""")]
    [InlineData("""[ { "id": 1 }, { "id": 2 } ]""")]
    [InlineData("""[ "a", "b" ]""")]
    [InlineData("""{ "kind": 3 }""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void NonCardJson_IsRejected(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Novior.MentorAgent.Middleware.MentorCardReader.TryRead(doc.RootElement).Should().BeNull();
    }

    [Fact]
    public void JsonThatOnlyLooksLikeACard_IsAccepted()
    {
        // A tool is free to return this shape by hand rather than via MentorCard.
        using var doc = JsonDocument.Parse("""[ { "kind": "order", "title": "X" } ]""");
        var cards = Novior.MentorAgent.Middleware.MentorCardReader.TryRead(doc.RootElement);

        cards.Should().NotBeNull();
        cards!.Should().ContainSingle();
        cards[0].Kind.Should().Be("order");
    }
}
