using FluentAssertions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Middleware;

/// <summary>
/// One turn is often several model responses: some text, a tool call, then more text once the result
/// is back. The widget renders them into a single bubble — and the stream carries no boundary between
/// them, so the join is the library's to make (BUG-062).
///
/// <para>
/// Found live on rc.4: <i>"Procedo ora a recuperare i dati.Il catalogo attuale…"</i>, on every turn
/// whose model wrote a sentence before acting — which the prompt <em>requires</em> before any
/// confirmed action, at every mentorship level. The visible damage is a missing space; the real
/// damage is Markdown: a second response that opens with a table or a heading no longer starts a
/// line, so the renderer shows the pipes and the hashes instead of the table and the heading.
/// </para>
/// </summary>
public sealed class StreamedSegmentBoundaryTests
{
    /// <summary>A response that writes a sentence and then asks for an ungated tool.</summary>
    private static ChatResponse TextThenTool(string text)
        => new(new ChatMessage(ChatRole.Assistant,
            [new TextContent(text), new FunctionCallContent("call-1", "count_open_orders", new Dictionary<string, object?>())]))
        {
            ModelId = "fake-model",
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
        };

    private static async Task<string> StreamedAsync(string before, string after,
        Action<Novior.MentorAgent.Options.MentorOptions>? configure = null)
    {
        await using var host = MentorHostFixture.Create(configure, withRecorder: true);
        host.Chat!.Respond((_, _) => TextThenTool(before)).Say(after);

        var error = await host.RunTurnAsync("quanti ordini aperti?");
        error.Should().BeNull();

        return string.Concat(host.Recorder!.Log
            .Where(e => e.Event == nameof(RecordingStateService.NotifyStreamingChunk))
            .Select(e => e.Args[0] as string));
    }

    [Fact]
    public async Task Text_after_a_tool_call_starts_a_new_paragraph()
    {
        var streamed = await StreamedAsync("Recupero i dati.", "Il catalogo contiene 8 prodotti.");

        streamed.Should().NotContain("dati.Il", "two responses glued into one word is the defect");
        streamed.Should().Contain("Recupero i dati.\n\nIl catalogo contiene 8 prodotti.");
    }

    [Fact]
    public async Task A_table_that_opens_the_second_response_still_starts_a_line()
    {
        // The Markdown case — the reason this is not cosmetic. A pipe row that does not start a line
        // is not a table row.
        var streamed = await StreamedAsync("Controllo il catalogo.", "| Nome | Prezzo |\n|---|---|\n| iPad Air | 749,99 |");

        streamed.Should().Contain("catalogo.\n\n| Nome | Prezzo |");
    }

    [Fact]
    public async Task A_first_response_that_already_ends_a_line_is_not_padded_further()
    {
        // No stacking: the boundary is only supplied when the text does not already provide one.
        var streamed = await StreamedAsync("Recupero i dati.\n", "Il catalogo contiene 8 prodotti.");

        streamed.Should().Contain("Recupero i dati.\nIl catalogo contiene 8 prodotti.");
    }

    [Fact]
    public async Task A_turn_with_a_single_response_is_streamed_exactly_as_written()
    {
        // The control. Only a boundary between responses is marked; nothing is inserted inside one.
        await using var host = MentorHostFixture.Create(withRecorder: true);
        host.Chat!.Say("Una risposta sola, senza strumenti. Seconda frase.");

        await host.RunTurnAsync("ciao");

        string.Concat(host.Recorder!.Log
                .Where(e => e.Event == nameof(RecordingStateService.NotifyStreamingChunk))
                .Select(e => e.Args[0] as string))
            .Should().Be("Una risposta sola, senza strumenti. Seconda frase.");
    }

    [Fact]
    public async Task The_boundary_survives_output_moderation()
    {
        // With a moderator the reply is buffered and emitted in one piece; it must be the same text
        // the live stream would have shown, boundary included.
        var streamed = await StreamedAsync("Recupero i dati.", "Il catalogo contiene 8 prodotti.",
            o => o.OutputGuardrail = (_, _) => Task.FromResult(true));

        streamed.Should().Contain("Recupero i dati.\n\nIl catalogo contiene 8 prodotti.");
    }
}
