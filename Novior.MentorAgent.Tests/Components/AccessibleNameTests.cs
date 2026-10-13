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
/// BUG-048 — every icon-only control in the widget must carry an <c>aria-label</c>.
///
/// <para>
/// Found live on the NuGet parity pass: of the nine buttons inside an open panel, seven render no
/// text at all and only one carried an <c>aria-label</c>. The other six were named by <c>title</c>
/// alone — the voice-output toggle, replay tour, new conversation, close, the MCP and A2A pills —
/// and so was the <b>Stop</b> button, which is the one control a user needs when a reply is running
/// away from them.
/// </para>
///
/// <para>
/// <c>title</c> is the last resort in the accessible-name computation, several screen readers do
/// not announce it at all, and it never surfaces on touch. An icon-only <c>&lt;button&gt;</c> named
/// only by <c>title</c> is announced as "button" — unnamed. This is not a style preference: the
/// library already gets it right on the launcher, the send button, the attachment remove button and
/// the URL button, all of which carry both attributes. The header row and Stop were simply missed.
/// </para>
///
/// <para>
/// The assertion is deliberately structural rather than a fixed list of selectors: <b>any</b> button
/// the widget renders with no text content must have a non-empty <c>aria-label</c>. A new icon
/// button added later is caught without anyone remembering to extend this file.
/// </para>
/// </summary>
public sealed class AccessibleNameTests : ComponentTestBase
{
    private readonly RecordingStateService _state = new();

    public AccessibleNameTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // Every optional control is switched on, because each one is rendered behind its own
        // flag and a sweep over the default configuration would miss most of them — the voice
        // toggle, the replay-tour button, and both status pills only exist when asked for.
        Widget.EnableVoiceInput      = true;
        Widget.EnableVoiceOutput     = true;
        Widget.EnableOnboardingTour  = true;
        Widget.EnableImageInput      = true;
        Widget.ShowMcpStatus         = true;
        Widget.HasMcpServers         = true;
        Widget.McpServerNames        = ["filesystem", "memory"];
        Widget.ShowA2AStatus         = true;
        Widget.HasRemoteAgents       = true;
        Widget.ShowHostedToolsStatus = true;
        Widget.HostedTools           = MentorHostedTools.WebSearch;
    }

    /// <summary>Every rendered button that shows no text, as "class → aria-label (or null)".</summary>
    private static IReadOnlyList<string> IconOnlyControlsWithoutALabel(IRenderedComponent<ChatWidget> cut)
        => [.. cut.FindAll("button").Concat(cut.FindAll("label"))
            .Where(b => string.IsNullOrWhiteSpace(b.TextContent))
            .Where(b => string.IsNullOrWhiteSpace(b.GetAttribute("aria-label")))
            .Select(b => $"{b.LocalName}.{b.ClassName} (title={b.GetAttribute("title") ?? "none"})")];

    /// <summary>The widget renders the launcher only until it is clicked; the panel is what this file is about.</summary>
    private IRenderedComponent<ChatWidget> OpenWidget()
    {
        var cut = Render<ChatWidget>();
        cut.Find(".bm-fab").Click();
        return cut;
    }

    [Fact]
    public void Every_icon_only_control_in_the_open_widget_has_an_aria_label()
    {
        var cut = OpenWidget();

        IconOnlyControlsWithoutALabel(cut).Should().BeEmpty(
            "an icon-only control named only by title is announced as an unnamed button — " +
            "title is the last resort in the accessible-name computation and several screen " +
            "readers skip it entirely");
    }

    [Fact]
    public void The_stop_button_has_an_accessible_name()
    {
        // Called out on its own because it is the highest-stakes control: Stop is what a user
        // reaches for when a reply is running away, and it only exists while the turn is in
        // flight. It earns its name from visible text rather than aria-label, which is the
        // stronger of the two — the assertion is the property, not the mechanism.
        var cut = OpenWidget();
        _state.NotifyBusyChanged(true);

        var stop = cut.FindAll(".bm-stop-btn");
        stop.Should().NotBeEmpty("the Stop button renders while the assistant is busy");

        var name = stop[0].GetAttribute("aria-label") ?? stop[0].TextContent;
        name.Should().NotBeNullOrWhiteSpace(
            "a control with neither visible text nor aria-label is announced as an unnamed button");
    }

    [Fact]
    public void The_controls_that_already_had_one_keep_it()
    {
        // The launcher and the send button were correct before this change and are the pattern the
        // rest is being brought up to. A "fix" that moved labels around instead of adding them
        // would pass the sweep above and fail here.
        var cut = OpenWidget();

        cut.Find(".bm-fab").GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
        cut.Find(".bm-send-btn").GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Does nothing; this file is about markup, not turns.</summary>
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
