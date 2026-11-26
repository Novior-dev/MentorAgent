using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Scanner.Cdp;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Docs;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// Story 750 — the widget measured with axe-core (WCAG 2.1 A and AA) in a real browser (Edge or Chrome, headless): the
/// real <see cref="ChatWidget"/>, interactive on Blazor Server, with its stylesheet and script, in a page that is itself
/// clean, so every violation is the widget's. Slice A measured each state and kept what it found as known; slice B fixed
/// them, so none is known now: closed, open, after a streamed reply, asking for a confirmation, showing the tour, in
/// Japanese, and in every theme over a light and a dark page, as the panel's contrast no longer depends on the page.
/// And what axe does not see (EN 301 549): the conversation is a log a screen reader announces, busy while a reply
/// streams; Esc closes the innermost layer first and never answers a confirmation; the focus follows the panel; the
/// closed panel is inert; less motion when asked; a narrow screen without horizontal scroll, the launcher off the input.
/// The browser is asked for reduced motion, so nothing is measured half drawn. axe-core 4.10.3 (MPL-2.0) is a fixture of
/// the tests only, in <c>FrontEnd/</c>.
/// </summary>
public sealed class AccessibilityBrowserTests
{
    private static async Task AssertCleanAsync(WidgetSite site, string state)
    {
        var found = await site.AxeAsync();
        found.Should().BeEmpty($"the widget meets WCAG 2.1 A and AA in the state '{state}'");
    }

    // ── axe, state by state ───────────────────────────────────────────────────────

    [BrowserFact]
    public async Task The_closed_widget_has_no_violation()
    {
        await using var site = await WidgetSite.OpenAsync();
        await AssertCleanAsync(site, "closed");
    }

    [BrowserFact]
    public async Task The_open_widget_has_no_violation()
    {
        await using var site = await WidgetSite.OpenAsync();
        await site.OpenPanelAsync();
        await AssertCleanAsync(site, "open");
    }

    [BrowserFact]
    public async Task A_streamed_reply_has_no_violation()
    {
        await using var site = await WidgetSite.OpenAsync();
        site.Chat.ChunkDelay = TimeSpan.FromMilliseconds(20);
        site.Chat.Say("Here are the open orders: **1001** for Anna Rossi and **1002** for Luca Bianchi.");
        await site.OpenPanelAsync();

        await site.SendAsync("Which orders are open?");
        await site.UntilAsync("document.querySelector('.bm-messages').textContent.includes('Luca Bianchi')", "the streamed reply did not arrive");

        await AssertCleanAsync(site, "streamed");
    }

    [BrowserFact]
    public async Task The_confirmation_banner_has_no_violation()
    {
        await using var site = await WidgetSite.OpenAsync();
        site.Chat.CallTool("issue_refund", new { orderId = 1001 }).Say("The refund is issued.");
        await site.OpenPanelAsync();

        await site.SendAsync("Refund order 1001");
        await site.UntilAsync("!!document.querySelector('.bm-confirm')", "the confirmation banner did not appear");

        await AssertCleanAsync(site, "confirmation");
    }

    [BrowserFact]
    public async Task The_tour_has_no_violation()
    {
        await using var site = await WidgetSite.OpenAsync(o => o.EnableOnboardingTour = true);
        await site.OpenPanelAsync();
        await site.UntilAsync("!!document.querySelector('.bm-tour')", "the tour did not open on a fresh profile");

        await AssertCleanAsync(site, "tour");
    }

    [BrowserFact]
    public async Task The_widget_in_Japanese_has_no_violation()
    {
        await using var site = await WidgetSite.OpenAsync(o => o.Language = MentorLanguage.Japanese);
        await site.OpenPanelAsync();

        await AssertCleanAsync(site, "japanese");
    }

    [BrowserTheory]
    [InlineData(MentorTheme.Default, false)]
    [InlineData(MentorTheme.Default, true)]
    [InlineData(MentorTheme.Dark, false)]
    [InlineData(MentorTheme.Dark, true)]
    [InlineData(MentorTheme.Minimal, false)]
    [InlineData(MentorTheme.Minimal, true)]
    public async Task Every_theme_has_no_violation_over_a_light_or_a_dark_page(MentorTheme theme, bool darkPage)
    {
        await using var site = await WidgetSite.OpenAsync(o => o.Theme = theme);
        if (darkPage) await site.TextAsync("document.body.style.background = '#000'; document.body.style.color = '#fff'; 'ok'");
        site.Chat.Say("Here are the open orders: **1001** for Anna Rossi and **1002** for Luca Bianchi.");
        await site.OpenPanelAsync();
        await AssertCleanAsync(site, $"{theme}, open, {(darkPage ? "dark" : "light")} page");

        await site.SendAsync("Which orders are open?");
        await site.UntilAsync("document.querySelector('.bm-messages').textContent.includes('Luca Bianchi')", "the reply did not arrive");
        await AssertCleanAsync(site, $"{theme}, after a reply, {(darkPage ? "dark" : "light")} page");
    }

