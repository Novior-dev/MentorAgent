using Bunit;
using FluentAssertions;
using FluentAssertions.Execution;
using Novior.MentorAgent.Abstractions.Components;
using Novior.MentorAgent.Abstractions.Core;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Extensions;
using Novior.MentorAgent.Tests.Components;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Localisation;

/// <summary>
/// BUG-149 — the input box spoke English on every server-hosted widget.
///
/// <para>
/// <c>MentorOptions.InputPlaceholder</c> defaulted to <c>"Type a message..."</c>, and
/// <c>AddMentorAgent()</c> copies it into <see cref="MentorWidgetOptions"/>. <c>ChatInput</c> falls
/// back to the localized <c>input.placeholder</c> only when it gets no placeholder, so with
/// <c>Language = Italian</c> the welcome panel and the buttons were Italian and the input box was
/// not. The WebAssembly client's option was already <c>null</c>: the two hosts disagreed.
/// </para>
///
/// <para>
/// Each case renders the widget with the options and the localizer the host's own registration
/// produces, so the mapping in between is part of what is tested.
/// </para>
/// </summary>
public sealed class InputPlaceholderTests : ComponentTestBase
{
    public InputPlaceholderTests()
    {
        Services.AddSingleton<IMentorStateService>(new RecordingStateService());
        Services.AddSingleton<IMentorOrchestrator>(new StubOrchestrator());
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
    }

    /// <summary>Renders and opens the widget with what the host's container hands it.</summary>
    private string PlaceholderShown(IServiceProvider host)
    {
        Services.AddSingleton(host.GetRequiredService<IOptions<MentorWidgetOptions>>());
        Services.AddSingleton(host.GetRequiredService<MentorLocalizer>());

        var cut = Render<ChatWidget>();
        cut.Find(".bm-fab").Click();
        return cut.Find("textarea.bm-input").GetAttribute("placeholder") ?? "";
    }

    private async Task<(string? Option, string Shown)> ServerAsync(MentorLanguage language, string? placeholder = null)
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.Language = language;
            if (placeholder is not null) o.InputPlaceholder = placeholder;
        });

        var option = host.Get<IOptions<MentorWidgetOptions>>().Value.InputPlaceholder;
        return (option, PlaceholderShown(host.Scope.ServiceProvider));
    }

    private async Task<(string? Option, string Shown)> WasmAsync(MentorLanguage language, string? placeholder = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMentorAgentBlazor(o =>
        {
            o.Language = language;
            if (placeholder is not null) o.InputPlaceholder = placeholder;
        });

        await using var provider = services.BuildServiceProvider();
        var option = provider.GetRequiredService<IOptions<MentorWidgetOptions>>().Value.InputPlaceholder;
        return (option, PlaceholderShown(provider));
    }

    [Theory]
    [InlineData(MentorLanguage.Italian, "Scrivi un messaggio...")]
    [InlineData(MentorLanguage.German,  "Nachricht schreiben...")]
    [InlineData(MentorLanguage.English, "Write a message...")]
    public async Task On_the_server_an_unset_placeholder_speaks_the_configured_language(MentorLanguage language, string expected)
    {
        var (option, shown) = await ServerAsync(language);

        using var _ = new AssertionScope();
        option.Should().BeNull("an unset placeholder must reach the widget as unset, not as English copy");
        shown.Should().Be(expected, "the input box speaks the widget's language, like the rest of it");
    }

    [Theory]
    [InlineData(MentorLanguage.Italian, "Scrivi un messaggio...")]
    [InlineData(MentorLanguage.English, "Write a message...")]
    public async Task In_WebAssembly_an_unset_placeholder_speaks_the_configured_language(MentorLanguage language, string expected)
    {
        // Already right before the fix: the reference the server now agrees with.
        var (option, shown) = await WasmAsync(language);

        option.Should().BeNull();
        shown.Should().Be(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_placeholder_the_host_sets_is_shown_as_written(bool wasm)
    {
        const string own = "Chiedi qualcosa sugli ordini...";

        var (option, shown) = wasm
            ? await WasmAsync(MentorLanguage.German, own)
            : await ServerAsync(MentorLanguage.German, own);

        option.Should().Be(own);
        shown.Should().Be(own, "a host's own text wins over the localized default, whatever the language");
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
