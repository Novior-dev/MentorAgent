using System.Reflection;
using Bunit;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// The widget itself — the always-active behaviours the core README promises every host gets for
/// free, and the layout invariant behind a regression that already shipped once.
/// </summary>
public sealed class ChatWidgetTests : ComponentTestBase
{
    private readonly RecordingStateService _state = new();
    private readonly StubOrchestrator _orchestrator = new();

    public ChatWidgetTests()
    {
        Services.AddSingleton<IMentorStateService>(_state);
        Services.AddSingleton<IMentorOrchestrator>(_orchestrator);
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    }

    private IRenderedComponent<ChatWidget> RenderWidget() => Render<ChatWidget>();

    /// <summary>Drives one assistant reply through the state service, as the orchestrator would.</summary>
    private void CompleteAReply(string text = "done")
    {
        _state.NotifyStreamingChunk(text);
        _state.NotifyStreamingCompleted();
    }

    // ── CardTemplate: a templated card keeps working buttons (rc.11 README audit) ─

    [Fact]
    public void A_CardTemplate_that_falls_back_to_MentorCardView_keeps_working_buttons()
    {
        // The documented template falls back to <MentorCardView Card="card" /> for the kinds it does
        // not render itself. The widget's button handler is private and was wired only to the card it
        // renders when there is no template, so every templated button rendered and did nothing.
        var cut = Render<ChatWidget>(p => p.Add(x => x.CardTemplate, (MentorCard card) => builder =>
        {
            builder.OpenComponent<MentorCardView>(0);
            builder.AddAttribute(1, nameof(MentorCardView.Card), card);
            builder.CloseComponent();
        }));

        _state.NotifyStreamingChunk("Ecco l'ordine.");
        _state.NotifyCardsReady([new MentorCard("order")
        {
            Title   = "Ordine #1002",
            Actions = [new("Apri", MentorCardActionKind.Navigate, "/orders/1002")],
        }]);
        _state.NotifyStreamingCompleted();

        cut.WaitForAssertion(() => cut.FindAll(".bm-card .bm-btn").Should().ContainSingle());
        cut.Find(".bm-card .bm-btn").Click();

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri
            .Should().EndWith("/orders/1002", "the widget's dispatcher reaches the templated card");
    }

    // ── The header-overlap regression (already shipped once) ────────────────────

    [Fact]
    public void The_status_pills_are_a_sibling_of_the_header_actions_not_a_child()
    {
        // This is the whole fix. Nested inside .bm-header__actions the pills can only ever compete
        // with the title for one row, and at the default 400px panel width avatar + name + two
        // pills + four buttons do not fit — the name wrapped to three lines and slid under the
        // badges. As a sibling the header is free to wrap and give the pills their own row.
        //
        // bUnit does not compute layout, so this asserts the structure the CSS depends on rather
        // than the pixels. That is the durable half: the pixels were only ever wrong because the
        // structure was.
        Widget.ShowMcpStatus = true;
        Widget.HasMcpServers = true;
        Widget.ShowA2AStatus = true;
        Widget.HasRemoteAgents = true;
        Widget.RemoteAgentDisplays = [new AgentDisplayInfo { Name = "Billing", AgentCardUrl = "https://b.test/c" }];
        _state.NotifyMcpServerStatusChanged("files", true);

        var cut = RenderWidget();

        var header = cut.Find(".bm-header");

        // Compared by class rather than by reference: bUnit hands out a fresh wrapper per query,
        // so two lookups of the same node are never the same object.
        (cut.Find(".bm-status-pills").ParentElement!.GetAttribute("class") ?? "")
            .Should().Contain("bm-header", "the pills must be a direct child of the header");
        header.QuerySelector(".bm-header__actions")!.QuerySelector(".bm-status-pills")
            .Should().BeNull("nesting the pills inside the action buttons is the regression");
    }

    [Fact]
    public void The_header_name_and_the_status_pills_are_in_different_containers()
    {
        Widget.ShowA2AStatus = true;
        Widget.HasRemoteAgents = true;
        Widget.RemoteAgentDisplays = [new AgentDisplayInfo { Name = "Billing", AgentCardUrl = "https://b.test/c" }];

        var cut = RenderWidget();

        cut.Find(".bm-header__info").QuerySelector(".bm-status-pills")
            .Should().BeNull("a pill inside the info block overlaps the bot name directly");
    }