    [BrowserTheory]
    [InlineData(MentorTheme.Default, false)]
    [InlineData(MentorTheme.Default, true)]
    [InlineData(MentorTheme.Dark, false)]
    [InlineData(MentorTheme.Dark, true)]
    [InlineData(MentorTheme.Minimal, false)]
    [InlineData(MentorTheme.Minimal, true)]
    public async Task The_header_says_its_status_in_words_and_reads_online_and_busy_in_every_theme(MentorTheme theme, bool darkPage)
    {
        // The status is said in words, the dot only repeats them (WCAG 1.4.1); online (the open state above) and busy
        // are both read, in their colours, on every theme's panel.
        await using var site = await WidgetSite.OpenAsync(o => o.Theme = theme);
        if (darkPage) await site.TextAsync("document.body.style.background = '#000'; document.body.style.color = '#fff'; 'ok'");
        site.Chat.ChunkDelay = TimeSpan.FromMilliseconds(300);
        site.Chat.HonourCancellation = true;
        site.Chat.Say(string.Join(' ', Enumerable.Repeat("word", 60)));
        await site.OpenPanelAsync();

        (await site.TextAsync("document.querySelector('.bm-header__status').textContent.trim()")).Should().NotBeNullOrEmpty("the status is said in words");
        (await site.TextAsync("document.querySelector('.bm-status-dot').getAttribute('aria-hidden')")).Should().Be("true", "the dot only repeats the words");

        await site.SendAsync("Tell me something long");
        await site.UntilAsync("!!document.querySelector('.bm-header__status--busy')", "the header did not say it was busy");
        await AssertCleanAsync(site, $"{theme}, busy, {(darkPage ? "dark" : "light")} page");
        (await site.TextAsync("String(!!document.querySelector('.bm-header__status--busy'))")).Should().Be("true", "it was measured busy");

        await site.TextAsync("document.querySelector('.bm-stop-btn') && document.querySelector('.bm-stop-btn').click(); 'ok'");
    }

    // ── What axe does not see ─────────────────────────────────────────────────────

    [BrowserFact]
    public async Task The_closed_panel_is_inert_and_the_open_one_is_not()
    {
        await using var site = await WidgetSite.OpenAsync();
        (await site.TextAsync("String(document.querySelector('.bm-panel').hasAttribute('inert'))")).Should().Be("true",
            "closed, its controls are out of the tab order and of what a screen reader reads");

        await site.OpenPanelAsync();
        (await site.TextAsync("String(document.querySelector('.bm-panel').hasAttribute('inert'))")).Should().Be("false");
    }

    [BrowserFact]
    public async Task Opened_the_focus_is_in_the_field_and_Esc_closes_the_panel_and_gives_it_back_to_the_button()
    {
        await using var site = await WidgetSite.OpenAsync();
        await site.OpenPanelAsync();
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-input')", "the focus did not go to the field");

        await site.PressAsync(".bm-input", "Escape");

        await site.UntilAsync("!document.querySelector('.bm-root').classList.contains('bm-open')", "Esc did not close the panel");
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-fab')", "the focus did not go back to the button");
    }

    [BrowserTheory]
    [InlineData(320, 640)]    // the launcher is hidden while the panel is open
    [InlineData(1280, 800)]
    public async Task Closed_with_Esc_or_its_close_button_the_panel_gives_the_focus_back_to_the_launcher_shown_again(int width, int height)
    {
        // An element with display:none takes no focus: the focus goes back to the launcher once it is shown again, not to
        // the page's body.
        const string onTheLauncher = "document.activeElement && document.activeElement.classList.contains('bm-fab') && document.activeElement.getClientRects().length > 0";
        await using var site = await WidgetSite.OpenAsync();
        await site.ViewportAsync(width, height);

        await site.OpenPanelAsync();
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-input')", "opened, the focus is not in the field");
        await site.PressAsync(".bm-input", "Escape");
        await site.UntilAsync(onTheLauncher, "closed with Esc, the focus is not on the launcher shown again");

        await site.OpenPanelAsync();
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-input')", "opened again, the focus is not in the field");
        // The panel's X, reached with the keyboard as a user does: focused, then pressed.
        await site.TextAsync("(() => { const x = document.querySelector('.bm-panel button.bm-icon-btn[aria-label=\"Close\"]'); x.focus(); x.click(); return 'ok'; })()");
        await site.UntilAsync(onTheLauncher, "closed with its X, the focus is not on the launcher shown again");
    }

    [BrowserFact]
    public async Task A_navigation_with_the_panel_open_leaves_the_focus_where_the_user_put_it()
    {
        // The focus goes to the field when the user opens the panel, never when the panel is open across pages: a
        // keyboard user would have it taken from the page at every navigation.
        await using var site = await WidgetSite.OpenAsync();
        await site.OpenPanelAsync();
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-input')", "opened, the focus is not in the field");
        await site.TextAsync("document.getElementById('page-button').focus(); 'ok'");

        site.Circuit.GetRequiredService<NavigationManager>().NavigateTo($"/recipes/widget?run={site.Run}&page=2");
        await site.UntilAsync("location.search.includes('page=2')", "the page did not navigate");
        await Task.Delay(500);

        (await site.TextAsync("document.activeElement ? (document.activeElement.id || document.activeElement.tagName + '.' + document.activeElement.className) : ''")).Should().Be("page-button",
            "the widget took the focus from the page");
        (await site.TextAsync("String(document.querySelector('.bm-root').classList.contains('bm-open'))")).Should().Be("true");
    }

