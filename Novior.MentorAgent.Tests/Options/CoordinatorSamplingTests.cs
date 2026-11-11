using FluentAssertions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Options;

/// <summary>
/// Story 169 — the coordinator had no sampling settings: with a local model whose default suits chat, not tool calls,
/// nothing could be tuned. <see cref="MentorOptions.CoordinatorSampling"/> puts them on the coordinator's requests — the
/// forced first call and the follow-ups of the tool loop too. The classifiers call the provider directly and keep
/// their own: those that ask for temperature 0 still do, the others keep the provider's default.
/// </summary>
public sealed class CoordinatorSamplingTests
{
    private static readonly MentorSamplingOptions Every = new()
    {
        Temperature = 0.3f,
        TopP = 0.8f,
        TopK = 40,
        MaxOutputTokens = 900,
        FrequencyPenalty = 0.1f,
        PresencePenalty = -0.2f,   // OpenAI takes -2 to 2: the range of the penalties is the provider's
        Seed = 7,
    };

    private static IReadOnlyList<FakeChatClient.RecordedCall> CoordinatorCalls(MentorHostFixture host)
        => host.Chat!.Calls.Where(c => c.ToolNames.Count > 0).ToList();

    private static void ShouldCarryEvery(FakeChatClient.RecordedCall call)
    {
        call.Temperature.Should().Be(0.3f);
        call.TopP.Should().Be(0.8f);
        call.TopK.Should().Be(40);
        call.MaxOutputTokens.Should().Be(900);
        call.FrequencyPenalty.Should().Be(0.1f);
        call.PresencePenalty.Should().Be(-0.2f);
        call.Seed.Should().Be(7);
    }

    [Fact]
    public async Task Every_value_reaches_the_coordinators_request()
    {
        await using var host = MentorHostFixture.Create(o => o.CoordinatorSampling = Every);
        host.Chat!.Say("Hello!");

        await host.RunTurnAsync("hello");

        ShouldCarryEvery(CoordinatorCalls(host).Should().ContainSingle().Subject);
    }

    [Fact]
    public async Task Without_the_option_the_request_carries_no_sampling_as_before()
    {
        await using var host = MentorHostFixture.Create();
        host.Chat!.Say("Hello!");

        await host.RunTurnAsync("hello");

        var call = CoordinatorCalls(host).Should().ContainSingle().Subject;
        call.Temperature.Should().BeNull();
        call.TopP.Should().BeNull();
        call.TopK.Should().BeNull();
        call.MaxOutputTokens.Should().BeNull();
        call.FrequencyPenalty.Should().BeNull();
        call.PresencePenalty.Should().BeNull();
        call.Seed.Should().BeNull();
    }

    [Fact]
    public async Task Only_the_values_set_are_sent()
    {
        await using var host = MentorHostFixture.Create(o => o.CoordinatorSampling = new MentorSamplingOptions { Temperature = 0.2f });
        host.Chat!.Say("Hello!");

        await host.RunTurnAsync("hello");

        var call = CoordinatorCalls(host).Should().ContainSingle().Subject;
        call.Temperature.Should().Be(0.2f);
        call.TopP.Should().BeNull("a value left null is the provider's own");
        call.MaxOutputTokens.Should().BeNull();
        call.Seed.Should().BeNull();
    }

    // ── The forced call, the tool loop and the classifiers ───────────────────────

    private static readonly string[] Reads = ["count_open_orders", "get_active_orders", "show_order_card"];

    [Fact]
    public async Task The_forced_call_and_the_follow_up_carry_them_and_the_classifier_stays_at_zero()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.CoordinatorSampling = Every;
            o.RequireLookupForRecordQuestions = true;
            o.IsReadOnlyAction = name => Reads.Contains(name);
            o.MemoryAutoCapture = false;
        });
        host.Auth.SignIn("ada", "Manager", "Admin");
        host.Chat!.Say("LOOKUP").CallTool("count_open_orders").Say("Sono 3.");

        await host.RunTurnAsync("Quanti ordini sono aperti?");

        var coordinator = CoordinatorCalls(host);
        coordinator.Should().HaveCount(2);
        coordinator[0].ToolMode.Should().Be(ChatToolMode.RequireAny);
        coordinator.Should().AllSatisfy(ShouldCarryEvery);
        var classifier = host.Chat.Calls.Where(c => c.ToolNames.Count == 0).Should().ContainSingle().Subject;
        classifier.Temperature.Should().Be(0f, "the classifiers keep their own settings");
        classifier.TopP.Should().BeNull();
        classifier.Seed.Should().BeNull();
        classifier.MaxOutputTokens.Should().NotBe(900);
    }

    [Fact]
    public async Task A_classifier_with_the_providers_default_keeps_it()
    {
        await using var host = MentorHostFixture.Create(o =>
        {
            o.CoordinatorSampling = new MentorSamplingOptions { Temperature = 0.9f, TopP = 0.5f };
            o.EnableSafetyCheck = true;
        });
        host.Chat!.Say("SAFE").Say("Hello!");

        await host.RunTurnAsync("hello");

        var safety = host.Chat.Calls[0];
        safety.ToolNames.Should().BeEmpty("the input safety check is asked first");
        safety.Temperature.Should().BeNull("it uses the provider's default today, and the coordinator's does not reach it");
        safety.TopP.Should().BeNull();
        CoordinatorCalls(host).Should().ContainSingle().Which.Temperature.Should().Be(0.9f);
    }

    // ── Startup ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Temperature", "cannot be negative")]
    [InlineData("TopP", "must be above 0 and at most 1")]
    [InlineData("TopPAboveOne", "must be above 0 and at most 1")]
    [InlineData("TopK", "must be at least 1")]
    [InlineData("MaxOutputTokens", "must be at least 1")]
    public async Task A_value_no_provider_accepts_stops_the_application_at_startup(string which, string rule)
    {
        var sampling = which switch
        {
            "Temperature" => new MentorSamplingOptions { Temperature = -0.1f },
            "TopP" => new MentorSamplingOptions { TopP = 0f },
            "TopPAboveOne" => new MentorSamplingOptions { TopP = 1.5f },
            "TopK" => new MentorSamplingOptions { TopK = 0 },
            _ => new MentorSamplingOptions { MaxOutputTokens = 0 },
        };
        await using var host = MentorHostFixture.Create(o => o.CoordinatorSampling = sampling);

        var read = () => host.Get<IOptions<MentorOptions>>().Value;

        read.Should().Throw<InvalidOperationException>()
            .WithMessage($"*CoordinatorSampling.{(which == "TopPAboveOne" ? "TopP" : which)} {rule}*");
    }

    [Fact]
    public async Task The_values_bind_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mentor:CoordinatorSampling:Temperature"] = "0.2",
                ["Mentor:CoordinatorSampling:Seed"] = "42",
            })
            .Build();
        await using var host = MentorHostFixture.Create(o => configuration.GetSection("Mentor").Bind(o));
        host.Chat!.Say("Hello!");

        await host.RunTurnAsync("hello");

        var call = CoordinatorCalls(host).Should().ContainSingle().Subject;
        call.Temperature.Should().Be(0.2f);
        call.Seed.Should().Be(42);
    }
}
