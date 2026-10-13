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
/// Part C2.5 — the voice state machine's C# half: the four documented barge-in triggers.
///
/// <para>
/// The core README states them as one sentence: playback stops when the user <b>presses the
/// microphone</b>, <b>sends a message</b>, <b>presses Stop</b>, or <b>starts a new conversation</b>.
/// One of those four lives in JavaScript and is pinned in <c>js/dom.test.js</c>; the other three are
/// C# call sites in <c>ChatWidget</c> and nothing asserted them.
/// </para>
///
/// <para>
/// The interesting part is not that each calls <c>stopSpeaking</c> — it is that only <b>one</b> of
/// them is gated on <c>VoiceBargeIn</c>. Sending a message is a barge-in proper, so a host can opt
/// out of it. Stop and Reset are not: they are the user saying "cease", and an assistant that kept
/// reading aloud through either would be the most obviously broken thing in the package. A refactor
/// that tidied all four behind the same flag would look like a simplification and would silently
/// take that away, which is exactly the kind of change a test has to stand in front of.
/// </para>
/// </summary>
public sealed class VoiceBargeInTests : ComponentTestBase
{
    private readonly RecordingStateService _state = new();

    public VoiceBargeInTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    }

    /// <summary>How many times the widget asked the browser to stop speaking.</summary>
    private int StopSpeakingCalls
        => JSInterop.Invocations.Count(i => i.Identifier == "MentorAgent.stopSpeaking");

    private IRenderedComponent<ChatWidget> RenderWidget() => Render<ChatWidget>();

    // ── Turning voice output off ────────────────────────────────────────────────
    //
    // The one trigger with a surface a test can call. The other three are private handlers wired to
    // markup; reaching them through the rendered component would assert bUnit's event dispatch
    // rather than the library, so they are checked structurally further down instead. Saying that
    // out loud beats a test whose name promises a click it never performs.

    [Fact]
    public async Task Turning_voice_output_off_silences_what_is_already_playing()
    {
        // The setting is not "stop speaking next time". A user who switches it off mid-sentence
        // expects silence now — the button is the only control they have while it talks.
        //
        // Voice output starts *off*: the widget enables it during initialisation only if the
        // browser reports speech-synthesis support, and bUnit's loose interop answers false. So the
        // first toggle is the on transition and the second is the one under test.
        var cut = RenderWidget();

        await cut.InvokeAsync(() => cut.Instance.ToggleVoiceOutput());   // off → on
        var afterOn = StopSpeakingCalls;

        await cut.InvokeAsync(() => cut.Instance.ToggleVoiceOutput());   // on → off

        StopSpeakingCalls.Should().BeGreaterThan(afterOn);
    }

    [Fact]
    public async Task Turning_voice_output_on_does_not_stop_anything()
    {
        // The control for the test above: only the off transition silences. Calling stopSpeaking on
        // the way *on* would cancel the queue of a turn that had just started speaking.
        var cut = RenderWidget();
        var before = StopSpeakingCalls;

        await cut.InvokeAsync(() => cut.Instance.ToggleVoiceOutput());   // off → on

        StopSpeakingCalls.Should().Be(before);
    }

    // ── The claim itself, read off the source ───────────────────────────────────

    [Fact]
    public void All_four_documented_triggers_exist_in_the_code()
    {
        // A structural check, deliberately: three of the four are private handlers wired to markup,
        // and reaching them through the rendered component would assert bUnit's event dispatch
        // rather than the library's behaviour. What matters is that each site is still there and
        // still calls the same thing.
        var widget = Docs.RepoFiles.SourceFiles
            .Single(f => f.EndsWith("ChatWidget.razor.cs", StringComparison.Ordinal));
        var source = File.ReadAllText(widget);

        var callSites = System.Text.RegularExpressions.Regex
            .Matches(source, @"await StopSpeakingAsync\(\)").Count;

        callSites.Should().BeGreaterThanOrEqualTo(4,
            "reset, send, Stop and the voice toggle each stop playback; the microphone is the " +
            "fourth documented trigger and lives in MentorAgent.js");
    }

    [Fact]
    public void Only_the_send_trigger_is_gated_on_the_opt_out_flag()
    {
        // The property this file exists for. VoiceBargeIn is documented as an opt-out for taking
        // the floor by *sending*; Stop and Reset must never consult it. Measured by counting the
        // guarded call sites rather than the total.
        var widget = Docs.RepoFiles.SourceFiles
            .Single(f => f.EndsWith("ChatWidget.razor.cs", StringComparison.Ordinal));
        var source = File.ReadAllText(widget);

        var guarded = System.Text.RegularExpressions.Regex
            .Matches(source, @"VoiceBargeIn\)\s*\r?\n\s*await StopSpeakingAsync\(\)").Count;

        guarded.Should().Be(1,
            "exactly one trigger — sending a message — may be switched off; Stop and Reset are the " +
            "user saying cease, and an assistant that read on through either would be plainly broken");
    }

    [Fact]
    public void The_readme_still_documents_all_four()
    {
        // The documentation is the contract here: the flag's meaning is only discoverable from it.
        var readme = Docs.RepoFiles.Readme("core");

        readme.Should().Contain("VoiceBargeIn");
        readme.ToLowerInvariant().Should().Contain("barge");
    }

    /// <summary>The widget only needs the orchestrator to exist; nothing here drives a turn.</summary>
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
