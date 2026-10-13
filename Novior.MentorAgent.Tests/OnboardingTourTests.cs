using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Attributes;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// Tests for the AI-generated onboarding tour (roadmap #18).
///
/// <para>
/// The interesting behaviour is not "does it produce steps" but what it does with a model that
/// invents things. A tour is navigation: a step whose "open this screen" button leads nowhere is a
/// dead end presented to a user on their very first minute in the application, and the model has
/// every incentive to invent a plausible-looking URL. So the generated plan is filtered against the
/// pages that actually exist, and the fallback has to be good enough to ship on its own.
/// </para>
/// </summary>
public sealed class OnboardingTourTests
{
    // ── Pages the fake application registers ──────────────────────────────────
    [MentorPage(Url = "/orders", Name = "Orders", Description = "Order list and status")]
    private sealed class OrdersPage { }

    [MentorPage(Url = "/products", Name = "Products", Description = "Catalogue and prices")]
    private sealed class ProductsPage { }

    /// <summary>Returns a fixed plan, so the test controls exactly what the "model" produced.</summary>
    private sealed class ScriptedStructured(object? plan) : IMentorStructured
    {
        public Task<T?> GenerateAsync<T>(string prompt, string? instructions = null, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(plan is T typed ? typed : default);
        }

        public string? LastPrompt { get; private set; }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static MentorOptions BuildOptions(bool withChatClient = true) => new()
    {
        AppName = "Test Shop",
        AppDescription = "A shop used by the tests",
        ScanAssemblies = [typeof(OnboardingTourTests).Assembly],
        ChatClient = withChatClient ? new StubChatClient() : null,
    };

    private static MentorTourService BuildService(MentorOptions options, IMentorStructured structured)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var discovery = new MentorDiscoveryService(wrapped, NullLogger<MentorDiscoveryService>.Instance);
        return new MentorTourService(wrapped, discovery, structured, NullLogger<MentorTourService>.Instance);
    }

    private static MentorTourService.TourPlan Plan(params MentorTourService.TourPlanStep[] steps)
        => new() { Steps = [.. steps] };

    [Fact]
    public async Task GeneratedStep_PointingAtARealPage_KeepsItsUrl()
    {
        var service = BuildService(BuildOptions(), new ScriptedStructured(Plan(
            new MentorTourService.TourPlanStep
            {
                Title = "Orders", Body = "Track what customers ordered.",
                Url = "/orders", TryAsking = "Quanti ordini sono in attesa?"
            })));

        var steps = await service.GetStepsAsync();

        steps.Should().ContainSingle();
        steps[0].Url.Should().Be("/orders");
        steps[0].TryAsking.Should().Be("Quanti ordini sono in attesa?");
    }

    [Fact]
    public async Task InventedUrl_IsDroppedButTheStepSurvives()
    {
        // The model made up a screen that sounds right and does not exist. Keeping the text is
        // fine; keeping the link would send a first-time user to a 404.
        var service = BuildService(BuildOptions(), new ScriptedStructured(Plan(
            new MentorTourService.TourPlanStep
            {
                Title = "Reports", Body = "See how the shop is doing.", Url = "/reports"
            })));

        var steps = await service.GetStepsAsync();

        steps.Should().ContainSingle();
        steps[0].Title.Should().Be("Reports");
        steps[0].Url.Should().BeNull();
    }

    [Fact]
    public async Task EmptyStep_IsDiscarded()
    {
        var service = BuildService(BuildOptions(), new ScriptedStructured(Plan(
            new MentorTourService.TourPlanStep { Title = "", Body = "no title" },
            new MentorTourService.TourPlanStep { Title = "no body", Body = "  " },
            new MentorTourService.TourPlanStep { Title = "Good", Body = "Kept." })));

        var steps = await service.GetStepsAsync();

        steps.Should().ContainSingle();
        steps[0].Title.Should().Be("Good");
    }

    [Fact]
    public async Task NoChatClient_FallsBackToTheDiscoveredPages()
    {
        // Path B, or simply an app with no ChatClient: the tour must still be worth showing.
        var service = BuildService(BuildOptions(withChatClient: false), new ScriptedStructured(null));

        var steps = await service.GetStepsAsync();

        steps.Should().HaveCount(3);                       // welcome + two pages
        steps[0].Title.Should().Be("Test Shop");
        steps.Select(s => s.Url).Should().Contain(["/orders", "/products"]);
    }

    [Fact]
    public async Task ModelReturnsNothingUsable_FallsBackInsteadOfShowingAnEmptyTour()
    {
        var service = BuildService(BuildOptions(), new ScriptedStructured(Plan()));

        var steps = await service.GetStepsAsync();

        steps.Should().NotBeEmpty();
        steps[0].Title.Should().Be("Test Shop");
    }

    [Fact]
    public async Task Generation_HappensOnceAndIsSharedByEveryVisitor()
    {
        var structured = new CountingStructured(Plan(
            new MentorTourService.TourPlanStep { Title = "Orders", Body = "…", Url = "/orders" }));
        var service = BuildService(BuildOptions(), structured);

        // Ten concurrent first-time visitors must not each pay for the same tour.
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.GetStepsAsync()));

        structured.Calls.Should().Be(1);
        results.Should().OnlyContain(r => r.Count == 1);
    }

    [Fact]
    public async Task Prompt_DescribesTheRealScreensAndCapabilities()
    {
        // TryAsking is only useful if it is answerable, which depends on the model being told what
        // the application actually has. If this stops being sent, the tour starts suggesting
        // questions the assistant cannot answer.
        var structured = new ScriptedStructured(Plan(
            new MentorTourService.TourPlanStep { Title = "Orders", Body = "…", Url = "/orders" }));
        var service = BuildService(BuildOptions(), structured);

        await service.GetStepsAsync();

        structured.LastPrompt.Should().Contain("Test Shop");
        structured.LastPrompt.Should().Contain("/orders");
        structured.LastPrompt.Should().Contain("/products");
    }

    private sealed class CountingStructured(object? plan) : IMentorStructured
    {
        private int _calls;
        public int Calls => _calls;

        public async Task<T?> GenerateAsync<T>(string prompt, string? instructions = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(20, ct);       // long enough for the callers to pile up on the gate
            return plan is T typed ? typed : default;
        }
    }
}
