using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Metrics;

/// <summary>
/// The model calls MentorAgent makes on its own behalf — and whether the dashboard knows about them.
///
/// <para>
/// Three features send a request to the model outside the coordinator's own turn: post-turn memory
/// auto-capture (<c>CaptureUserFactsAsync</c>, <b>on by default</b> whenever memory is enabled), the
/// input safety check, and the hosted-tool domain gate. All three go through
/// <c>MentorOrchestrator.GetChatClientForAux()</c>, which returns <c>_options.ChatClient</c> — the
/// <b>raw, host-supplied client</b>. Nothing in the library ever reassigns that property, so those
/// calls bypass the pipeline the orchestrator builds, and with it
/// <see cref="Novior.MentorAgent.Middleware.TokenUsageChatClient"/>.
/// </para>
///
/// <para>
/// Bypassing the <em>agent</em> pipeline is deliberate and correct — the domain gate's own comment
/// explains that routing it back through the middleware "would recurse straight back into here".
/// But metering is a plain delegating wrapper with nothing to recurse into, and the consequence of
/// skipping it is the same one Part D has now found three times: a real, billed call missing from
/// the page built to show spend. Memory auto-capture fires on **every turn**, so this is not an edge
/// case — it is a fixed per-turn cost that no operator can see.
/// </para>
///
/// <para>
/// The property asserted is deliberately blunt and hard to satisfy by accident: **every call the
/// provider actually served is counted exactly once.** The fake records every request that reaches
/// it, whichever path sent it, so its own call count is the ground truth to measure the metrics
/// against.
/// </para>
/// </summary>
public sealed class AuxiliaryCallMeteringTests
{
    private static long MeteredChatRequests(MentorHostFixture host)
        => host.Get<MentorMetrics>()
               .GetSnapshot().Models
               .Where(m => m.Kind != Abstractions.Models.MentorModelKind.Embedding)
               .Sum(m => m.RequestCount);

    [Fact]
    public async Task Every_call_the_provider_served_is_metered_once()
    {
        // MemoryAutoCapture is on by default, so an ordinary turn already makes the extra call this
        // is about — no special configuration, which is the point.
        await using var host = MentorHostFixture.Create(configure: o => o.UseMemoryContext = true);
        host.Chat!.Say("Certo, ci penso io.");

        await host.RunTurnAsync("Ciao, mi chiamo Antonio e lavoro nel team vendite.");

        var served = host.Chat.Calls.Count;
        served.Should().BeGreaterThan(1,
            "auto-capture makes a second call after the turn — if this is 1 the fixture changed and "
            + "the rest of this test is measuring nothing");

        MeteredChatRequests(host).Should().Be(served,
            "a request that reached the provider was billed, whoever inside MentorAgent sent it");
    }

    [Fact]
    public async Task The_post_turn_memory_call_is_the_one_that_goes_missing()
    {
        // Names the specific gap, so a regression report says which feature stopped being counted
        // rather than just "the numbers disagree".
        await using var host = MentorHostFixture.Create(configure: o => o.UseMemoryContext = true);
        host.Chat!.Say("Certo.");

        await host.RunTurnAsync("Mi chiamo Antonio.");

        var extraction = host.Chat.Calls.Count(c =>
            (c.Messages.FirstOrDefault()?.Text ?? "").Contains("Extract durable facts"));
        extraction.Should().Be(1, "auto-capture ran exactly once for this turn");

        MeteredChatRequests(host).Should().BeGreaterThanOrEqualTo(extraction + 1,
            "the coordinator's own call plus the extraction call both reached the model");
    }

    [Fact]
    public async Task Turning_auto_capture_off_removes_the_call_rather_than_hiding_it()
    {
        // The control: with the feature off there is no extra call, so a fix that merely inflated the
        // counter by one would fail here.
        await using var host = MentorHostFixture.Create(configure: o =>
        {
            o.UseMemoryContext = true;
            o.MemoryAutoCapture = false;
        });
        host.Chat!.Say("Certo.");

        await host.RunTurnAsync("Mi chiamo Antonio.");

        host.Chat.Calls.Should().NotContain(c =>
            (c.Messages.FirstOrDefault()!.Text ?? "").Contains("Extract durable facts"));
        MeteredChatRequests(host).Should().Be(host.Chat.Calls.Count);
    }
}
