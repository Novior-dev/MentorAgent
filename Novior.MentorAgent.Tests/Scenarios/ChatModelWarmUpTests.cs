using System.Diagnostics;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Novior.MentorAgent.Tests.Scenarios;

/// <summary>
/// Story 173 — <c>WarmUpAtStartup</c> built a coordinator and filled the embedding cache, but never called the chat
/// model: the first turn still paid for the connection, the provider's empty prompt cache, and a local model still
/// being loaded. <see cref="MentorOptions.WarmUpChatModel"/> sends it one minimal request at start-up — the
/// coordinator's instructions and tools, no call allowed, a 16-token cap — and the strong model one too when routing
/// has it. Without the option nothing changes.
/// </summary>
public sealed class ChatModelWarmUpTests
{
    private static MentorHostFixture Host(Action<MentorOptions>? configure = null, bool warmUp = true, bool chatModel = true)
        => MentorHostFixture.Create(o =>
        {
            o.WarmUpAtStartup = warmUp;
            o.WarmUpChatModel = chatModel;
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        });

    /// <summary>The hosted service, started by hand as in <c>WarmUpTests</c>: the fixture builds a container, not a host.</summary>
    private static async Task RunWarmUpAsync(MentorHostFixture host)
    {
        var service = new MentorWarmUpService(
            host.Scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider,
            host.Get<IOptions<MentorOptions>>(),
            host.Get<ILogger<MentorWarmUpService>>());

        await service.StartAsync(default);
        await service.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Without_the_option_the_warm_up_never_calls_the_chat_model()
    {
        await using var host = Host(chatModel: false);

        await RunWarmUpAsync(host);

        host.Chat!.Calls.Should().BeEmpty("nothing changes without the option");
    }

    [Fact]
    public async Task With_the_option_one_minimal_request_carries_the_coordinators_prefix_and_no_call()
    {
        await using var host = Host(o => o.CoordinatorSampling = new MentorSamplingOptions { Temperature = 0.2f, MaxOutputTokens = 900 });

        await RunWarmUpAsync(host);
        // A user with every role: for the others the role filter rewrites the count of the actions, and the tools.
        host.Auth.SignIn("ada", "Manager", "Admin");
        host.Chat!.Say("Hello!");
        await host.RunTurnAsync("hello");

        host.Chat.Calls.Should().HaveCount(2, "the warm-up's request, then the turn's");
        var warm = host.Chat.Calls[0];
        var turn = host.Chat.Calls[1];
        warm.ToolMode.Should().Be(ChatToolMode.None, "no call is allowed");
        warm.MaxOutputTokens.Should().Be(MentorOrchestrator.WarmUpMaxOutputTokens, "the cap wins over the coordinator's own limit");
        warm.Temperature.Should().Be(0.2f, "the coordinator's sampling, so the request is the one its turns send");
        warm.Messages.Should().ContainSingle().Which.Text.Should().Be(MentorOrchestrator.WarmUpMessage);
        warm.Instructions.Should().NotBeNullOrWhiteSpace();
        turn.Instructions.Should().StartWith(warm.Instructions!, "the static prompt is the prefix a provider can cache");
        warm.ToolNames.Should().BeEquivalentTo(turn.ToolNames, "the same tools, so the prefix is the same");
        host.Logs.Records.Should().Contain(r => r.Level == LogLevel.Information && r.Message.Contains("ChatClient warmed up in"));
    }

    [Fact]
    public async Task With_the_option_the_first_turn_is_not_slower_than_the_next()
    {
        // A provider whose first request is slow: a cold connection, an empty cache, a local model being loaded.
        static Func<Task> ColdOnce(TimeSpan delay)
        {
            var cold = true;
            return async () =>
            {
                if (!cold) return;
                cold = false;
                await Task.Delay(delay);
            };
        }

        var coldStart = TimeSpan.FromMilliseconds(1500);

        await using (var warm = Host())
        {
            warm.Chat!.BeforeRespond = ColdOnce(coldStart);
            var warming = Stopwatch.StartNew();
            await RunWarmUpAsync(warm);
            warming.Elapsed.Should().BeGreaterThanOrEqualTo(coldStart - TimeSpan.FromMilliseconds(50), "the warm-up paid for it");

            warm.Chat.SayRepeatedly("Hello!", 2);
            var first = Stopwatch.StartNew();
            await warm.RunTurnAsync("hello");
            first.Stop();
            first.Elapsed.Should().BeLessThan(coldStart / 2, "the first turn starts warm");
        }

        await using var cold = Host(chatModel: false);
        cold.Chat!.BeforeRespond = ColdOnce(coldStart);
        await RunWarmUpAsync(cold);
        cold.Chat.Say("Hello!");
        var unwarmed = Stopwatch.StartNew();
        await cold.RunTurnAsync("hello");
        unwarmed.Elapsed.Should().BeGreaterThanOrEqualTo(coldStart - TimeSpan.FromMilliseconds(50),
            "without the option the first turn pays for it, as before");
    }

    [Fact]
    public async Task With_routing_the_strong_model_is_warmed_too_with_the_same_cap()
    {
        var strong = new FakeChatClient();
        await using var host = Host(o => o.StrongChatClient = strong);

        await RunWarmUpAsync(host);

        host.Chat!.Calls.Should().ContainSingle();
        var call = strong.Calls.Should().ContainSingle().Subject;
        call.ToolMode.Should().Be(ChatToolMode.None);
        call.MaxOutputTokens.Should().Be(MentorOrchestrator.WarmUpMaxOutputTokens);
        host.Logs.Records.Should().Contain(r => r.Level == LogLevel.Information && r.Message.Contains("StrongChatClient warmed up in"));
    }

    [Fact]
    public async Task A_request_that_fails_leaves_a_warning_and_the_first_turn_works()
    {
        await using var host = Host();
        host.Chat!.ThrowOnNextCall = new HttpRequestException("connection refused");

        await RunWarmUpAsync(host);
        host.Chat.Say("Hello!");
        var error = await host.RunTurnAsync("hello");

        host.Logs.HasWarning("Warming up ChatClient failed").Should().BeTrue(host.Logs.WarningText);
        error.Should().BeNull();
        host.Chat.Calls.Should().HaveCount(2, "the failed warm-up request, then the turn's");
    }

    [Fact]
    public async Task Without_WarmUpAtStartup_the_option_warns_and_calls_nothing()
    {
        await using var host = Host(warmUp: false);

        await RunWarmUpAsync(host);

        host.Logs.HasWarning("WarmUpChatModel has no effect without WarmUpAtStartup").Should().BeTrue();
        host.Chat!.Calls.Should().BeEmpty();
    }
}
