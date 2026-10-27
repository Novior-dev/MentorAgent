using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Localisation;

/// <summary>
/// The generated tour, one per language: a page in French gets the French tour, generated once for every French
/// visitor, while the Italian one is generated once for the Italian visitors. A tour written by hand that knows
/// nothing of languages keeps working, in its one language.
/// </summary>
public sealed class TourLanguageTests
{
    // The pages are the test assembly's own, among them /orders (OnboardingTourTests): a page added here would change
    // the count other tests read.

    /// <summary>Writes a one-step tour titled with the language it was asked for, slowly, and counts the calls.</summary>
    private sealed class CountingStructured : IMentorStructured
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public async Task<T?> GenerateAsync<T>(string prompt, string? instructions = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(50, ct);
            var language = Enum.GetValues<MentorLanguage>().First(l => instructions!.Contains($"Answer in {l}.", StringComparison.Ordinal));
            object plan = new MentorTourService.TourPlan
            {
                Steps = [new MentorTourService.TourPlanStep { Title = language.ToString(), Body = "A step.", Url = "/orders" }],
            };
            return (T)plan;
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static (MentorTourService Tour, MentorOptions Options) Build(IMentorStructured structured)
    {
        var options = new MentorOptions
        {
            AppName = "Test Shop",
            ScanAssemblies = [typeof(TourLanguageTests).Assembly],
            ChatClient = new StubChatClient(),
            Language = MentorLanguage.Italian,
        };
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var discovery = new MentorDiscoveryService(wrapped, NullLogger<MentorDiscoveryService>.Instance);
        return (new MentorTourService(wrapped, discovery, structured, NullLogger<MentorTourService>.Instance), options);
    }

    private static string Title(IReadOnlyList<MentorTourStep> steps) => steps.Should().NotBeEmpty().And.Subject.First().Title;

    [Fact]
    public async Task Two_languages_give_two_tours_each_generated_once_even_when_asked_together()
    {
        var structured = new CountingStructured();
        var (tour, _) = Build(structured);

        var asked = Enumerable.Range(0, 6)
            .Select(i => tour.GetStepsAsync(i % 2 == 0 ? MentorLanguage.French : MentorLanguage.German))
            .ToList();
        var results = await Task.WhenAll(asked);

        structured.Calls.Should().Be(2, "one generation per language, whoever asked first");
        results.Where((_, i) => i % 2 == 0).Select(Title).Should().OnlyContain(t => t == "French");
        results.Where((_, i) => i % 2 == 1).Select(Title).Should().OnlyContain(t => t == "German");
    }

    [Fact]
    public async Task Without_a_language_the_tour_is_in_the_application_language_read_at_the_call()
    {
        var structured = new CountingStructured();
        var (tour, options) = Build(structured);

        var italian = await tour.GetStepsAsync();
        options.Language = MentorLanguage.Spanish;
        var spanish = await tour.GetStepsAsync();
        var again = await tour.GetStepsAsync(MentorLanguage.Italian);

        Title(italian).Should().Be("Italian");
        Title(spanish).Should().Be("Spanish");
        Title(again).Should().Be("Italian");
        structured.Calls.Should().Be(2);
    }

    private sealed class HandWrittenTour : IMentorTour
    {
        public Task<IReadOnlyList<MentorTourStep>> GetStepsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MentorTourStep>>([new MentorTourStep("Welcome", "Written by hand.")]);
    }

    [Fact]
    public async Task A_tour_written_by_hand_answers_in_its_one_language_for_every_page()
    {
        IMentorTour tour = new HandWrittenTour();

        var french = await tour.GetStepsAsync(MentorLanguage.French);

        Title(french).Should().Be("Welcome");
    }
}