    [BrowserFact]
    public async Task Esc_closes_the_tour_first_and_the_panel_stays_open()
    {
        await using var site = await WidgetSite.OpenAsync(o => o.EnableOnboardingTour = true);
        await site.OpenPanelAsync();
        await site.UntilAsync("!!document.querySelector('.bm-tour')", "the tour did not open");

        await site.PressAsync(".bm-panel", "Escape");

        await site.UntilAsync("!document.querySelector('.bm-tour')", "Esc did not close the tour");
        (await site.TextAsync("String(document.querySelector('.bm-root').classList.contains('bm-open'))")).Should().Be("true");
    }

    [BrowserFact]
    public async Task Esc_closes_the_image_URLs_field_first_and_the_panel_stays_open()
    {
        await using var site = await WidgetSite.OpenAsync(o => o.EnableImageInput = true);
        await site.OpenPanelAsync();
        await site.TextAsync("[...document.querySelectorAll('.bm-attach-btn')].pop().click(); 'ok'");
        await site.UntilAsync("!!document.querySelector('.bm-attach-url__input')", "the URL's field did not open");

        await site.PressAsync(".bm-attach-url__input", "Escape");

        await site.UntilAsync("!document.querySelector('.bm-attach-url__input')", "Esc did not close the URL's field");
        (await site.TextAsync("String(document.querySelector('.bm-root').classList.contains('bm-open'))")).Should().Be("true");
    }

    [BrowserFact]
    public async Task Esc_never_answers_a_waiting_confirmation_which_is_there_again_when_the_panel_opens()
    {
        await using var site = await WidgetSite.OpenAsync();
        site.Chat.CallTool("issue_refund", new { orderId = 1001 }).Say("The refund is issued.");
        await site.OpenPanelAsync();
        await site.SendAsync("Refund order 1001");
        await site.UntilAsync("!!document.querySelector('.bm-confirm')", "the confirmation banner did not appear");

        await site.PressAsync(".bm-panel", "Escape");
        await site.UntilAsync("!document.querySelector('.bm-root').classList.contains('bm-open')", "Esc did not close the panel");
        await site.OpenPanelAsync();

        (await site.TextAsync("String(!!document.querySelector('.bm-confirm'))")).Should().Be("true", "closing the panel decides nothing");
        site.Observer.Seen.Should().NotContain(d => d.ToolName == "issue_refund"
            && (d.Outcome == MentorActionOutcome.Rejected || d.Outcome == MentorActionOutcome.Executed || d.Outcome == MentorActionOutcome.Stopped));
        site.Circuit.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
    }

    [BrowserFact]
    public async Task The_conversation_is_a_log_busy_while_a_reply_streams_and_no_longer_once_it_ends()
    {
        await using var site = await WidgetSite.OpenAsync();
        (await site.TextAsync("[...document.querySelector('.bm-messages').attributes].filter(a => ['role','aria-live','tabindex'].includes(a.name)).map(a => a.name + '=' + a.value).sort().join(' ')"))
            .Should().Be("aria-live=polite role=log tabindex=0", "a screen reader announces it, and the keyboard reaches it");
        site.Chat.ChunkDelay = TimeSpan.FromMilliseconds(150);
        site.Chat.Say("A reply long enough to be streamed for a while, word after word, so that the test sees it busy.");
        await site.OpenPanelAsync();

        await site.SendAsync("Tell me something");

        await site.UntilAsync("document.querySelector('.bm-messages').getAttribute('aria-busy') === 'true'", "the log was not busy while the reply streamed");
        await site.UntilAsync("document.querySelector('.bm-messages').getAttribute('aria-busy') === 'false'", "the log stayed busy after the reply");
    }

    [BrowserFact]
    public async Task A_reply_that_fails_leaves_the_log_no_longer_busy()
    {
        await using var site = await WidgetSite.OpenAsync();
        site.Chat.ThrowOnNextCall = new InvalidOperationException("the provider is down");
        await site.OpenPanelAsync();

        await site.SendAsync("Tell me something");

        await site.UntilAsync("document.querySelector('.bm-messages').textContent.includes('⚠') || document.querySelectorAll('.bm-bubble--assistant').length > 0",
            "the failure did not reach the conversation");
        await site.UntilAsync("document.querySelector('.bm-messages').getAttribute('aria-busy') === 'false'", "the log stayed busy after a failure");
    }

    [BrowserFact]
    public async Task A_reply_stopped_leaves_the_log_no_longer_busy()
    {
        await using var site = await WidgetSite.OpenAsync();
        site.Chat.ChunkDelay = TimeSpan.FromMilliseconds(300);
        site.Chat.HonourCancellation = true;
        site.Chat.Say(string.Join(' ', Enumerable.Repeat("word", 60)));
        await site.OpenPanelAsync();
        await site.SendAsync("Tell me something long");
        await site.UntilAsync("!!document.querySelector('.bm-stop-btn')", "no Stop button while the reply streamed");

        await site.TextAsync("document.querySelector('.bm-stop-btn').click(); 'ok'");

        await site.UntilAsync("document.querySelector('.bm-messages').getAttribute('aria-busy') === 'false'", "the log stayed busy after Stop");
    }

    [BrowserFact]
    public async Task Asked_for_less_motion_nothing_in_the_widget_moves()
    {
        await using var site = await WidgetSite.OpenAsync();
        await site.OpenPanelAsync();

        (await site.TextAsync("String(document.getAnimations().filter(a => a.effect && a.effect.target && a.effect.target.closest && a.effect.target.closest('.bm-root') && a.effect.getComputedTiming().duration > 1).length)"))
            .Should().Be("0", "the browser asked for reduced motion: no animation or transition of the widget lasts");
    }

