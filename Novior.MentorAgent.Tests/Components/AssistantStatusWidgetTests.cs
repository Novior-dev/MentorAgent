using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// Story 1290 — an application that turns its assistant off from somewhere else (the console's switch, through the
/// bridge) registers an <see cref="IMentorAssistantStatus"/>: while it is off the widget's input and send button are
/// disabled, and a sentence in the widget's language says so before anyone writes. Without one nothing changes.
/// </summary>
public sealed class AssistantStatusWidgetTests : ComponentTestBase
{
    private readonly RecordingStateService _state = new();
    private readonly CountingOrchestrator _orchestrator = new();
    private readonly Switch _switch = new();

    public AssistantStatusWidgetTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(_orchestrator);
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    }

    private static readonly MentorLanguage[] Languages = Enum.GetValues<MentorLanguage>();

    private static bool IsDisabled(AngleSharp.Dom.IElement element) => element.HasAttribute("disabled");

    [Fact]
    public void Without_a_status_the_widget_is_as_before()
    {
        var cut = Render<ChatWidget>();

        cut.FindAll(".bm-assistant-off").Should().BeEmpty();
        IsDisabled(cut.Find("textarea")).Should().BeFalse();
    }

    [Fact]
    public void With_the_assistant_off_nothing_can_be_written_and_a_sentence_says_why_before_anyone_writes()
    {
        _switch.Set(false);
        Services.AddSingleton<IMentorAssistantStatus>(_switch);

        var cut = Render<ChatWidget>();

        var off = cut.Find(".bm-assistant-off");
        off.TextContent.Trim().Should().Be(new MentorLocalizer(Widget.Language).Get("assistant.off"));
        off.GetAttribute("role").Should().Be("status", "a screen reader reads it when it appears");
        off.NextElementSibling!.ClassList.Should().Contain("bm-input-area", "right above the input");
        IsDisabled(cut.Find("textarea")).Should().BeTrue();
        IsDisabled(cut.Find(".bm-send-btn")).Should().BeTrue();

        var status = cut.Find(".bm-header__status");
        status.ClassList.Should().Contain("bm-header__status--off").And.NotContain("bm-header__status--online",
            "the header does not say online beside a sentence that says off");
        status.TextContent.Trim().Should().Be(new MentorLocalizer(Widget.Language).Get("status.off"));
    }

    [Fact]
    public async Task With_the_assistant_off_the_welcome_panel_offers_no_suggestion_to_send()
    {
        Widget.EnableSuggestions = true;
        Services.AddSingleton<IMentorAssistantStatus>(_switch);
        var cut = Render<ChatWidget>();
        cut.FindAll(".bm-chip").Should().NotBeEmpty("the suggestions are on");

        await Task.Run(() => _switch.Set(false));

        cut.WaitForAssertion(() => cut.FindAll(".bm-chip").Should().BeEmpty("a suggestion would send a message"));
        _orchestrator.Sent.Should().Be(0);
    }

    [Fact]
    public async Task Turned_off_and_on_again_from_a_background_thread_the_widget_follows()
    {
        Services.AddSingleton<IMentorAssistantStatus>(_switch);
        var cut = Render<ChatWidget>();
        cut.FindAll(".bm-assistant-off").Should().BeEmpty();

        await Task.Run(() => _switch.Set(false));
        cut.WaitForAssertion(() => cut.FindAll(".bm-assistant-off").Should().ContainSingle());
        IsDisabled(cut.Find("textarea")).Should().BeTrue();

        cut.Find(".bm-header__status").ClassList.Should().Contain("bm-header__status--off");

        await Task.Run(() => _switch.Set(true));
        cut.WaitForAssertion(() => cut.FindAll(".bm-assistant-off").Should().BeEmpty());
        IsDisabled(cut.Find("textarea")).Should().BeFalse();
        cut.Find(".bm-header__status").ClassList.Should().Contain("bm-header__status--online");
    }

    [Fact]
    public async Task A_message_the_client_did_not_send_because_the_assistant_is_off_stays_with_the_sentence_and_no_error()
    {
        // The WebAssembly client: the first message waits for the value of the connection it opened, finds the
        // assistant off, and sends nothing; the status changes before the send returns.
        Services.AddSingleton<IMentorAssistantStatus>(_switch);
        _orchestrator.InsteadOfTurn = () => Task.Run(() => _switch.Set(false));
        var cut = Render<ChatWidget>();

        cut.Find("textarea").Input("Show the orders");
        cut.Find(".bm-send-btn").Click();

        cut.WaitForAssertion(() => cut.FindAll(".bm-assistant-off").Should().ContainSingle());
        cut.FindAll(".bm-bubble--user").Should().ContainSingle().Which.TextContent.Should().Contain("Show the orders",
            "the message stays where it was written, with the sentence saying why nothing answers");
        cut.Markup.Should().NotContain(new MentorLocalizer(Widget.Language).Get("error.send"), "it is no error");
        IsDisabled(cut.Find("textarea")).Should().BeTrue();
        _orchestrator.Sent.Should().Be(0);
        cut.Find(".bm-header__status").ClassList.Should().Contain("bm-header__status--off", "not busy: no turn runs");
    }

    [Fact]
    public void A_widget_that_is_gone_no_longer_listens()
    {
        Services.AddSingleton<IMentorAssistantStatus>(_switch);
        var cut = Render<ChatWidget>();
        _switch.Handlers.Should().Be(1);

        cut.Instance.Dispose();

        _switch.Handlers.Should().Be(0);
    }

    [Fact]
    public void The_sentence_is_in_the_widgets_language()
    {
        Widget.Language = MentorLanguage.Italian;
        _switch.Set(false);
        Services.AddSingleton<IMentorAssistantStatus>(_switch);

        var cut = Render<ChatWidget>();

        cut.Find(".bm-assistant-off").TextContent.Trim().Should().Be("Assistente spento dall'amministratore");
        cut.Find(".bm-header__status").TextContent.Trim().Should().Be("spento");
    }

    [Theory]
    [InlineData("assistant.off", "The assistant was turned off by the administrator")]
    [InlineData("status.off", "off")]
    public void The_texts_are_translated_into_every_language_of_the_widget(string key, string english)
    {
        new MentorLocalizer(MentorLanguage.English).Get(key).Should().Be(english);

        foreach (var language in Languages.Where(l => l != MentorLanguage.English))
            new MentorLocalizer(language).Get(key)
                .Should().NotBeNullOrWhiteSpace().And.NotBe(key).And.NotBe(english,
                    "{0} has a text of its own, not the English fallback", language);
    }

    /// <summary>The application's switch: raises <see cref="Changed"/> only when the value changes.</summary>
    private sealed class Switch : IMentorAssistantStatus
    {
        private EventHandler? _changed;

        public bool IsOn { get; private set; } = true;

        public int Handlers => _changed?.GetInvocationList().Length ?? 0;

        public event EventHandler? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public void Set(bool on)
        {
            if (IsOn == on) return;
            IsOn = on;
            _changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Counts the turns the widget sends.</summary>
    private sealed class CountingOrchestrator : IMentorOrchestrator
    {
        public int Sent;

        /// <summary>Runs in place of the turn, as the WebAssembly client that finds the assistant off and sends nothing.</summary>
        public Func<Task>? InsteadOfTurn;

        public Task SendMessageAsync(string userMessage, CancellationToken ct = default)
        {
            if (InsteadOfTurn is { } instead) return instead();
            Interlocked.Increment(ref Sent);
            return Task.CompletedTask;
        }

        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default)
            => SendMessageAsync(userMessage, ct);

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
