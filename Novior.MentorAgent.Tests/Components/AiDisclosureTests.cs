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
/// Two tools for article 50 of the EU AI Act: a notice, under the header, that tells people they are talking to an
/// AI assistant, on by default and in the widget's language; and a machine-readable marker on every reply the model
/// wrote, which stays when the notice is turned off. The notice's words are the application's to choose, and may
/// still change: only the default text's own test reads them.
/// </summary>
public sealed class AiDisclosureTests : ComponentTestBase
{
    private const string Marker = "data-ai-generated";

    private readonly RecordingStateService _state = new();

    public AiDisclosureTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    }

    private static readonly MentorLanguage[] Languages = Enum.GetValues<MentorLanguage>();

    // ── The default text ─────────────────────────────────────────────────────────

    [Fact]
    public void The_default_notice_is_the_one_sentence_chosen_for_it()
    {
        new MentorLocalizer(MentorLanguage.English).Get("ai.disclosure")
            .Should().Be("You are talking to an AI assistant: its replies are generated automatically.");
    }

    [Fact]
    public void The_default_notice_is_translated_into_every_language_of_the_widget()
    {
        var english = new MentorLocalizer(MentorLanguage.English).Get("ai.disclosure");

        foreach (var language in Languages.Where(l => l != MentorLanguage.English))
            new MentorLocalizer(language).Get("ai.disclosure")
                .Should().NotBeNullOrWhiteSpace().And.NotBe("ai.disclosure").And.NotBe(english,
                    "{0} has a text of its own, not the English fallback", language);
    }

    // ── The notice ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_notice_is_shown_by_default_under_the_header_outside_the_messages()
    {
        var cut = Render<ChatWidget>();

        var notice = cut.Find(".bm-ai-disclosure");
        notice.TextContent.Trim().Should().Be(new MentorLocalizer(Widget.Language).Get("ai.disclosure"));
        notice.ParentElement!.ClassList.Should().Contain("bm-panel",
            "a child of the panel, not of the messages: it does not scroll away with them");
        notice.PreviousElementSibling!.TagName.Should().BeEquivalentTo("header", "right under the header");
        notice.QuerySelectorAll("button").Should().BeEmpty("it cannot be closed");
    }

    [Fact]
    public void The_notice_stays_through_the_conversation()
    {
        var cut = Render<ChatWidget>();

        _state.NotifyStreamingChunk("Hello.");
        _state.NotifyStreamingCompleted();

        cut.WaitForAssertion(() => cut.FindAll(".bm-bubble--assistant").Should().NotBeEmpty());
        cut.FindAll(".bm-ai-disclosure").Should().ContainSingle();
    }

    [Fact]
    public void The_notice_is_in_the_widgets_language()
    {
        Widget.Language = MentorLanguage.Italian;

        var cut = Render<ChatWidget>();

        cut.Find(".bm-ai-disclosure").TextContent.Trim()
            .Should().Be(new MentorLocalizer(MentorLanguage.Italian).Get("ai.disclosure"));
    }

    [Fact]
    public void The_applications_own_notice_is_shown_as_written_in_every_language()
    {
        Widget.Language = MentorLanguage.German;
        Widget.AiDisclosureMessage = "You are talking to Aria, an <AI> assistant.";

        var cut = Render<ChatWidget>();

        var notice = cut.Find(".bm-ai-disclosure");
        notice.TextContent.Trim().Should().Be("You are talking to Aria, an <AI> assistant.");
        notice.QuerySelectorAll("AI").Should().BeEmpty("the text is text, never markup");
    }

    [Fact]
    public void The_notice_can_be_turned_off()
    {
        Widget.ShowAiDisclosure = false;

        Render<ChatWidget>().FindAll(".bm-ai-disclosure").Should().BeEmpty();
    }

    [Fact]
    public async Task Both_hosts_hand_the_two_options_to_the_widget()
    {
        await using var host = Novior.MentorAgent.Tests.Harness.MentorHostFixture.Create(o =>
        {
            o.ShowAiDisclosure = false;
            o.AiDisclosureMessage = "Server words.";
        });
        var server = host.Get<Microsoft.Extensions.Options.IOptions<MentorWidgetOptions>>().Value;

        var services = new ServiceCollection().AddLogging();
        Novior.MentorAgent.Blazor.Extensions.ClientServiceExtensions.AddMentorAgentBlazor(services, o =>
        {
            o.ShowAiDisclosure = false;
            o.AiDisclosureMessage = "Client words.";
        });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MentorWidgetOptions>>().Value;

        (server.ShowAiDisclosure, server.AiDisclosureMessage).Should().Be((false, "Server words."), "AddMentorAgent");
        (client.ShowAiDisclosure, client.AiDisclosureMessage).Should().Be((false, "Client words."), "AddMentorAgentBlazor");
        new MentorWidgetOptions().ShowAiDisclosure.Should().BeTrue("on by default");
    }

    // ── The marker ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_reply_of_the_model_is_marked_and_the_users_message_is_not()
    {
        var cut = Render<ChatWidget>();

        cut.Find(".bm-fab").Click();
        cut.Find("textarea").Input("hello");
        cut.Find(".bm-send-btn").Click();
        cut.WaitForAssertion(() => cut.FindAll(".bm-bubble--user").Should().ContainSingle());
        _state.NotifyStreamingChunk("Hello, how can I help?");

        cut.WaitForAssertion(() => cut.Find(".bm-bubble--streaming").GetAttribute(Marker).Should().Be("true",
            "a reply is marked while it streams"));
        _state.NotifyStreamingCompleted();

        cut.WaitForAssertion(() => cut.FindAll(".bm-bubble--streaming").Should().BeEmpty());
        cut.Find(".bm-bubble--assistant").GetAttribute(Marker).Should().Be("true");
        cut.FindAll(".bm-bubble--user").Should().NotBeEmpty()
            .And.OnlyContain(b => !b.HasAttribute(Marker), "what the user wrote is not AI-generated");
    }

    [Fact]
    public void An_error_the_widget_shows_is_not_marked()
    {
        var cut = Render<ChatWidget>();

        _state.NotifyError("The service is unavailable.");

        cut.WaitForAssertion(() => cut.FindAll(".bm-bubble--assistant").Should().ContainSingle());
        cut.Find(".bm-bubble--assistant").HasAttribute(Marker).Should().BeFalse(
            "the library's own message, not the model's");
    }

    [Fact]
    public void Turning_the_notice_off_leaves_the_marker()
    {
        Widget.ShowAiDisclosure = false;
        var cut = Render<ChatWidget>();

        _state.NotifyStreamingChunk("Hello.");
        _state.NotifyStreamingCompleted();

        cut.WaitForAssertion(() => cut.FindAll(".bm-bubble--assistant").Should().ContainSingle());
        cut.Find(".bm-bubble--assistant").GetAttribute(Marker).Should().Be("true",
            "the notice and the marker are separate tools");
    }

    /// <summary>The widget is driven through <see cref="IMentorStateService"/>; the orchestrator only has to answer.</summary>
    private sealed class StubOrchestrator : IMentorOrchestrator
    {
        public Task SendMessageAsync(string userMessage, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default)
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
