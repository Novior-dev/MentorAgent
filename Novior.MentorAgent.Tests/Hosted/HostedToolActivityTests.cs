using FluentAssertions;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Hosted;

/// <summary>
/// <c>ShowHostedToolActivity</c> — the "🔍 Searching the web…" line, and the one thing that must
/// <b>not</b> travel with it.
///
/// <para>
/// The flag is a display setting: a host that finds the feedback line noisy turns it off. What it
/// must not also turn off is the <b>session budget</b>, because the reporter is the only place a
/// provider-side call is ever counted — such a call never reaches the function-calling middleware
/// and never reaches the metrics. If the counter lived behind the same <c>if</c> as the label, then
/// switching off a cosmetic line would silently disable <c>MaxHostedToolCallsPerSession</c>, and the
/// only evidence would be the provider's invoice.
/// </para>
///
/// <para>
/// Same shape as the <c>RequireConfirmation</c> question in <c>DocumentedSwitchTests</c>: one flag,
/// two responsibilities, and only one of them is the host's to switch off.
/// </para>
/// </summary>
public sealed class HostedToolActivityTests
{
    private static (HostedToolActivityReporter Reporter, RecordingStateService State, HostedToolBudget Budget)
        Build(bool showActivity)
    {
        var state = new RecordingStateService();
        var budget = new HostedToolBudget();
        var options = new Novior.MentorAgent.Options.MentorOptions
        {
            AppName = "TestApp",
            ScanAssemblies = [],
            ShowHostedToolActivity = showActivity,
        };

        var reporter = new HostedToolActivityReporter(
            state,
            new MentorLocalizer(MentorLanguage.English),
            options,
            new TestLoggerProvider(new TestLogCollector()).CreateLogger("test"),
            budget);

        return (reporter, state, budget);
    }

    private static AIContent WebSearchCall(string callId)
        => new WebSearchToolCallContent(callId);

    // ── The feedback line ───────────────────────────────────────────────────────

    [Fact]
    public void With_the_line_on_a_hosted_call_is_announced_to_the_widget()
    {
        var (reporter, state, _) = Build(showActivity: true);

        reporter.Handle(WebSearchCall("c1"));

        state.Sequence.Should().Contain("NotifyActionExecuting");
    }

    [Fact]
    public void With_the_line_off_nothing_is_announced()
    {
        var (reporter, state, _) = Build(showActivity: false);

        reporter.Handle(WebSearchCall("c1"));

        state.Sequence.Should().NotContain("NotifyActionExecuting");
    }

    [Fact]
    public void The_line_is_on_by_default()
    {
        // A hosted tool can take several seconds. Without the line the widget looks frozen, so the
        // default is to explain what is happening.
        new Novior.MentorAgent.Options.MentorOptions { AppName = "x", ScanAssemblies = [] }
            .ShowHostedToolActivity.Should().BeTrue();
    }

    // ── The budget is not a display concern ─────────────────────────────────────

    [Fact]
    public void Turning_the_line_off_does_not_stop_the_session_budget_counting()
    {
        // The property this file exists for. The reporter is the only place a provider-side call is
        // counted at all; if the counter sat behind the display flag, switching off a cosmetic line
        // would disable MaxHostedToolCallsPerSession and the first evidence would be the invoice.
        var (reporter, _, budget) = Build(showActivity: false);

        reporter.Handle(WebSearchCall("c1"));
        reporter.Handle(WebSearchCall("c2"));

        budget.Used.Should().Be(2, "spend is metered whether or not the user is told about it");
    }

    [Fact]
    public void The_budget_counts_the_same_with_the_line_on()
    {
        // The control: the two paths must agree, or the cap would depend on a display setting.
        var (reporter, _, budget) = Build(showActivity: true);

        reporter.Handle(WebSearchCall("c1"));
        reporter.Handle(WebSearchCall("c2"));

        budget.Used.Should().Be(2);
    }

    // ── De-duplication ──────────────────────────────────────────────────────────

    [Fact]
    public void A_streamed_call_repeated_across_updates_is_counted_once()
    {
        // Streaming repeats the same call part on every update. Counting each fragment would exhaust
        // a session budget in a single search and withdraw the capability mid-conversation.
        var (reporter, state, budget) = Build(showActivity: true);

        reporter.Handle(WebSearchCall("same-call"));
        reporter.Handle(WebSearchCall("same-call"));
        reporter.Handle(WebSearchCall("same-call"));

        budget.Used.Should().Be(1, "one call id is one provider call");
        state.Log.Count(e => e.Event == "NotifyActionExecuting")
             .Should().Be(1, "and the user is told once, not three times");
    }

    [Fact]
    public void Two_distinct_calls_are_two()
    {
        // The control for the de-duplication: it must key on the call id, not swallow everything
        // after the first.
        var (reporter, _, budget) = Build(showActivity: true);

        reporter.Handle(WebSearchCall("a"));
        reporter.Handle(WebSearchCall("b"));

        budget.Used.Should().Be(2);
    }

    // ── Completion ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_completion_event_follows_the_display_flag_too()
    {
        // Both halves of the pair have to agree: announcing a start without ever completing it would
        // leave the widget's activity line stuck on the last hosted tool for the rest of the session.
        var (onReporter, onState, _) = Build(showActivity: true);
        onReporter.Handle(new WebSearchToolResultContent("c1"));
        onState.Sequence.Should().Contain("NotifyActionCompleted");

        var (offReporter, offState, _) = Build(showActivity: false);
        offReporter.Handle(new WebSearchToolResultContent("c1"));
        offState.Sequence.Should().NotContain("NotifyActionCompleted");
    }
}