    [Fact]
    public void No_pill_row_is_emitted_when_there_is_nothing_to_show()
    {
        // An empty .bm-status-pills still claims its gap in the flex row.
        var cut = RenderWidget();

        cut.FindAll(".bm-status-pills").Should().BeEmpty();
    }

    [Fact]
    public void The_pill_row_appears_as_soon_as_any_one_integration_is_active()
    {
        Widget.ShowA2AStatus = true;
        Widget.HasRemoteAgents = true;
        Widget.RemoteAgentDisplays = [new AgentDisplayInfo { Name = "Billing", AgentCardUrl = "https://b.test/c" }];

        RenderWidget().FindAll(".bm-status-pills").Should().HaveCount(1);
    }

    // ── Hosted-tools badge (the fourth badge; it lives inline in the widget) ────

    [Fact]
    public void The_hosted_badge_names_a_single_tool_and_counts_several()
    {
        Widget.ShowHostedToolsStatus = true;
        Widget.HostedTools = MentorHostedTools.WebSearch;

        RenderWidget().Find(".bm-hosted-pill__name").TextContent.Should().NotContain("tools",
            "with one tool its name is more useful than a count");

        Widget.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter;
        RenderWidget().Find(".bm-hosted-pill__name").TextContent.Should().Be("2 tools");
    }

    [Fact]
    public void The_hosted_badge_reflects_what_the_server_declared_over_the_wire()
    {
        // The WASM case: the client cannot know which hosted tools survived the server's
        // fail-closed checks, so the declaration replaces whatever was configured locally.
        Widget.ShowHostedToolsStatus = true;
        Widget.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.FileSearch;

        var cut = RenderWidget();
        cut.Find(".bm-hosted-pill__name").TextContent.Should().Be("2 tools");

        _state.NotifyHostedToolsDeclared(MentorHostedTools.WebSearch);

        cut.Find(".bm-hosted-pill__name").TextContent.Should().NotBe("2 tools",
            "a tool that was dropped server-side must stop being advertised");
    }

    [Fact]
    public void A_server_declaring_no_hosted_tools_removes_the_badge_entirely()
    {
        Widget.ShowHostedToolsStatus = true;
        Widget.HostedTools = MentorHostedTools.WebSearch;
        var cut = RenderWidget();

        _state.NotifyHostedToolsDeclared(MentorHostedTools.None);

        cut.FindAll(".bm-hosted-pill").Should().BeEmpty();
    }

    [Fact]
    public void The_hosted_badge_hides_when_the_option_is_off_or_nothing_is_configured()
    {
        Widget.ShowHostedToolsStatus = false;
        Widget.HostedTools = MentorHostedTools.WebSearch;
        RenderWidget().FindAll(".bm-hosted-pill").Should().BeEmpty();

        Widget.ShowHostedToolsStatus = true;
        Widget.HostedTools = MentorHostedTools.None;
        RenderWidget().FindAll(".bm-hosted-pill").Should().BeEmpty();
    }

    [Fact]
    public void Every_hosted_tool_flag_is_counted_once_when_all_are_enabled()
    {
        // All five flags at once: an omitted arm in the flag walk would under-count silently.
        Widget.ShowHostedToolsStatus = true;
        Widget.HostedTools = MentorHostedTools.WebSearch | MentorHostedTools.CodeInterpreter
            | MentorHostedTools.FileSearch | MentorHostedTools.ImageGeneration | MentorHostedTools.HostedMcp;

        RenderWidget().Find(".bm-hosted-pill__name").TextContent.Should().Be("5 tools");
    }

    // ── Unread badge ────────────────────────────────────────────────────────────

    [Fact]
    public void There_is_no_unread_badge_before_anything_arrives()
    {
        RenderWidget().FindAll(".bm-badge").Should().BeEmpty();
    }

    [Fact]
    public void A_reply_arriving_while_the_widget_is_closed_raises_the_unread_count()
    {
        var cut = RenderWidget();

        CompleteAReply();

        cut.Find(".bm-badge").TextContent.Should().Be("1");
    }

    [Fact]
    public void The_unread_badge_caps_at_nine_plus()
    {
        // Documented as "caps at 9+". Two digits do not fit the badge circle.
        var cut = RenderWidget();

        for (var i = 0; i < 12; i++) CompleteAReply();

        cut.Find(".bm-badge").TextContent.Should().Be("9+");
    }