    [BrowserTheory]
    [InlineData(320, 640, ChatPosition.BottomRight)]
    [InlineData(320, 640, ChatPosition.TopLeft)]
    [InlineData(320, 640, ChatPosition.SideRight)]   // its rules set its width and height with !important too
    [InlineData(640, 800, ChatPosition.BottomRight)]   // a 1280 px window at 200% zoom
    public async Task On_a_narrow_screen_the_open_panel_fits_without_horizontal_scroll_and_the_launcher_leaves_it(int width, int height, ChatPosition position)
    {
        // BUG-253: under 480 px the position's rules, with !important, kept the panel 72 px from the screen's edge, and
        // the launcher stayed over the panel.
        await using var site = await WidgetSite.OpenAsync(o => o.Position = position);
        await site.ViewportAsync(width, height);
        await site.OpenPanelAsync();
        await site.UntilAsync("document.getAnimations().filter(a => a.playState === 'running' && a.effect && a.effect.getComputedTiming().iterations !== Infinity).length === 0",
            "the panel's opening did not end");   // measured half open, the panel is lower than where it stays

        (await site.TextAsync($"String(document.documentElement.scrollWidth <= {width})")).Should().Be("true", "WCAG 1.4.10: no horizontal scroll");
        (await site.TextAsync($"(() => {{ const r = document.querySelector('.bm-panel').getBoundingClientRect(); return [r.left, r.top, r.right, r.bottom].map(Math.round).join(','); }})()"))
            .Should().Match(width < 480 ? $"0,0,{width},{height}" : "*", "under 480 px the panel takes the whole screen");
        (await site.TextAsync($"(() => {{ const r = document.querySelector('.bm-panel').getBoundingClientRect(); return String(r.left >= -0.5 && r.right <= {width} + 0.5 && r.top >= -0.5 && r.bottom <= {height} + 0.5); }})()"))
            .Should().Be("true", "the panel is inside the screen");
        (await site.TextAsync("""
            (() => {
              const fab = document.querySelector('.bm-fab');
              if (!fab || fab.getClientRects().length === 0) return 'clear';
              const a = fab.getBoundingClientRect(), b = document.querySelector('.bm-panel').getBoundingClientRect();
              return a.right <= b.left || a.left >= b.right || a.bottom <= b.top || a.top >= b.bottom ? 'clear' : 'covers';
            })()
            """)).Should().Be("clear", "the launcher does not cover the open panel");
    }

    // ── The defects of the 1059 audit not covered above (A2, A5, A6, A7, A11, A12) ──────────

    [BrowserFact]
    public async Task The_launcher_says_whether_the_panel_is_open_and_names_what_it_does_now()
    {
        await using var site = await WidgetSite.OpenAsync();
        (await site.TextAsync("document.querySelector('.bm-fab').getAttribute('aria-expanded')")).Should().Be("false");
        var panel = await site.TextAsync("document.querySelector('.bm-fab').getAttribute('aria-controls')");
        (await site.TextAsync($"String(!!document.getElementById('{panel}') && document.getElementById('{panel}').classList.contains('bm-panel'))"))
            .Should().Be("true", "it controls the panel");
        var closedName = await site.TextAsync("document.querySelector('.bm-fab').getAttribute('aria-label')");

        await site.OpenPanelAsync();

        (await site.TextAsync("document.querySelector('.bm-fab').getAttribute('aria-expanded')")).Should().Be("true");
        (await site.TextAsync("document.querySelector('.bm-fab').getAttribute('aria-label')")).Should().NotBe(closedName,
            "open, it no longer says it opens (A2)");
    }

