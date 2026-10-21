using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Pages;

/// <summary>
/// <c>navigate_to</c> waits for <c>SignalReady</c> only where a page can send it.
///
/// <para>
/// Found by the rc.11 README audit. A page with <c>HasUIActions = true</c> made <c>navigate_to</c>
/// wait up to its <c>ReadyTimeout</c> for the new page to signal. Only an interactive Blazor circuit
/// in the same scope can do that. On a hub connection (WebAssembly, React, MAUI), over SSE or A2A the
/// page runs on the other end — the WebAssembly <c>SignalReady()</c> is a no-op — so every navigation
/// paid the full timeout and logged a warning, for a signal that could not come.
/// </para>
/// </summary>
public sealed class PageReadyScopeTests
{
    private sealed class Uninitialised : NavigationManager;

    private sealed class Initialised : NavigationManager
    {
        public Initialised() => Initialize("https://localhost/", "https://localhost/orders");
    }

    [Fact]
    public void A_headless_scope_with_no_navigation_manager_cannot_receive_it()
        => MentorOrchestrator.CanReceivePageReady(new ServiceCollection().BuildServiceProvider())
            .Should().BeFalse();

    [Fact]
    public void A_scope_whose_navigation_manager_was_never_initialised_cannot_receive_it()
    {
        // What a hub, SSE or A2A scope on a Blazor Server host resolves: registered, no circuit.
        var sp = new ServiceCollection().AddScoped<NavigationManager, Uninitialised>().BuildServiceProvider();

        MentorOrchestrator.CanReceivePageReady(sp.CreateScope().ServiceProvider).Should().BeFalse();
    }

    [Fact]
    public void A_circuit_scope_still_waits_as_before()
    {
        var sp = new ServiceCollection().AddScoped<NavigationManager, Initialised>().BuildServiceProvider();

        MentorOrchestrator.CanReceivePageReady(sp.CreateScope().ServiceProvider).Should().BeTrue(
            "in-process Blazor Server keeps the documented timing: the page signals when its actions are registered");
    }

    // ── Blazor Hybrid (MAUI): an initialised NavigationManager that is not Blazor Server's ──────────
    //
    // The READMEs said navigate_to waits for SignalReady "only in Blazor Server". The test is whether
    // NavigationManager.Uri can be read, and BlazorWebView's navigation manager is initialised like a
    // circuit's: in a MAUI Hybrid app the widget, the orchestrator and the page share one scope, the
    // page calls SignalReady, and navigate_to waits for it. The fixture's NavigationManager is that
    // kind — initialised, and no Blazor Server behind it. The sample MAUI app has no page with UI
    // actions to show it live.

