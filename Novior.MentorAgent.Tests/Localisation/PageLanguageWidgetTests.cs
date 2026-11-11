using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using FluentAssertions.Execution;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Components;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Localisation;

/// <summary>
/// The language of the widget is the page's: <c>ChatWidget.Language</c> decides its texts, its voice and the language
/// of every turn it sends, and wins over <c>MentorOptions.Language</c> for that widget only.
///
/// <para>
/// Before it the language was one per server: the localizer was a singleton with the language read at startup, and a
/// site in two languages showed one of them on every page.
/// </para>
/// </summary>
public sealed class PageLanguageWidgetTests : ComponentTestBase
{
    private readonly RecordingStateService _state = new();
    private readonly RecordingOrchestrator _orchestrator = new();

    public PageLanguageWidgetTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(_orchestrator);
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    }

    private static string Text(MentorLanguage language, string key) => new MentorLocalizer(language).Get(key);

    private IRenderedComponent<ChatWidget> Open(MentorLanguage? language)
    {
        var cut = Render<ChatWidget>(p => p.Add(w => w.Language, language));
        cut.Find(".bm-fab").Click();
        return cut;
    }

    private static string Placeholder(IRenderedComponent<ChatWidget> cut)
        => cut.Find("textarea.bm-input").GetAttribute("placeholder") ?? "";

    /// <summary>
    /// Sends <paramref name="text"/> and waits until the orchestrator has it. The widget hands the turn over after an
    /// await (it scrolls the transcript first), so the click returns before the turn is sent: under load an assertion
    /// right after the click found the turn missing.
    /// </summary>
    private void Send(IRenderedComponent<ChatWidget> cut, string text)
    {
        var sent = _orchestrator.Messages.Count;
        cut.Find("textarea.bm-input").Input(text);
        cut.Find(".bm-send-btn").Click();
        cut.WaitForAssertion(() => _orchestrator.Messages.Should().HaveCount(sent + 1), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Two_widgets_in_two_languages_show_their_own_texts_at_the_same_time()
    {
        var italian = Open(MentorLanguage.Italian);
        var french = Open(MentorLanguage.French);

        using (new AssertionScope())
        {
            Placeholder(italian).Should().Be(Text(MentorLanguage.Italian, "input.placeholder"));
            Placeholder(french).Should().Be(Text(MentorLanguage.French, "input.placeholder"));
            italian.Find(".bm-send-btn").GetAttribute("aria-label").Should().Be(Text(MentorLanguage.Italian, "input.send"));
            french.Find(".bm-send-btn").GetAttribute("aria-label").Should().Be(Text(MentorLanguage.French, "input.send"));
            italian.Markup.Should().Contain(Text(MentorLanguage.Italian, "header.close"));
            french.Markup.Should().Contain(Text(MentorLanguage.French, "header.close"));
        }
    }

    [Fact]
    public void Each_widget_sends_its_turns_in_its_own_language()
    {
        var italian = Open(MentorLanguage.Italian);
        var french = Open(MentorLanguage.French);

        Send(italian, "quanti ordini?");
        Send(french, "combien de commandes ?");

        _orchestrator.Messages.Select(m => (m.Text, m.Language)).Should().Equal(
            ("quanti ordini?", MentorLanguage.Italian),
            ("combien de commandes ?", MentorLanguage.French));
    }

    [Fact]
    public void Without_the_parameter_the_widget_is_in_the_application_language_and_its_turns_carry_none()
    {
        Widget.Language = MentorLanguage.Italian;
        var cut = Open(null);

        Send(cut, "ciao");

        Placeholder(cut).Should().Be(Text(MentorLanguage.Italian, "input.placeholder"));
        _orchestrator.Messages.Should().ContainSingle().Which.Language.Should().BeNull(
            "a turn without a language is answered in MentorOptions.Language, read on the server at the turn");
    }

    [Fact]
    public void Every_text_of_an_Italian_widget_is_Italian_on_an_English_application()
    {
        // Every optional control on: each child component draws its own texts, and one left on the application's
        // localizer would show English in an Italian page.
        Widget.EnableVoiceInput      = true;
        Widget.EnableVoiceOutput     = true;
        Widget.EnableOnboardingTour  = true;
        Widget.EnableImageInput      = true;
        Widget.ShowMcpStatus         = true;
        Widget.HasMcpServers         = true;
        Widget.McpServerNames        = ["filesystem"];
        Widget.ShowA2AStatus         = true;
        Widget.HasRemoteAgents       = true;
        Widget.ShowHostedToolsStatus = true;
        Widget.ShowAiDisclosure      = true;

        var cut = Open(MentorLanguage.Italian);

        var english = EnglishOnlyTexts();
        var shown = cut.FindAll("*")
            .SelectMany(e => new[] { e.GetAttribute("aria-label"), e.GetAttribute("title"), e.GetAttribute("placeholder") }
                .Concat(e.ChildNodes.OfType<IText>().Select(t => t.Data)))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .ToList();

        shown.Should().NotBeEmpty();
        shown.Where(t => english.Any(e => t.Contains(e, StringComparison.Ordinal))).Should().BeEmpty(
            "every text of the widget follows its Language");
    }

    /// <summary>The localizer's English texts that differ from the Italian ones, long enough to be told apart.</summary>
    private static string[] EnglishOnlyTexts()
    {
        var table = (System.Collections.IDictionary)typeof(MentorLocalizer)
            .GetField("_strings", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return [.. table.Keys.Cast<string>()
            .Select(k => (En: Text(MentorLanguage.English, k), It: Text(MentorLanguage.Italian, k)))
            .Where(p => p.En != p.It && p.En.Length >= 6 && !p.En.Contains('{'))
            .Select(p => p.En.Split("<br/>")[0])];
    }

    [Fact]
    public void Switching_the_language_redraws_the_texts_keeps_the_conversation_and_sends_the_next_turn_in_the_new_one()
    {
        var cut = Open(MentorLanguage.Italian);
        Send(cut, "quanti ordini?");
        _state.NotifyStreamingChunk("Tre.");
        _state.NotifyStreamingCompleted();
        var bubbles = cut.FindAll(".bm-bubble").Count;

        cut.Render(p => p.Add(w => w.Language, MentorLanguage.French));
        Send(cut, "et les clients ?");

        using (new AssertionScope())
        {
            Placeholder(cut).Should().Be(Text(MentorLanguage.French, "input.placeholder"));
            cut.FindAll(".bm-bubble").Count.Should().BeGreaterThan(bubbles, "the conversation is kept, and the new turn added");
            cut.Markup.Should().Contain("quanti ordini?");
            _orchestrator.Messages.Select(m => m.Language).Should().Equal(MentorLanguage.Italian, MentorLanguage.French);
        }
    }

    [Theory]
    [InlineData(false, "trois")]
    [InlineData(true, "Trois commandes sont en attente. La plus ancienne date d'hier. ")]
    public void The_voice_speaks_the_language_of_the_page(bool streaming, string answer)
    {
        // Both ways of reading aloud: the whole answer at the end, and sentence by sentence while it streams.
        Widget.EnableVoiceOutput = true;
        Widget.VoiceStreaming = streaming;
        JSInterop.Setup<bool>("MentorAgent.isSpeechSynthesisSupported").SetResult(true);
        var cut = Open(MentorLanguage.French);

        _state.NotifyStreamingChunk(answer);
        _state.NotifyStreamingCompleted();

        var speech = JSInterop.Invocations.Where(i => i.Identifier is "MentorAgent.speak" or "MentorAgent.beginSpeech").ToList();
        speech.Should().NotBeEmpty();
        speech.Should().OnlyContain(i => i.Arguments.Contains("fr"), "the page is French, the application English");
    }

    [Fact]
    public async Task The_widget_asks_for_the_tour_of_its_language_and_for_the_new_one_after_a_switch()
    {
        var tour = new RecordingTour();
        Services.AddSingleton<IMentorTour>(tour);
        Widget.EnableOnboardingTour = true;

        var cut = Open(MentorLanguage.French);
        var frenchSkip = cut.Find(".bm-tour__skip").TextContent;
        cut.Render(p => p.Add(w => w.Language, MentorLanguage.Italian));
        await cut.InvokeAsync(() => cut.Instance.ReplayTour());

        tour.Asked.Should().Equal(MentorLanguage.French, MentorLanguage.Italian);
        frenchSkip.Should().Be(Text(MentorLanguage.French, "tour.skip"), "the tour's own texts follow the widget too");
        cut.Find(".bm-tour__skip").TextContent.Should().Be(Text(MentorLanguage.Italian, "tour.skip"));
    }

    [Fact]
    public void An_open_tour_shows_its_steps_in_the_new_language_on_the_step_it_was_at()
    {
        // BUG-228: after a switch the tour's buttons followed the widget, the steps already loaded did not.
        var tour = new TwoLanguageTour();
        Services.AddSingleton<IMentorTour>(tour);
        Widget.EnableOnboardingTour = true;

        var cut = Open(MentorLanguage.English);
        cut.Find(".bm-tour__actions .bm-btn--primary").Click();   // step 2 of 3
        cut.Render(p => p.Add(w => w.Language, MentorLanguage.Italian));

        cut.WaitForAssertion(() => cut.Find(".bm-tour__title").TextContent.Should().Be("Ordini"));
        cut.Find(".bm-tour__count").TextContent.Should().Be(string.Format(Text(MentorLanguage.Italian, "tour.step"), 2, 3));
        tour.Asked.Should().Equal(MentorLanguage.English, MentorLanguage.Italian);
    }

    [Fact]
    public void An_open_tour_whose_new_language_has_fewer_steps_stays_on_one_of_them()
    {
        var tour = new TwoLanguageTour();
        Services.AddSingleton<IMentorTour>(tour);
        Widget.EnableOnboardingTour = true;

        var cut = Open(MentorLanguage.Italian);
        cut.Find(".bm-tour__actions .bm-btn--primary").Click();
        cut.Find(".bm-tour__actions .bm-btn--primary").Click();   // step 3 of 3
        cut.Render(p => p.Add(w => w.Language, MentorLanguage.French));

        cut.WaitForAssertion(() => cut.Find(".bm-tour__title").TextContent.Should().Be("Bienvenue"));
        cut.Find(".bm-tour__count").TextContent.Should().Be(string.Format(Text(MentorLanguage.French, "tour.step"), 1, 1));
    }

    [Fact]
    public void Of_two_quick_switches_the_tour_shows_the_last_language_even_when_the_first_answers_later()
    {
        var tour = new TwoLanguageTour();
        var italian = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tour.Hold[MentorLanguage.Italian] = italian.Task;
        Services.AddSingleton<IMentorTour>(tour);
        Widget.EnableOnboardingTour = true;

        var cut = Open(MentorLanguage.English);
        cut.Render(p => p.Add(w => w.Language, MentorLanguage.Italian));   // its steps are still on the way
        cut.Render(p => p.Add(w => w.Language, MentorLanguage.French));
        cut.WaitForAssertion(() => cut.Find(".bm-tour__title").TextContent.Should().Be("Bienvenue"));
        italian.SetResult();

        cut.WaitForAssertion(() => tour.Answered.Should().Contain(MentorLanguage.Italian));
        cut.Find(".bm-tour__title").TextContent.Should().Be("Bienvenue", "the Italian steps came after the switch to French");
    }

    [Fact]
    public void A_chat_input_on_its_own_uses_the_application_localizer()
    {
        // Composition: a page built from the parts, with no ChatWidget around them, keeps working as before.
        Widget.Language = MentorLanguage.Italian;

        var cut = Render<ChatInput>();

        cut.Find("textarea.bm-input").GetAttribute("placeholder").Should().Be(Text(MentorLanguage.Italian, "input.placeholder"));
    }

    private sealed class RecordingTour : IMentorTour
    {
        internal List<MentorLanguage?> Asked { get; } = [];

        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
            => GetStepsAsync(null, ct);

        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(MentorLanguage? language, CancellationToken ct = default)
        {
            Asked.Add(language);
            return Task.FromResult<IReadOnlyList<MentorTourStep>>([new MentorTourStep("Benvenuto", "Ciao.")]);
        }
    }

    /// <summary>A tour with steps of its own in each language: three in English and Italian, one in French.</summary>
    private sealed class TwoLanguageTour : IMentorTour
    {
        private static readonly Dictionary<MentorLanguage, string[]> Titles = new()
        {
            [MentorLanguage.English] = ["Welcome", "Orders", "Done"],
            [MentorLanguage.Italian] = ["Benvenuto", "Ordini", "Fine"],
            [MentorLanguage.French]  = ["Bienvenue"],
        };

        internal List<MentorLanguage?> Asked { get; } = [];

        /// <summary>The languages whose steps have been handed back.</summary>
        internal List<MentorLanguage?> Answered { get; } = [];

        /// <summary>A language whose steps wait for the task given here.</summary>
        internal Dictionary<MentorLanguage, Task> Hold { get; } = [];

        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
            => GetStepsAsync(null, ct);

        public async Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(MentorLanguage? language, CancellationToken ct = default)
        {
            Asked.Add(language);
            var key = language ?? MentorLanguage.English;
            if (Hold.TryGetValue(key, out var hold)) await hold;
            Answered.Add(language);
            return Titles[key].Select(t => new MentorTourStep(t, t + ".")).ToList();
        }
    }

    /// <summary>Records every turn the widget sends, with its language.</summary>
    private sealed class RecordingOrchestrator : IMentorOrchestrator
    {
        internal List<MentorUserMessage> Messages { get; } = [];

        public Task SendMessageAsync(string userMessage, CancellationToken ct = default)
            => SendMessageAsync(new MentorUserMessage(userMessage), ct);

        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default)
            => SendMessageAsync(new MentorUserMessage(userMessage, attachments), ct);

        public Task SendMessageAsync(MentorUserMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

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