    [BrowserFact]
    public async Task After_a_reply_the_focus_is_back_in_the_field_the_message_was_sent_from()
    {
        await using var site = await WidgetSite.OpenAsync();
        site.Chat.ChunkDelay = TimeSpan.FromMilliseconds(100);
        site.Chat.Say("Here are the open orders, one after the other.");
        await site.OpenPanelAsync();

        // Sent with the button, as a keyboard user does with Tab and Enter: the focus leaves the field, which is
        // disabled while the reply comes.
        await site.SendAsync("Which orders are open?", withTheButtonFocused: true);
        await site.UntilAsync("document.querySelector('.bm-messages').textContent.includes('one after the other')", "the reply did not arrive");

        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-input')",
            "the focus did not come back to the field after the reply (A5)");
    }

    [BrowserFact]
    public async Task With_the_widgets_voice_on_after_a_reply_sent_from_the_field_the_focus_is_back_in_the_field()
    {
        // BUG-258: with the voice on, the send first stops what is playing; the render of that await spent the focus on
        // the field before the turn had begun, the field was then disabled, and after the reply the focus was on the body.
        await using var site = await WidgetSite.OpenAsync(o => o.EnableVoiceOutput = true);
        site.Chat.ChunkDelay = TimeSpan.FromMilliseconds(100);
        site.Chat.Say("Here are the open orders, one after the other.");
        await site.OpenPanelAsync();
        await site.UntilAsync("!!document.querySelector('.bm-header__actions .bm-icon-btn--active')", "the widget's voice is not on");

        await site.SendAsync("Which orders are open?");
        await site.UntilAsync("document.querySelector('.bm-messages').textContent.includes('one after the other')", "the reply did not arrive");

        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-input')",
            "with the voice on, the focus did not come back to the field after the reply");
    }

    [BrowserFact]
    public async Task A_widget_whose_script_comes_after_its_first_render_has_its_voice_and_its_colour_at_the_first_load()
    {
        // BUG-261: without a prerender the widget adds its script to the head with its first render, and asked it at once
        // whether the browser speaks and to set the primary colour; a script that came later left both off for the visit.
        await using var site = await WidgetSite.OpenAsync(o => { o.EnableVoiceOutput = true; o.PrimaryColor = "#0a7cff"; },
                                                          scriptDelay: TimeSpan.FromMilliseconds(1500));
        await site.OpenPanelAsync();

        await site.UntilAsync("!!document.querySelector('.bm-header__actions .bm-icon-btn--active')",
            "the widget's voice is not on after its script came late");
        (await site.TextAsync("document.documentElement.style.getPropertyValue('--bm-accent')")).Should().Be("#0a7cff",
            "the primary colour is set once the script is there");
    }

    /// <summary>
    /// BUG-257: what a screen reader would read that is decoration, in the live page: an image with a name, an icon not
    /// hidden or focusable, a button named with an emoji. Empty when there is none.
    /// </summary>
    private static Task<string?> DecorationReadAsync(WidgetSite site)
        => site.TextAsync("""
            (() => {
              const root = document.querySelector('.bm-root'), out = [];
              root.querySelectorAll('img').forEach(i => { if (i.getAttribute('alt') !== '') out.push('img.' + i.className + ' alt=' + i.getAttribute('alt')); });
              root.querySelectorAll('svg').forEach(s => {
                if (s.getAttribute('aria-hidden') !== 'true' || s.getAttribute('focusable') !== 'false') out.push('svg in ' + (s.parentElement ? s.parentElement.className : '?'));
              });
              const words = n => [...n.childNodes].filter(c => !(c.nodeType === 1 && c.getAttribute('aria-hidden') === 'true'))
                                                .map(c => c.nodeType === 1 ? words(c) : c.textContent).join('');
              root.querySelectorAll('button').forEach(b => {
                const name = b.getAttribute('aria-label') || words(b);
                if (/\p{Extended_Pictographic}/u.test(name)) out.push('button ' + name);
              });
              return out.join(' | ');
            })()
            """);

    [BrowserFact]
    public async Task In_the_live_page_nothing_decorative_is_read_and_each_message_says_who_speaks()
    {
        await using var site = await WidgetSite.OpenAsync(o => { o.EnableOnboardingTour = true; o.EnableVoiceOutput = true; o.BotName = "Orders assistant"; });
        await site.OpenPanelAsync();
        await site.UntilAsync("!!document.querySelector('.bm-tour')", "the tour did not open on a fresh profile");
        (await DecorationReadAsync(site)).Should().BeEmpty("in the tour");

        await site.PressAsync(".bm-tour", "Escape");
        await site.UntilAsync("!document.querySelector('.bm-tour') && !!document.querySelector('.bm-chip')", "the welcome did not show");
        (await DecorationReadAsync(site)).Should().BeEmpty("in the welcome");

        site.Chat.CallTool("issue_refund", new { orderId = 1001 }).Say("The refund is issued.");
        await site.SendAsync("Refund order 1001");
        await site.UntilAsync("!!document.querySelector('.bm-confirm')", "the confirmation banner did not appear");
        (await DecorationReadAsync(site)).Should().BeEmpty("with the confirmation");

        await site.TextAsync("document.querySelector('.bm-confirm .bm-btn--primary').click(); 'ok'");
        await site.UntilAsync("document.querySelector('.bm-messages').textContent.includes('The refund is issued.')", "the reply did not arrive");
        (await DecorationReadAsync(site)).Should().BeEmpty("with the reply");

        (await site.TextAsync("[...document.querySelectorAll('.bm-messages .bm-bubble .bm-sr-only')].map(e => e.textContent).join('|')"))
            .Should().StartWith("You:|Orders assistant:", "each message says who speaks, hidden from sight");
    }

    [BrowserFact]
    public async Task A_confirmation_takes_the_focus_on_its_banner_and_Enter_confirms_nothing()
    {
        await using var site = await WidgetSite.OpenAsync();
        site.Chat.CallTool("issue_refund", new { orderId = 1001 }).Say("The refund is issued.");
        await site.OpenPanelAsync();
        await site.SendAsync("Refund order 1001");
        await site.UntilAsync("!!document.querySelector('.bm-confirm')", "the confirmation banner did not appear");

        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-confirm')",
            "the banner did not take the focus (A6)");
        (await site.TextAsync("document.querySelector('.bm-confirm').getAttribute('role')")).Should().Be("alertdialog");
        var desc = await site.TextAsync("document.querySelector('.bm-confirm__desc').textContent.trim()");
        (await site.TextAsync("document.querySelector('.bm-confirm .bm-btn--primary').getAttribute('aria-label')"))
            .Should().EndWith(desc!, "the button says the action it confirms");

        await site.TextAsync("['keydown', 'keypress', 'keyup'].forEach(t => document.activeElement.dispatchEvent(new KeyboardEvent(t, { key: 'Enter', bubbles: true }))); 'ok'");
        await Task.Delay(500);

        (await site.TextAsync("String(!!document.querySelector('.bm-confirm'))")).Should().Be("true", "Enter on the banner confirms nothing");
        site.Observer.Seen.Should().NotContain(d => d.ToolName == "issue_refund" && d.Outcome == MentorActionOutcome.Confirmed);
        site.Circuit.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
    }

    [BrowserFact]
    public async Task A_confirmation_waiting_takes_the_focus_when_the_panel_opens_again_and_answered_gives_it_back_to_the_field()
    {
        await using var site = await WidgetSite.OpenAsync();
        site.Chat.CallTool("issue_refund", new { orderId = 1001 }).Say("The refund was not issued.");
        await site.OpenPanelAsync();
        await site.SendAsync("Refund order 1001");
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-confirm')", "the banner did not take the focus");

        // Closed with Esc, opened again from the launcher with the keyboard: the field is disabled, the banner waits.
        await site.PressAsync(".bm-confirm", "Escape");
        await site.UntilAsync("!document.querySelector('.bm-root').classList.contains('bm-open')", "Esc did not close the panel");
        await site.TextAsync("(() => { const f = document.querySelector('.bm-fab'); f.focus(); f.click(); return 'ok'; })()");
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-confirm')",
            "opened again, the focus did not go to the confirmation still waiting (A6)");

        // Cancel, reached with Tab and pressed: its button goes away, and the focus comes back to the field.
        await site.TextAsync("(() => { const b = document.querySelector('.bm-confirm .bm-btn--ghost'); b.focus(); b.click(); return 'ok'; })()");
        await site.UntilAsync("document.querySelector('.bm-messages').textContent.includes('was not issued')", "the turn did not end");
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-input')",
            "answered, the focus did not come back to the field");
        site.Circuit.GetRequiredService<TestOrderService>().Invoked.Should().BeEmpty();
    }

    [BrowserFact]
    public async Task The_image_button_is_reached_with_the_keyboard_and_named_on_its_input()
    {
        await using var site = await WidgetSite.OpenAsync(o => o.EnableImageInput = true);
        await site.OpenPanelAsync();

        (await site.TextAsync("getComputedStyle(document.querySelector('.bm-attach-file')).display")).Should().NotBe("none",
            "a hidden input is out of the tab order (A7)");
        (await site.TextAsync("String(document.querySelector('.bm-attach-file').tabIndex >= 0)")).Should().Be("true");
        (await site.TextAsync("document.querySelector('.bm-attach-file').getAttribute('aria-label') || ''")).Should().NotBeEmpty();
        (await site.TextAsync("String(document.querySelector('label.bm-attach-btn').hasAttribute('aria-label'))")).Should().Be("false",
            "a label may not have an aria-label");
    }

    [BrowserFact]
    public async Task Tab_and_Shift_Tab_go_round_the_open_tours_buttons_and_the_rest_of_the_panel_is_inert()
    {
        // BUG-262: the tour is a modal dialog; Tab from its last button went out of it, to the panel's microphone and field,
        // which a screen reader no longer saw under aria-modal.
        await using var site = await WidgetSite.OpenAsync(o => { o.EnableOnboardingTour = true; o.EnableVoiceInput = true; });
        await site.OpenPanelAsync();
        await site.UntilAsync("!!document.querySelector('.bm-tour')", "the tour did not open on a fresh profile");
        (await site.TextAsync("String(document.querySelector('.bm-header').inert && document.querySelector('.bm-input-area').inert)"))
            .Should().Be("true", "outside the open tour the panel is inert");

        await site.TextAsync("document.querySelector('.bm-tour__actions .bm-btn--primary').focus(); 'ok'");
        await site.TabAsync();
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-tour__skip')",
            "Tab from the tour's last button did not go to its first");
        await site.TabAsync(shift: true);
        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-btn--primary')",
            "Shift+Tab from the tour's first button did not go to its last");

        await site.PressAsync(".bm-tour", "Escape");
        await site.UntilAsync("!document.querySelector('.bm-tour') && !document.querySelector('.bm-panel [inert]')",
            "closed the tour, the panel is still inert");
    }

    [BrowserFact]
    public async Task The_tour_takes_the_focus_when_it_opens_with_the_panel()
    {
        await using var site = await WidgetSite.OpenAsync(o => o.EnableOnboardingTour = true);
        await site.OpenPanelAsync();
        await site.UntilAsync("!!document.querySelector('.bm-tour')", "the tour did not open");

        await site.UntilAsync("document.activeElement && document.activeElement.classList.contains('bm-tour')", "the tour did not take the focus (A11)");

        await site.PressAsync(".bm-tour", "Escape");
        await site.UntilAsync("!document.querySelector('.bm-tour') && document.activeElement && document.activeElement.classList.contains('bm-input')",
            "closed, the tour did not leave the focus in the field");
    }

    [BrowserTheory]
    [InlineData(MentorLanguage.English, "en")]
    [InlineData(MentorLanguage.Japanese, "ja")]
    public async Task The_widget_says_its_language_also_on_a_page_in_another(MentorLanguage language, string code)
    {
        await using var site = await WidgetSite.OpenAsync(o => o.Language = language);
        (await site.TextAsync("document.documentElement.lang")).Should().Be("en", "the page is in English");

        (await site.TextAsync("document.querySelector('.bm-root').getAttribute('lang')")).Should().Be(code, "A12");
    }
}

