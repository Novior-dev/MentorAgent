using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Fakes;
using Xunit;

namespace Novior.MentorAgent.Tests.Components;

/// <summary>
/// Story 1640 (L6) — the widget starts the observation of the pages only when the application has one and it is on
/// (<c>page.observe-off</c>), and stops it when it goes.
/// </summary>
public sealed class ChatWidgetObservationTests : ComponentTestBase
{
    public ChatWidgetObservationTests()
    {
        Services.AddSingleton<IMentorStateService>(new RecordingStateService());
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    }

    private sealed class StubOrchestrator : IMentorOrchestrator
    {
        public Task SendMessageAsync(string userMessage, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendMessageAsync(string userMessage, IReadOnlyList<MentorAttachment>? attachments, CancellationToken ct = default)
            => Task.CompletedTask;
        public void CancelCurrentRequest() { }
        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<System.Text.Json.JsonElement?> SerializeSessionAsync(CancellationToken ct = default)
            => Task.FromResult<System.Text.Json.JsonElement?>(null);
        public Task RestoreSessionAsync(System.Text.Json.JsonElement snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task RespondToApprovalAsync(ConfirmationRequest req, bool approved, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Observer(bool on) : IMentorPageObserver
    {
        public bool IsOn => on;
        public Task<bool> ReportAsync(string? path, IReadOnlyList<PageObservedElement?>? elements, string? limit) => Task.FromResult(true);
    }

    [Fact]
    [Trait("Rule", "page.observe")]
    public void With_the_observation_on_the_widget_loads_its_module_starts_it_and_stops_it_when_it_goes()
    {
        Services.AddSingleton<IMentorPageObserver>(new Observer(true));
        var module = JSInterop.SetupModule(MentorPageObservation.Module);

        var cut = Render<ChatWidget>();

        module.Invocations.Select(i => i.Identifier).Should().Equal("start");
        cut.Instance.Dispose();
        module.Invocations.Select(i => i.Identifier).Should().Equal("start", "stop");
    }

    [Theory]
    [Trait("Rule", "page.observe-off")]
    [InlineData(true)]
    [InlineData(false)]
    public void Off_or_with_no_observer_the_widget_never_loads_the_module(bool registered)
    {
        if (registered) Services.AddSingleton<IMentorPageObserver>(new Observer(false));

        Render<ChatWidget>();

        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "import");
    }
}