    [Fact]
    public void Nine_replies_still_show_the_number_itself()
    {
        // The boundary: 9 is the last count shown as a digit.
        var cut = RenderWidget();

        for (var i = 0; i < 9; i++) CompleteAReply();

        cut.Find(".bm-badge").TextContent.Should().Be("9");
    }

    [Fact]
    public void Opening_the_widget_clears_the_unread_count()
    {
        var cut = RenderWidget();
        CompleteAReply();
        CompleteAReply();

        cut.Find(".bm-fab").Click();

        cut.FindAll(".bm-badge").Should().BeEmpty();
    }

    [Fact]
    public void A_reply_arriving_while_the_widget_is_open_never_counts_as_unread()
    {
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();

        CompleteAReply();

        cut.FindAll(".bm-badge").Should().BeEmpty("the user is looking at it");
    }

    // ── Open / close ────────────────────────────────────────────────────────────

    [Fact]
    public void The_panel_is_closed_until_the_fab_is_pressed()
    {
        var cut = RenderWidget();
        (cut.Find(".bm-root").GetAttribute("class") ?? "").Should().NotContain("bm-open");

        cut.Find(".bm-fab").Click();
        (cut.Find(".bm-root").GetAttribute("class") ?? "").Should().Contain("bm-open");
    }

    [Fact]
    public void Pressing_the_fab_again_closes_it()
    {
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();
        cut.Find(".bm-fab").Click();

        (cut.Find(".bm-root").GetAttribute("class") ?? "").Should().NotContain("bm-open");
    }

    [Fact]
    public void The_backdrop_closes_the_panel()
    {
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();

        cut.Find(".bm-backdrop").Click();

        (cut.Find(".bm-root").GetAttribute("class") ?? "").Should().NotContain("bm-open");
    }

    [Fact]
    public void The_closed_fab_shows_the_avatar_and_the_open_one_shows_a_close_icon()
    {
        var cut = RenderWidget();
        cut.FindAll(".bm-fab__avatar-img").Should().HaveCount(1);

        cut.Find(".bm-fab").Click();
        cut.FindAll(".bm-fab__avatar-img").Should().BeEmpty(
            "the avatar under a close icon reads as two overlapping buttons");
    }

    [Fact]
    public void The_fab_is_labelled_for_screen_readers()
    {
        Widget.BotName = "Ada";

        var cut = RenderWidget();

        (cut.Find(".bm-fab").GetAttribute("aria-label") ?? "").Should().Contain("Ada");
    }

    // ── Busy state ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_fab_and_header_show_a_busy_state_while_a_turn_is_running()
    {
        var cut = RenderWidget();

        _state.NotifyBusyChanged(true);

        (cut.Find(".bm-fab").GetAttribute("class") ?? "").Should().Contain("bm-fab--busy");
        (cut.Find(".bm-header__status").GetAttribute("class") ?? "").Should().Contain("bm-header__status--busy");
    }

    [Fact]
    public void The_busy_state_clears_when_the_turn_ends()
    {
        var cut = RenderWidget();
        _state.NotifyBusyChanged(true);

        _state.NotifyBusyChanged(false);

        (cut.Find(".bm-fab").GetAttribute("class") ?? "").Should().NotContain("bm-fab--busy");
        (cut.Find(".bm-header__status").GetAttribute("class") ?? "").Should().Contain("bm-header__status--online");
    }

    // ── New conversation (BUG-109, D2) ──────────────────────────────────────────

    private static AngleSharp.Dom.IElement ResetButton(IRenderedComponent<ChatWidget> cut)
        => cut.Find("button[aria-label='New conversation']");

    [Fact]
    public async Task A_reset_that_fails_does_not_escape_the_widget()
    {
        // BUG-109: on WebAssembly the hub throws when it is not connected, and the exception reached the
        // Blazor error bar. A reset the transport could not deliver leaves nothing to undo: the
        // transcript is cleared either way, and the next message opens a fresh conversation.
        _orchestrator.ResetFailure = new InvalidOperationException(
            "The 'InvokeCoreAsync' method cannot be called if the connection is not active");
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();
        CompleteAReply();

        var act = () => ResetButton(cut).ClickAsync(new());

        await act.Should().NotThrowAsync();
        _orchestrator.ResetCalls.Should().Be(1);
        cut.FindAll(".bm-bubble").Should().BeEmpty("the transcript is cleared even when the server was not reached");
    }

