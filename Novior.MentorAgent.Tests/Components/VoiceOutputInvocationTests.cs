using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// Does the widget actually ask the browser to speak?
///
/// <para>
/// Written to settle a question the browser could not answer. Driving the live apps, a stub on
/// <c>window.MentorAgent.speak</c> recorded the call on the WebAssembly host and recorded nothing
/// on the Blazor Server one — which looks like a transport-dependent defect and is far more likely
/// to be the probe: Blazor resolves an interop identifier through its own lookup, and a property
/// swapped in afterwards is not guaranteed to be on that path. bUnit records invocations by
/// identifier at the interop boundary itself, so it answers the question without the guesswork.
/// </para>
///
/// <para>
/// The two paths are separate on purpose. A streamed answer speaks through
/// <c>beginSpeech</c> + <c>pushSpeech</c>, sentence by sentence, so playback starts before the
/// answer is finished. An answer too short to cross a sentence boundary never opens that session
/// and is read at completion by a single <c>speak</c>. Losing either one is silent — the text still
/// arrives, the widget still looks right, and only the sound is missing.
/// </para>
/// </summary>
public sealed class VoiceOutputInvocationTests : ComponentTestBase
{
    private readonly RecordingStateService _state = new();

    public VoiceOutputInvocationTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        Widget.EnableVoiceOutput = true;
        Widget.VoiceStreaming    = true;
    }

    /// <summary>
    /// The widget only turns playback on once it has asked the browser whether speech synthesis
    /// exists, and that answer arrives through interop — so the stub has to say yes before the
    /// first render, exactly as a real browser would.
    /// </summary>
    private IRenderedComponent<ChatWidget> OpenWidgetWithSpeech()
    {
        JSInterop.Setup<bool>("MentorAgent.isSpeechSynthesisSupported").SetResult(true);
        var cut = Render<ChatWidget>();
        cut.Find(".bm-fab").Click();
        return cut;
    }

    private IReadOnlyList<string> SpeechCalls()
        => [.. JSInterop.Invocations
            .Select(i => i.Identifier)
            .Where(k => k is "MentorAgent.beginSpeech" or "MentorAgent.pushSpeech"
                          or "MentorAgent.speak" or "MentorAgent.endSpeech")];

    [Fact]
    public void A_streamed_answer_speaks_sentence_by_sentence()
    {
        var cut = OpenWidgetWithSpeech();

        _state.NotifyStreamingChunk("Gli ordini in attesa sono tre. ");
        _state.NotifyStreamingChunk("Il più vecchio risale a ieri. ");
        _state.NotifyStreamingCompleted();

        SpeechCalls().Should().Contain("MentorAgent.beginSpeech",
            "streaming playback has to open a session before the answer is finished, or the user " +
            "waits in silence for the whole turn");
        SpeechCalls().Should().Contain("MentorAgent.pushSpeech");
    }

    [Fact]
    public void A_short_answer_with_no_sentence_boundary_is_still_read_at_completion()
    {
        // The fallback branch. Nothing crosses a boundary here, so no streaming session is ever
        // opened and the whole answer has to be spoken once the turn ends.
        var cut = OpenWidgetWithSpeech();

        _state.NotifyStreamingChunk("otto prodotti");
        _state.NotifyStreamingCompleted();

        SpeechCalls().Should().Contain("MentorAgent.speak",
            "an answer too short to open a streaming session must still be read out");
    }

    [Fact]
    public void With_voice_output_off_nothing_is_spoken()
    {
        // The control: the flag has to actually gate playback, or the toggle is decorative.
        Widget.EnableVoiceOutput = false;
        var cut = Render<ChatWidget>();
        cut.Find(".bm-fab").Click();

        _state.NotifyStreamingChunk("Gli ordini in attesa sono tre. ");
        _state.NotifyStreamingCompleted();

        SpeechCalls().Should().BeEmpty();
    }

    /// <summary>Does nothing; this file is about interop, not turns.</summary>
    private sealed class StubOrchestrator : IMentorOrchestrator
    {
        public Task SendMessageAsync(string userMessage, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments,
                                     CancellationToken ct = default)
            => Task.CompletedTask;

        public void CancelCurrentRequest() { }

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<System.Text.Json.JsonElement?> SerializeSessionAsync(CancellationToken ct = default)
            => Task.FromResult<System.Text.Json.JsonElement?>(null);

        public Task RestoreSessionAsync(System.Text.Json.JsonElement snapshot, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RespondToApprovalAsync(ConfirmationRequest req, bool approved, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
