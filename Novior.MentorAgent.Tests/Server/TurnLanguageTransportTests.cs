using System.Net.Http.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Hub;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Server;

/// <summary>
/// The language of the turn on every transport: the hub's <c>SendUserMessage</c>, the <c>language</c> of
/// <c>/mentor/chat</c>, the WebAssembly client and <c>/mentor/tour?language=</c>. A value that is not a language is the
/// application's, and never fails the request. The application here is English.
/// </summary>
public sealed class TurnLanguageTransportTests
{
    private const string Italian = "ALWAYS respond in **Italian**";
    private const string English = "ALWAYS respond in **English**";

    private static async Task<string> FirstInstructionsAsync(ServerAppFactory app)
    {
        // The invocation and the request return with the turn, but a turn can end before its first call is recorded
        // only when it failed: wait a little rather than read an empty list.
        for (var i = 0; i < 100 && app.Chat.Calls.Count == 0; i++)
            await Task.Delay(50);
        app.Chat.Calls.Should().NotBeEmpty("the turn reached the model. Warnings: {0}", app.Logs.WarningText);
        return app.Chat.Calls[0].Instructions ?? "";
    }

    [Fact]
    public async Task The_hub_s_SendUserMessage_carries_the_language_of_the_turn()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Say("ciao");
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        await hub.InvokeAsync("SendUserMessage", new { text = "ciao", attachments = (object?)null, language = "it" });

        (await FirstInstructionsAsync(app)).Should().Contain(Italian).And.NotContain(English);
    }

    public static TheoryData<object?> NotALanguage => new() { "klingon", "5", "", null, 5, true };

    [Theory]
    [MemberData(nameof(NotALanguage))]
    public async Task On_the_hub_a_value_that_is_not_a_language_is_the_application_s(object? language)
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Say("hello");
        await using var hub = app.CreateHubConnection();
        await hub.StartAsync();

        var send = async () => await hub.InvokeAsync("SendUserMessage", new { text = "hello", attachments = (object?)null, language });

        await send.Should().NotThrowAsync();
        (await FirstInstructionsAsync(app)).Should().Contain(English);
    }

    [Theory]
    [InlineData("it")]
    [InlineData("IT")]
    [InlineData("Italian")]
    public async Task The_language_of_a_chat_request_is_the_language_of_the_turn(string language)
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Say("ciao");

        using var response = await app.Client.PostAsJsonAsync("/mentor/chat", new { message = "ciao", language });
        await response.Content.ReadAsStringAsync();

        (await FirstInstructionsAsync(app)).Should().Contain(Italian).And.NotContain(English);
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("5")]
    [InlineData("")]
    public async Task On_a_chat_request_a_value_that_is_not_a_language_is_the_application_s(string language)
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Say("hello");

        using var response = await app.Client.PostAsJsonAsync("/mentor/chat", new { message = "hello", language });
        await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.Should().BeTrue();
        (await FirstInstructionsAsync(app)).Should().Contain(English);
    }

    [Fact]
    public async Task The_text_only_chat_request_takes_the_language_from_the_query()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Say("ciao");

        using var response = await app.Client.GetAsync("/mentor/chat?message=ciao&language=it");
        await response.Content.ReadAsStringAsync();

        (await FirstInstructionsAsync(app)).Should().Contain(Italian);
    }

    [Fact]
    public async Task The_WebAssembly_client_sends_the_language_with_the_turn()
    {
        await using var app = await ServerAppFactory.CreateAsync(o => o.MemoryAutoCapture = false);
        app.Chat.Say("ciao");
        var options = new MentorAgentBlazorOptions
        {
            HubUrl = new Uri(app.BaseAddress, "mentor-hub").ToString(),
            ConfigureConnection = o => o.HttpMessageHandlerFactory = _ => app.CreateHandler(),
        };
        await using var orchestrator = new WasmMentorOrchestrator(
            new MentorHubClient(options, NullLogger<MentorHubClient>.Instance),
            new WasmMentorPageContext(), NullLogger<WasmMentorOrchestrator>.Instance, new HttpClient(), options);

        await orchestrator.SendMessageAsync(new MentorUserMessage("ciao") { Language = MentorLanguage.Italian });

        (await FirstInstructionsAsync(app)).Should().Contain(Italian);
    }

    // ── The tour ────────────────────────────────────────────────────────────────

    private sealed class RecordingTour : IMentorTour
    {
        internal List<MentorLanguage?> Asked { get; } = [];

        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
            => GetStepsAsync(null, ct);

        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(MentorLanguage? language, CancellationToken ct = default)
        {
            lock (Asked) Asked.Add(language);
            return Task.FromResult<IReadOnlyList<MentorTourStep>>([new MentorTourStep("Benvenuto", "Ciao.")]);
        }
    }

    [Theory]
    [InlineData("/mentor/tour?language=it", MentorLanguage.Italian)]
    [InlineData("/mentor/tour?language=FR", MentorLanguage.French)]
    [InlineData("/mentor/tour", null)]
    [InlineData("/mentor/tour?language=klingon", null)]
    public async Task The_tour_endpoint_asks_for_the_tour_of_the_language_of_the_query(string url, MentorLanguage? expected)
    {
        var tour = new RecordingTour();
        await using var app = await ServerAppFactory.CreateAsync(configureServices: s => s.AddSingleton<IMentorTour>(tour));

        using var response = await app.Client.GetAsync(url);

        response.IsSuccessStatusCode.Should().BeTrue();
        tour.Asked.Should().Equal(expected);
    }
}