/// <summary>A site with the widget on an otherwise empty page, and a browser on it.</summary>
internal sealed class WidgetSite : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    internal required WebApplication App { get; init; }
    internal required CdpBrowser Browser { get; init; }
    internal required FakeChatClient Chat { get; init; }
    internal required string Run { get; init; }
    internal required RecordingActionObserver Observer { get; init; }

    /// <summary>The circuit's services, once the widget's page has rendered.</summary>
    internal IServiceProvider Circuit => WidgetA11yPage.Circuits[Run];

    public async ValueTask DisposeAsync()
    {
        WidgetA11yPage.Circuits.TryRemove(Run, out _);
        await Browser.DisposeAsync();
        await App.DisposeAsync();
    }

    // scriptDelay: how long the widget's MentorAgent.js waits before it is served; it then comes after the widget's first
    // render, as on a slow network (BUG-261).
    internal static async Task<WidgetSite> OpenAsync(Action<MentorOptions>? configure = null, TimeSpan? scriptDelay = null)
    {
        var chat = new FakeChatClient();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        var observer = new RecordingActionObserver();
        builder.Services.AddMentorActionObserver(observer);
        builder.Services.AddScoped<TestOrderService>();   // the application's actions: issue_refund asks for a confirmation
        builder.Services.AddMentorAgent(o =>
        {
            o.AppName = "Orders";
            o.ChatClient = chat;
            o.ScanAssemblies = [typeof(TestOrderService).Assembly];
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = false;
            o.EnableOnboardingTour = false;
            configure?.Invoke(o);
        });

        var app = builder.Build();
        app.UseAntiforgery();
        var blazor = Path.Combine(AppContext.BaseDirectory, "Interactive", "blazor.web.js");
        app.MapGet("/_framework/blazor.web.js", () => Results.File(blazor, "text/javascript"));
        var assets = Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Abstractions", "wwwroot");
        var types = new FileExtensionContentTypeProvider();
        app.MapGet("/_content/Novior.MentorAgent.Abstractions/{**path}", async (string path) =>
        {
            if (scriptDelay is { } delay && path.Equals("js/MentorAgent.js", StringComparison.OrdinalIgnoreCase)) await Task.Delay(delay);
            var file = Path.GetFullPath(Path.Combine(assets, path));
            if (!file.StartsWith(assets, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return Results.NotFound();
            return Results.File(file, types.TryGetContentType(file, out var type) ? type : "application/octet-stream");
        });
        app.MapGet("/axe.min.js", () => Results.File(Path.Combine(RepoFiles.Root, "Novior.MentorAgent.Tests", "FrontEnd", "axe.min.js"), "text/javascript"));
        app.MapRazorComponents<WidgetA11yRoot>().AddInteractiveServerRenderMode();
        await app.StartAsync();

        var run = Guid.NewGuid().ToString("N");
        var browser = await CdpBrowser.StartAsync(CdpBrowser.Find(null)!, CancellationToken.None);
        // Story 750: the widget follows prefers-reduced-motion, so nothing is measured half drawn.
        await browser.SendCommandAsync("Emulation.setEmulatedMedia", new System.Text.Json.Nodes.JsonObject
        {
            ["features"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "prefers-reduced-motion", ["value"] = "reduce",
            }),
        }, CancellationToken.None);
        await browser.OpenAsync(new Uri($"{app.Urls.First()}/recipes/widget?run={run}"), Wait, CancellationToken.None);
        var site = new WidgetSite { App = app, Browser = browser, Chat = chat, Run = run, Observer = observer };
        await site.UntilAsync("!!document.querySelector('.bm-fab')", "the widget did not render");
        return site;
    }

    internal async Task<string?> TextAsync(string expression)
    {
        var value = await Browser.EvaluateAsync(expression, CancellationToken.None);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    internal async Task UntilAsync(string condition, string failure)
    {
        var until = DateTime.UtcNow + Wait;
        while (await TextAsync($"String(!!({condition}))") != "true")
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException(failure + ". The panel reads: "
                    + await TextAsync("document.body.innerText.slice(0, 1500)") + " | at " + await TextAsync("location.href")
                    + " | model calls: " + Chat.Calls.Count + " | decisions: " + string.Join(", ", Observer.Seen.Select(d => d.ToolName + "/" + d.Outcome + "/" + d.ReasonCode)) + " | widget: " + await TextAsync("String(!!document.querySelector('.bm-root'))"));
            await Task.Delay(100);
        }
    }

    /// <summary>Tab, or Shift+Tab, pressed on the keyboard: the browser moves the focus itself (BUG-262).</summary>
    internal async Task TabAsync(bool shift = false)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await Browser.SendCommandAsync("Input.dispatchKeyEvent", new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = type, ["key"] = "Tab", ["code"] = "Tab", ["windowsVirtualKeyCode"] = 9, ["modifiers"] = shift ? 8 : 0,
            }, CancellationToken.None);
    }

    /// <summary>A key pressed on the element, as the keyboard does: keydown, which bubbles to the panel.</summary>
    internal async Task PressAsync(string selector, string key)
        => await TextAsync($"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(selector)}); el.dispatchEvent(new KeyboardEvent('keydown', {{ key: {JsonSerializer.Serialize(key)}, bubbles: true }})); return 'ok'; }})()");

    /// <summary>The page's width and height in CSS pixels, as a phone's or a zoomed window's.</summary>
    internal async Task ViewportAsync(int width, int height)
    {
        await Browser.SendCommandAsync("Emulation.setDeviceMetricsOverride", new System.Text.Json.Nodes.JsonObject
        {
            ["width"] = width, ["height"] = height, ["deviceScaleFactor"] = 1, ["mobile"] = width < 480,
        }, CancellationToken.None);
        await Task.Delay(200);
    }

    /// <summary>Opens the panel with the launcher, as the user does, and waits for its input.</summary>
    internal async Task OpenPanelAsync()
    {
        await TextAsync("document.querySelector('.bm-fab').click(); 'ok'");
        // The closed panel is rendered too (inert): its input is there before the click is handled.
        await UntilAsync("document.querySelector('.bm-root.bm-open .bm-input')", "the panel did not open");
    }

    /// <summary>Types the message in the widget's input and sends it, as the user does.</summary>
    internal async Task SendAsync(string message, bool withTheButtonFocused = false)
    {
        await TextAsync("(() => { const i = document.querySelector('.bm-input'); i.focus();"
            + $" Object.getOwnPropertyDescriptor(Object.getPrototypeOf(i), 'value').set.call(i, {JsonSerializer.Serialize(message)});"
            + " i.dispatchEvent(new Event('input', { bubbles: true })); i.dispatchEvent(new Event('change', { bubbles: true })); return 'ok'; })()");
        await UntilAsync("document.querySelector('.bm-send-btn') && !document.querySelector('.bm-send-btn').disabled", "the send button did not enable");
        await TextAsync(withTheButtonFocused
            ? "(() => { const b = document.querySelector('.bm-send-btn'); b.focus(); b.click(); return 'ok'; })()"
            : "document.querySelector('.bm-send-btn').click(); 'ok'");
    }

    /// <summary>axe-core on the page, WCAG 2.1 A and AA: one "rule @ element" for each element of each violation.</summary>
    internal async Task<List<string>> AxeAsync()
    {
        // Measured once the page is still: an element that is still fading in has its contrast left "incomplete" by axe,
        // not checked. The animations that never end (a spinner, the status dot) are not waited for.
        await UntilAsync("document.getAnimations().filter(a => a.playState === 'running' && a.effect && a.effect.getComputedTiming().iterations !== Infinity).length === 0",
            "the widget's animations did not end");
        await Task.Delay(200);

        // Twice, half a second apart, and only what both runs find. An animation that has not started when the wait above
        // looked (the welcome fades in after the panel opens) left an element half drawn, and axe measured its contrast
        // against what was behind it: a violation of one run in a release gate. A violation of the widget is there both
        // times; an element caught fading in is not. The widget does not follow prefers-reduced-motion yet (slice B), so
        // asking the browser for reduced motion would not stop its animations.
        var first = await RunAxeAsync();
        await Task.Delay(500);
        var second = await RunAxeAsync();
        return [.. first.Intersect(second, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private async Task<List<string>> RunAxeAsync()
    {
        var json = await TextAsync("""
            (async () => {
              if (!window.axe) await new Promise((ok, ko) => { const s = document.createElement('script'); s.src = '/axe.min.js'; s.onload = ok; s.onerror = ko; document.head.appendChild(s); });
              const r = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] }, resultTypes: ['violations'] });
              return JSON.stringify(r.violations.flatMap(v => v.nodes.map(n => v.id + ' @ ' + n.target.join(' '))));
            })()
            """);
        return JsonSerializer.Deserialize<List<string>>(json!)!;
    }
}

/// <summary>The root of the test site: a clean page (language, title, a heading, a main landmark) and the widget.</summary>
public sealed class WidgetA11yRoot : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "html");
        builder.AddAttribute(1, "lang", "en");
        builder.OpenElement(2, "head");
        builder.AddMarkupContent(3, "<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><base href=\"/\"><title>Orders</title>");
        builder.OpenComponent<HeadOutlet>(4);
        builder.AddComponentRenderMode(new InteractiveServerRenderMode(prerender: false));
        builder.CloseComponent();
        builder.CloseElement();
        builder.OpenElement(5, "body");
        builder.AddMarkupContent(6, "<main><h1>Orders</h1><p>The orders of the shop.</p><button id=\"page-button\" type=\"button\">Refresh</button></main>");
        builder.OpenComponent<WidgetA11yPage>(7);
        builder.AddComponentRenderMode(new InteractiveServerRenderMode(prerender: false));
        builder.CloseComponent();
        builder.AddMarkupContent(8, "<script src=\"_framework/blazor.web.js\"></script>");
        builder.CloseElement();
        builder.CloseElement();
    }
}

/// <summary>
/// The widget, interactive in the circuit. It tells the test its circuit's services under the run of its address. It has
/// no route of its own: a page without a parameter would be one more page of this assembly for the scanner's tests. The
/// site answers on the test pages' route, <c>/recipes/{page}</c>, and its root shows this page whatever the address.
/// </summary>
public sealed class WidgetA11yPage : ComponentBase
{
    internal static readonly ConcurrentDictionary<string, IServiceProvider> Circuits = new();

    [Inject] private IServiceProvider Services { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private string Run => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query)
        .TryGetValue("run", out var run) ? run.ToString() : "";

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender) Circuits[Run] = Services;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ChatWidget>(0);
        builder.CloseComponent();
    }
}
