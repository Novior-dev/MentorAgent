using System.Text;
using AngleSharp.Dom;
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
/// BUG-257 — what is decoration is not read. With Narrator the widget said "ShopFlow assistant image, ShopFlow
/// assistant": the avatar beside the name in the header, the orb beside the welcome's heading, an "AI" image before
/// every reply, the welcome suggestions' emoji in their names, and icons a screen reader may read. The avatars are now
/// decoration (<c>alt=""</c>), every icon is <c>aria-hidden</c> and not focusable, a suggestion is named by its words,
/// and each message in the log says who speaks, in words hidden from sight.
/// </summary>
public sealed class DecorativeElementsTests : ComponentTestBase
{
    private readonly RecordingStateService _state = new();

    public DecorativeElementsTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddSingleton<IMentorTour>(new OneStepTour());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // Every optional control on: each is rendered behind its own flag, and its icon with it.
        Widget.BotName               = "ShopFlow";
        Widget.EnableSuggestions     = true;
        Widget.EnableVoiceInput      = true;
        Widget.EnableVoiceOutput     = true;
        Widget.EnableOnboardingTour  = true;
        Widget.EnableImageInput      = true;
        Widget.ShowMcpStatus         = true;
        Widget.HasMcpServers         = true;
        Widget.McpServerNames        = ["filesystem"];
        Widget.ShowA2AStatus         = true;
        Widget.HasRemoteAgents       = true;
        Widget.RemoteAgentDisplays   = [new AgentDisplayInfo { Name = "ShopFlowRemote" }];
        Widget.ShowHostedToolsStatus = true;
        Widget.HostedTools           = MentorHostedTools.WebSearch;
        Widget.ShowRagSources        = true;
        JSInterop.Setup<bool>("MentorAgent.isSpeechSynthesisSupported").SetResult(true);
    }

    /// <summary>The widget, opened, in one of the states a visitor meets.</summary>
    private IRenderedComponent<ChatWidget> Open(string state, MentorLanguage language = MentorLanguage.Italian)
    {
        Widget.Language = language;
        if (state != "tour")
            JSInterop.Setup<bool>("MentorAgent.flagGet", "mentoragent.tour.seen").SetResult(true);
        var cut = Render<ChatWidget>();
        cut.Find(".bm-fab").Click();

        switch (state)
        {
            case "welcome":
                cut.WaitForAssertion(() => cut.FindAll(".bm-chip").Should().NotBeEmpty());
                break;
            case "tour":
                cut.WaitForAssertion(() => cut.FindAll(".bm-tour").Should().ContainSingle());
                break;
            case "typing":
                Send(cut, "quanti ordini sono aperti?");
                _state.NotifyBusyChanged(true);
                cut.WaitForAssertion(() => cut.FindAll(".bm-bubble--typing").Should().ContainSingle());
                break;
            case "reply":
                Send(cut, "quanti ordini sono aperti?");
                _state.NotifyBusyChanged(true);
                _state.NotifyRagSourcesReady([new MentorRagResult("Ordini", "https://example.com/ordini", "Ordini", 0.9f)]);
                _state.NotifyStreamingChunk("Sono tre.");
                _state.NotifyStreamingCompleted();
                _state.NotifyStreamingChunk("E il più vecchio");   // a second reply, still streaming
                cut.WaitForAssertion(() => cut.FindAll(".bm-bubble--streaming").Should().ContainSingle());
                break;
            case "confirmation":
                Send(cut, "annulla l'ordine 1005");
                _state.NotifyBusyChanged(true);
                _state.NotifyConfirmationRequired(new ConfirmationRequest
                {
                    ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "Annullo l'ordine #1005",
                });
                cut.WaitForAssertion(() => cut.FindAll(".bm-confirm").Should().ContainSingle());
                break;
            case "details":
                cut.Find(".bm-mcp-pill").Click();
                cut.Find(".bm-a2a-pill").Click();
                cut.Find(".bm-hosted-pill").Click();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
        return cut;
    }

    private static void Send(IRenderedComponent<ChatWidget> cut, string text)
    {
        cut.Find("textarea").Input(text);
        cut.Find("textarea").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
    }

    /// <summary>A button's name as a screen reader computes it here: its aria-label, else its text out of aria-hidden.</summary>
    private static string NameOf(IElement button)
        => button.GetAttribute("aria-label") is { Length: > 0 } label ? label : VisibleText(button);

    private static string VisibleText(INode node)
    {
        var text = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            if (child is IElement e && e.GetAttribute("aria-hidden") == "true") continue;
            text.Append(child is IElement ? VisibleText(child) : child.TextContent);
        }
        return text.ToString();
    }

    private static bool HasEmoji(string s)
        => s.EnumerateRunes().Any(r => r.Value > 0xFFFF || Rune.GetUnicodeCategory(r) == System.Globalization.UnicodeCategory.OtherSymbol);

    [Theory]
    [InlineData("welcome")]
    [InlineData("tour")]
    [InlineData("typing")]
    [InlineData("reply")]
    [InlineData("confirmation")]
    [InlineData("details")]
    public void In_every_state_no_image_is_named_no_icon_is_read_and_no_button_is_named_with_an_emoji(string state)
    {
        var cut = Open(state);

        cut.FindAll("img").Select(i => i.OuterHtml)
            .Where(html => !html.Contains("alt=\"\"", StringComparison.Ordinal))
            .Should().BeEmpty($"in the state '{state}' every image of the widget is decoration beside the name written");
        cut.FindAll("svg")
            .Where(s => s.GetAttribute("aria-hidden") != "true" || s.GetAttribute("focusable") != "false")
            .Select(s => s.ParentElement?.ClassName ?? "?")
            .Should().BeEmpty($"in the state '{state}' every icon is hidden from a screen reader and not focusable");
        cut.FindAll("button").Select(NameOf).Where(HasEmoji)
            .Should().BeEmpty($"in the state '{state}' a button is named by its words, not read with its emoji");
    }

    [Theory]
    [InlineData(MentorLanguage.Italian, "Tu:", "ShopFlow:")]
    [InlineData(MentorLanguage.English, "You:", "ShopFlow:")]
    [InlineData(MentorLanguage.Japanese, "あなた：", "ShopFlow：")]
    public void Each_message_in_the_log_says_who_speaks_in_the_widgets_language(MentorLanguage language, string you, string assistant)
    {
        var cut = Open("reply", language);

        var speakers = cut.FindAll(".bm-messages .bm-bubble:not(.bm-bubble--typing)")
            .Select(b => b.QuerySelector(".bm-bubble__body")!.FirstElementChild)
            .Select(e => e is not null && e.ClassList.Contains("bm-sr-only") ? e.TextContent : "(none)")
            .ToList();

        speakers.Should().Equal([you, assistant, assistant], "the user's message, the reply and the reply streaming");
    }

    [Fact]
    public void A_suggestion_is_named_by_its_words_and_sends_them_without_its_icon()
    {
        string? sent = null;
        Widget.Language = MentorLanguage.Italian;
        var cut = Render<WelcomePanel>(p => p.Add(c => c.OnChipClick, (string s) => sent = s));

        var chip = cut.FindAll(".bm-chip")[0];
        chip.QuerySelector(".bm-chip__icon")!.GetAttribute("aria-hidden").Should().Be("true");
        NameOf(chip).Should().Be("Cosa puoi fare?");
        chip.Click();

        sent.Should().Be("Cosa puoi fare?", "the user's message says what the button said, without the icon read aloud");
    }

    [Theory]
    [InlineData("💡 Cosa puoi fare?", "💡", "Cosa puoi fare?")]
    [InlineData("❓ どうやって使う？", "❓", "どうやって使う？")]
    [InlineData("→ Start", "→", "Start")]
    [InlineData("Cosa puoi fare?", null, "Cosa puoi fare?")]
    [InlineData("💡", null, "💡")]
    [InlineData("1. Start", null, "1. Start")]
    public void A_suggestion_loses_its_first_word_only_when_it_has_no_letter_and_no_digit(string label, string? icon, string text)
        => WelcomePanel.SplitIcon(label).Should().Be((icon, text));

    /// <summary>One step: the tour opens in the widget the first time.</summary>
    private sealed class OneStepTour : IMentorTour
    {
        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default) => GetStepsAsync(null, ct);

        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(MentorLanguage? language, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MentorTourStep>>([new MentorTourStep("Benvenuto", "Ciao.")]);
    }

    /// <summary>Does nothing; this file is about markup, not turns.</summary>
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