    [Fact]
    public async Task A_hybrid_scope_waits_for_SignalReady_and_goes_on_when_it_arrives()
    {
        await using var host = MentorHostFixture.Create();
        var page = host.Get<IMentorPageContext>();

        // A wait no signal can miss, for this test only. The test page's ReadyTimeout is 500 ms, so the
        // timeout test below does not stall the suite, and against it a signal sent from the thread pool
        // came too late once on a loaded full run. The discovery is this host's own, not shared, so
        // the timeout test keeps its 500 ms. The turn still goes on as soon as the signal arrives.
        var pages = host.Get<MentorDiscoveryService>().Discover().Pages;
        var orders = pages.FindIndex(p => p.Url == "/orders");
        pages[orders] = pages[orders] with { ReadyTimeout = 30_000 };

        // The new page signals a moment after the navigation, as OnInitialized does once its actions
        // are registered. The moment is what shows the wait: a navigate_to that did not wait would
        // reach the next model call before the signal. Set before SignalReady, so the reply sees it
        // whenever the wait has returned on it.
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signalledBeforeTheTurnWentOn = false;
        host.Get<IMentorStateService>().OnNavigationRequested += url => _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            signalled.TrySetResult();
            page.SignalReady();
        });
        host.Chat!.CallTool("navigate_to", new { url = "/orders" })
            .Respond((_, _) =>
            {
                signalledBeforeTheTurnWentOn = signalled.Task.IsCompleted;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Here are your orders."));
            });

        (await host.RunTurnAsync("show me the orders")).Should().BeNull();

        host.Logs.Records.Should().Contain(r => r.Message.Contains("Waiting for SignalReady on /orders", StringComparison.Ordinal),
            "an initialised NavigationManager that is not Blazor Server's is a scope the page can signal in");
        signalledBeforeTheTurnWentOn.Should().BeTrue("navigate_to returned only once the page had signalled");
        host.Logs.HasWarning("SignalReady timeout").Should().BeFalse(host.Logs.WarningText);
        host.Chat!.Calls.Should().HaveCount(2, "the turn went on to its reply after the navigation");
    }

    [Fact]
    public async Task A_hybrid_scope_without_SignalReady_waits_out_the_timeout_and_says_so()
    {
        // The other half: the wait is real, so a page that never signals costs its ReadyTimeout (500 ms
        // on the test page) and a warning, and the turn still finishes.
        await using var host = MentorHostFixture.Create();
        host.Chat!.CallTool("navigate_to", new { url = "/orders" }).Say("Here are your orders.");

        (await host.RunTurnAsync("show me the orders")).Should().BeNull();

        host.Logs.HasWarning("SignalReady timeout for /orders").Should().BeTrue(host.Logs.WarningText);
        host.Chat!.Calls.Should().HaveCount(2);
    }

    // ── BUG-194: the signal of the page being left ──────────────────────────────────────────────────

    [Fact]
    public async Task A_navigation_waits_for_the_new_pages_signal_even_when_the_page_it_leaves_had_signalled()
    {
        // The page on screen signalled when it opened, and that signal was still there: navigate_to returned at once,
        // before the new page had registered its actions. Live, the model's next round trip hid the race.
        await using var host = MentorHostFixture.Create();
        var page = host.Get<IMentorPageContext>();
        var pages = host.Get<MentorDiscoveryService>().Discover().Pages;
        var orders = pages.FindIndex(p => p.Url == "/orders");
        pages[orders] = pages[orders] with { ReadyTimeout = 30_000 };

        page.SetPageName("Home").SignalReady();

        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signalledBeforeTheTurnWentOn = false;
        host.Get<IMentorStateService>().OnNavigationRequested += url => _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            signalled.TrySetResult();
            page.SetPageName("Orders").SignalReady();
        });
        host.Chat!.CallTool("navigate_to", new { url = "/orders" })
            .Respond((_, _) =>
            {
                signalledBeforeTheTurnWentOn = signalled.Task.IsCompleted;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Here are your orders."));
            });

        (await host.RunTurnAsync("show me the orders")).Should().BeNull();

        signalledBeforeTheTurnWentOn.Should().BeTrue("the old page's signal is not the new page's");
        host.Logs.HasWarning("SignalReady timeout").Should().BeFalse(host.Logs.WarningText);
    }

    [Fact]
    public async Task A_navigation_to_the_page_already_shown_does_not_wait_for_a_signal()
    {
        // Blazor does not initialize a page again for its own address, so no new SignalReady comes: taking the
        // signal back there would wait out the whole ReadyTimeout for nothing.
        await using var host = MentorHostFixture.Create();
        var pages = host.Get<MentorDiscoveryService>().Discover().Pages;
        var orders = pages.FindIndex(p => p.Url == "/orders");
        pages[orders] = pages[orders] with { ReadyTimeout = 30_000 };

        host.Navigation.StartAt("orders?status=pending");
        host.Get<IMentorPageContext>().SetPageName("Orders").SignalReady();
        host.Chat!.CallTool("navigate_to", new { url = "/orders" }).Say("You are on the orders page.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        (await host.RunTurnAsync("show me the orders")).Should().BeNull();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the page on screen registered its actions already");
        host.Logs.HasWarning("SignalReady timeout").Should().BeFalse(host.Logs.WarningText);
    }
}