    [Fact]
    public void The_reset_button_is_disabled_while_a_reply_is_running()
    {
        // D2: a reset during a turn cleared the transcript and the old reply then landed in the new one.
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();

        _state.NotifyBusyChanged(true);

        ResetButton(cut).HasAttribute("disabled").Should().BeTrue();
        ResetButton(cut).Click();
        _orchestrator.ResetCalls.Should().Be(0, "a click on a disabled button must not reset either");
    }

    [Fact]
    public void The_reset_button_is_disabled_while_a_confirmation_is_pending()
    {
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();

        // As both confirmation flows raise it: inside a busy turn.
        _state.NotifyBusyChanged(true);
        _state.NotifyConfirmationRequired(new ConfirmationRequest
        {
            ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "Annullo l'ordine #1005",
        });

        ResetButton(cut).HasAttribute("disabled").Should().BeTrue(
            "the banner has to be answered first: a reset would leave it hanging over a new conversation");
    }

    [Fact]
    public void A_banner_left_up_by_Stop_does_not_keep_the_reset_disabled()
    {
        // Review of the rc.14 fix: only answering the banner cleared it. After Stop the turn was over, and
        // the button stayed disabled until the user answered a confirmation nobody was waiting for.
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();
        _state.NotifyBusyChanged(true);
        _state.NotifyConfirmationRequired(new ConfirmationRequest
        {
            ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "Annullo l'ordine #1005",
        });

        _state.NotifyBusyChanged(false);   // what Stop does, on every transport

        cut.FindAll(".bm-confirm").Should().BeEmpty();
        ResetButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void The_reset_button_is_disabled_from_the_send_until_the_server_reports_the_turn()
    {
        // Review of the rc.14 fix: on WebAssembly the busy flag comes back over the hub a round trip after
        // the send, and a reset in that gap let the reply land in the new conversation.
        var send = new TaskCompletionSource();
        _orchestrator.PendingSend = send;
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();

        cut.Find("textarea").Input("quanti prodotti ci sono?");
        cut.Find(".bm-send-btn").Click();

        ResetButton(cut).HasAttribute("disabled").Should().BeTrue("the server has not answered yet");

        _state.NotifyBusyChanged(true);
        _state.NotifyBusyChanged(false);
        send.SetResult();

        cut.WaitForAssertion(() => ResetButton(cut).HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void The_end_of_the_send_releases_the_reset_even_if_no_turn_was_reported()
    {
        // The guard must never outlive the send: a transport that reported nothing would otherwise leave the
        // button disabled for good.
        var send = new TaskCompletionSource();
        _orchestrator.PendingSend = send;
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();
        cut.Find("textarea").Input("ciao");
        cut.Find(".bm-send-btn").Click();

        send.SetResult();

        cut.WaitForAssertion(() => ResetButton(cut).HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void A_send_that_fails_shows_a_plain_message_and_not_the_exceptions_text()
    {
        // BUG-147: the chat showed the exception's message as it was — a transport's or a server's internal
        // text. It goes to the log; the user reads that the message was not sent.
        var send = new TaskCompletionSource();
        _orchestrator.PendingSend = send;
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();
        cut.Find("textarea").Input("ciao");
        cut.Find(".bm-send-btn").Click();

        send.SetException(new InvalidOperationException("Failed to invoke 'SendMessage' on db-prod-7"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("The message could not be sent. Please try again.");
            cut.Markup.Should().NotContain("db-prod-7");
        });
    }

    [Fact]
    public void The_reset_button_is_enabled_when_nothing_is_pending()
    {
        // Control: the guard does not disable the button for good.
        var cut = RenderWidget();
        cut.Find(".bm-fab").Click();
        _state.NotifyBusyChanged(true);
        _state.NotifyBusyChanged(false);

        ResetButton(cut).HasAttribute("disabled").Should().BeFalse();
        ResetButton(cut).Click();
        _orchestrator.ResetCalls.Should().Be(1);
    }

    // ── Position ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChatPosition.BottomRight, "bm-pos--bottom-right")]
    [InlineData(ChatPosition.BottomLeft, "bm-pos--bottom-left")]
    [InlineData(ChatPosition.TopRight, "bm-pos--top-right")]
    [InlineData(ChatPosition.TopLeft, "bm-pos--top-left")]
    [InlineData(ChatPosition.SideRight, "bm-pos--side-right")]
    [InlineData(ChatPosition.SideLeft, "bm-pos--side-left")]
    public void Every_documented_position_maps_to_its_own_class(ChatPosition position, string expected)
    {
        // All six enumerated: a value with no switch arm falls into the default and silently
        // lands bottom-right, which looks like the option being ignored.
        Widget.Position = position;

        (RenderWidget().Find(".bm-root").GetAttribute("class") ?? "").Should().Contain(expected);
    }

    // ── Theme ───────────────────────────────────────────────────────────────────
    //
    // BUG-213: a theme is a stylesheet of the package that the widget links in its own markup, and a
    // PrimaryColor is set on <html> through the CSSOM. Both used to be a <style> block, which a page
    // without style-src 'unsafe-inline' drops. What each stylesheet holds is in CssVariableContractTests.

    private static string ThemeHref(string file)
        => $"_content/{RepoFiles.Abstractions}/css/themes/{file}.css?v={ChatWidget.AssetVersion}";

    /// <summary>The variables the widget handed to <c>MentorAgent.setRootVars</c>; null when it made no such call.</summary>
    private IReadOnlyDictionary<string, string>? RootVars()
        => JSInterop.Invocations.Where(i => i.Identifier == "MentorAgent.setRootVars").ToList() switch
        {
            [] => null,
            [var call] => (IReadOnlyDictionary<string, string>)call.Arguments[0]!,
            var calls => throw new InvalidOperationException($"setRootVars was called {calls.Count} times."),
        };

    [Theory]
    [InlineData(MentorTheme.Default)]
    [InlineData(MentorTheme.Custom)]
    public void A_theme_that_overrides_nothing_links_no_stylesheet(MentorTheme theme)
    {
        // Default is the stylesheet's own palette, and Custom leaves every variable to the host.
        Widget.Theme = theme;

        var cut = RenderWidget();

        cut.FindAll("link").Should().BeEmpty();
        cut.FindAll("style").Should().BeEmpty();
    }

    [Theory]
    [InlineData(MentorTheme.Dark, "dark")]
    [InlineData(MentorTheme.Minimal, "minimal")]
    public void Dark_and_minimal_link_their_stylesheet_inside_the_widget(MentorTheme theme, string file)
    {
        // In the body, where the <style> block was: after everything in <head>, so its :root values win
        // over the baseline and over a host's :root rule of the same specificity, as before. With the
        // package version, like the widget's own stylesheet (BUG-209), and for every medium.
        Widget.Theme = theme;

        var link = RenderWidget().Find("link");

        link.GetAttribute("rel").Should().Be("stylesheet");
        link.GetAttribute("href").Should().Be(ThemeHref(file));
        link.HasAttribute("media").Should().BeFalse();
    }

    [Fact]
    public void The_auto_theme_links_the_light_stylesheet_for_a_light_system_only()
    {
        // Auto is Default on a dark system and Minimal on a light one: Minimal's own file, for the media
        // that asks for light, so the browser follows the visitor's setting and no script runs.
        Widget.Theme = MentorTheme.Auto;

        var link = RenderWidget().Find("link");

        link.GetAttribute("rel").Should().Be("stylesheet");
        link.GetAttribute("href").Should().Be(ThemeHref("minimal"));
        link.GetAttribute("media").Should().Be("(prefers-color-scheme: light)");
    }

    [Fact]
    public void Without_a_primary_colour_the_widget_sets_no_variable()
    {
        Widget.PrimaryColor = null;

        RenderWidget();

        RootVars().Should().BeNull();
    }

    [Fact]
    public void A_hex_primary_colour_sets_the_accent_and_derives_the_four_dependent_variables()
    {
        Widget.PrimaryColor = "#3b82f6";

        RenderWidget();

        RootVars().Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["--bm-accent"]        = "#3b82f6",
            ["--bm-accent-dark"]   = "rgb(48,106,201)",
            ["--bm-accent-glow"]   = "rgba(59,130,246,.35)",
            ["--bm-border-accent"] = "rgba(59,130,246,.3)",
            ["--bm-shadow-fab"]    = "0 8px 32px rgba(59,130,246,.4),0 2px 8px rgba(0,0,0,.5)",
        });
    }

    [Fact]
    public void A_non_hex_primary_colour_sets_the_accent_but_derives_nothing()
    {
        // A named colour or a CSS function cannot be decomposed into channels. Setting the accent
        // and skipping the derived shades degrades; guessing would produce a clashing palette.
        Widget.PrimaryColor = "rebeccapurple";

        RenderWidget();

        RootVars().Should().BeEquivalentTo(new Dictionary<string, string> { ["--bm-accent"] = "rebeccapurple" });
    }

    [Fact]
    public void A_three_digit_hex_sets_the_accent_but_derives_nothing()
    {
        // Documented in the core README: "Only 6-digit hex is parsed for the derived shades."
        // #f00 is valid CSS and a common shorthand, so this pins the limitation where a reader
        // will trip over it — the accent applies, the glow and shadow keep the theme's values.
        Widget.PrimaryColor = "#f00";

        RenderWidget();

        RootVars().Should().BeEquivalentTo(new Dictionary<string, string> { ["--bm-accent"] = "#f00" });
    }

    [Fact]
    public void The_primary_colour_reaches_the_page_through_the_cssom()
    {
        // style.setProperty on <html>: the :root scope the <style> block had, and a way of writing a style
        // that a policy without 'unsafe-inline' allows. A setAttribute('style', …) would be refused.
        var script = File.ReadAllText(Path.Combine(
            RepoFiles.Root, RepoFiles.Abstractions, "wwwroot", "js", "MentorAgent.js"));

        var function = System.Text.RegularExpressions.Regex.Match(script, @"setRootVars:\s*\(vars\)\s*=>\s*\{(?<body>[^}]*)\}");

        function.Success.Should().BeTrue("MentorAgent.js should define the setRootVars the widget calls");
        function.Groups["body"].Value.Should().Contain("document.documentElement.style").And.Contain(".setProperty(");
    }

    // ── Assets ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_stylesheet_and_script_carry_the_package_version_as_their_cache_buster()
    {
        // BUG-209: neither file is fingerprinted, and a host serving them as classic static files sends
        // no Cache-Control, so the browser may reuse its copy without asking. A hand-bumped ?v=10 that
        // never moved gave an upgraded visitor the previous release's CSS and JS. <HeadContent> renders
        // into the page's HeadOutlet, so the widget is rendered next to one, as a host renders it.
        var cut = Render(b =>
        {
            b.OpenComponent<Microsoft.AspNetCore.Components.Web.HeadOutlet>(0);
            b.CloseComponent();
            b.OpenComponent<ChatWidget>(1);
            b.CloseComponent();
        });

        var built = typeof(ChatWidget).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var version = System.Xml.Linq.XDocument.Load(Path.Combine(RepoFiles.Root, "Directory.Build.props"))
            .Descendants("Version").Single().Value.Trim();
        built.Should().StartWith(version, "the informational version is the package version, plus the commit");

        cut.Find("link[rel=stylesheet]").GetAttribute("href").Should()
            .Be("_content/Novior.MentorAgent.Abstractions/css/MentorAgent.css?v=" + built);
        cut.Find("script").GetAttribute("src").Should()
            .Be("_content/Novior.MentorAgent.Abstractions/js/MentorAgent.js?v=" + built);
    }

    /// <summary>
    /// The orchestrator is not the subject here: the widget's own behaviour is driven through
    /// <see cref="IMentorStateService"/>, which is how the real one talks to it.
    /// </summary>
    private sealed class StubOrchestrator : IMentorOrchestrator
    {
        public Task SendMessageAsync(string userMessage, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default)
            => PendingSend?.Task ?? Task.CompletedTask;

        /// <summary>When set, a send lasts until the test completes it — as a hub invocation lasts the turn.</summary>
        internal TaskCompletionSource? PendingSend { get; set; }

        public void CancelCurrentRequest() { }

        internal int ResetCalls { get; private set; }

        internal Exception? ResetFailure { get; set; }

        public Task ResetSessionAsync(CancellationToken ct = default)
        {
            ResetCalls++;
            return ResetFailure is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }

        public Task<System.Text.Json.JsonElement?> SerializeSessionAsync(CancellationToken ct = default)
            => Task.FromResult<System.Text.Json.JsonElement?>(null);

        public Task RestoreSessionAsync(System.Text.Json.JsonElement snapshot, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RespondToApprovalAsync(ConfirmationRequest req, bool approved, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
