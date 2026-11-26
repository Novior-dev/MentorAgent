using System.Collections.Concurrent;
using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// BUG-261 — the widget adds MentorAgent.js to the head itself. Without a prerender the script tag comes with the first
/// interactive render and the browser runs it later: the calls of that render found no <c>MentorAgent</c>, failed without
/// a word, and the voice and the primary colour stayed off for the visit. The first render now waits for the script,
/// asking <c>MentorAgent.ready</c> until it answers, for 10 s at most, and then says so in a Warning.
/// </summary>
public sealed class ScriptReadyTests : ComponentTestBase
{
    private readonly Logs _logs = new();

    public ScriptReadyTests()
    {
        Services.AddSingleton<IMentorStateService>(new RecordingStateService());
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(_logs));
        Services.AddSingleton(new ChatWidget.ScriptWaiting(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20)));

        Widget.EnableVoiceOutput = true;
        Widget.PrimaryColor      = "#0a7cff";
        JSInterop.Setup<bool>("MentorAgent.isSpeechSynthesisSupported").SetResult(true);
    }

    private static JSException NotLoaded()
        => new("Could not find 'MentorAgent.ready' ('MentorAgent' was undefined).");

    /// <summary>The widget waits with timers, not renders: this looks at its calls until <paramref name="done"/>.</summary>
    private async Task UntilAsync(IRenderedComponent<ChatWidget> cut, Func<List<string>, bool> done, string failure)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done(await CallsAsync(cut)))
        {
            DateTime.UtcNow.Should().BeBefore(until, failure);
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The widget's calls so far, read on the renderer's dispatcher: the widget makes them there, from its timer, and
    /// bUnit's list of them is not safe to read from another thread meanwhile (it failed once in a release gate).
    /// </summary>
    private Task<List<string>> CallsAsync(IRenderedComponent<ChatWidget> cut)
        => cut.InvokeAsync(() => JSInterop.Invocations.Select(i => i.Identifier)
            .Where(k => k.StartsWith("MentorAgent.", StringComparison.Ordinal)).ToList());

    [Fact]
    public async Task A_script_that_comes_after_the_first_render_is_waited_for_and_the_voice_and_the_colour_are_on()
    {
        // Long enough not to give up while the test looks: the script comes when the test says so.
        Services.AddSingleton(new ChatWidget.ScriptWaiting(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20)));
        var ready = JSInterop.SetupVoid("MentorAgent.ready");
        ready.SetException(NotLoaded());
        var cut = Render<ChatWidget>();
        await UntilAsync(cut, calls => calls.Count(c => c == "MentorAgent.ready") >= 3, "the widget did not ask again");
        (await CallsAsync(cut)).Should().NotContain(["MentorAgent.setRootVars", "MentorAgent.isSpeechSynthesisSupported"],
            "nothing of the first render is asked of a script that is not there yet");

        ready.SetVoidResult();   // the browser has run the script

        await UntilAsync(cut, calls => calls.Contains("MentorAgent.setRootVars") && calls.Contains("MentorAgent.isSpeechSynthesisSupported"),
            "the first render did not go on once the script was there");
        (await CallsAsync(cut)).Count(c => c == "MentorAgent.ready").Should().BeLessThan(100, "it stopped asking once the script answered");
        cut.Find(".bm-fab").Click();
        cut.Find(".bm-header__actions .bm-icon-btn").ClassList.Should().Contain("bm-icon-btn--active", "the voice is on");
        _logs.Warnings.Should().BeEmpty("the script came in time");
    }

    [Fact]
    public async Task A_script_already_in_the_page_is_asked_once()
    {
        JSInterop.SetupVoid("MentorAgent.ready").SetVoidResult();
        var cut = Render<ChatWidget>();

        await UntilAsync(cut, calls => calls.Contains("MentorAgent.isSpeechSynthesisSupported"), "the voice was not looked for");
        var calls = await CallsAsync(cut);
        calls.Count(c => c == "MentorAgent.ready").Should().Be(1);
        calls.IndexOf("MentorAgent.ready").Should().BeLessThan(calls.IndexOf("MentorAgent.setRootVars"));
    }

    [Fact]
    public void A_script_that_never_comes_is_said_in_a_Warning_once_and_the_widget_goes_on()
    {
        JSInterop.SetupVoid("MentorAgent.ready").SetException(NotLoaded());
        var cut = Render<ChatWidget>();

        cut.WaitForAssertion(() => _logs.Warnings.Should().ContainSingle(w => w.Contains("MentorAgent.js did not load")
            && w.Contains("HeadOutlet")), TimeSpan.FromSeconds(3));
        cut.Find(".bm-fab").Click();
        cut.Find(".bm-root").ClassList.Should().Contain("bm-open", "the widget works without its script's extras");
    }

    /// <summary>The Warnings the widget logs.</summary>
    private sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _warnings = new();
        internal IReadOnlyList<string> Warnings => [.. _warnings];
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(Logs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) owner._warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    /// <summary>Does nothing; this file is about the first render.</summary>
    private sealed class StubOrchestrator : IMentorOrchestrator
    {
        public Task SendMessageAsync(string userMessage, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments,
                                     CancellationToken ct = default) => Task.CompletedTask;

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
