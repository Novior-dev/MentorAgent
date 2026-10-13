using FluentAssertions;
using Novior.MentorAgent.Core;
using Microsoft.AspNetCore.Components;
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
}
