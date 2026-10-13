using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests;

/// <summary>
/// The fakes are load-bearing: several hundred assertions will be written against them, so a fake
/// that lies produces a suite that is confidently wrong. These tests pin the fakes' own contracts
/// before anything is built on top.
/// </summary>
public sealed class FakesSelfTests
{
    // ── FakeChatClient ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Records_the_request_the_library_actually_sent()
    {
        var client = new FakeChatClient().Say("hello");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "sys"), new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { Instructions = "per-request", ConversationId = "conv-1" });

        var call = client.LastCall!;
        call.LastUserText.Should().Be("hi");
        call.SystemText.Should().Be("sys");
        call.Instructions.Should().Be("per-request");
        call.ConversationId.Should().Be("conv-1");
        call.Streaming.Should().BeFalse();
    }

    [Fact]
    public async Task Records_temperature_and_output_cap_for_classifier_assertions()
    {
        var client = new FakeChatClient().Say("SAFE").Say("SAFE");

        await client.GetResponseAsync("x", new ChatOptions { Temperature = 0f, MaxOutputTokens = 16 });
        await client.GetResponseAsync("y");

        client.Calls[0].Temperature.Should().Be(0f);
        client.Calls[0].MaxOutputTokens.Should().Be(16);
        client.Calls[1].Temperature.Should().BeNull("no options is the rc.13 request shape of the combined call");
        client.Calls[1].MaxOutputTokens.Should().BeNull();
    }

    [Fact]
    public async Task A_scripted_conversation_id_reaches_a_streaming_caller()
    {
        // A Responses-API reply carries the service's conversation id; the streaming path is the one the
        // orchestrator uses, so a service-managed host can only be simulated if it arrives there too.
        var client = new FakeChatClient().Respond((_, _) =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { ConversationId = "resp_1" });

        var response = await client.GetStreamingResponseAsync("hi").ToChatResponseAsync();

        response.ConversationId.Should().Be("resp_1");
    }

    [Fact]
    public async Task Records_the_tool_list_so_filtering_can_be_asserted()
    {
        var client = new FakeChatClient().Say("ok");
        var tools = new AITool[]
        {
            AIFunctionFactory.Create(() => 1, name: "alpha"),
            AIFunctionFactory.Create(() => 2, name: "beta"),
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")],
                                      new ChatOptions { Tools = [.. tools] });

        client.LastCall!.ToolNames.Should().Equal("alpha", "beta");
    }

    [Fact]
    public async Task Replays_the_script_in_order_then_repeats_the_last_reply()
    {
        var client = new FakeChatClient().Say("first").Say("second");

        (await client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")])).Text.Should().Be("first");
        (await client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")])).Text.Should().Be("second");

        // Running past the script must not throw: a test that scripts one turn should not have to
        // script the auxiliary calls (safety check, memory capture) it does not care about.
        (await client.GetResponseAsync([new ChatMessage(ChatRole.User, "3")])).Text.Should().Be("second");
        client.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Streams_in_several_chunks_so_ordering_bugs_are_visible()
    {
        var client = new FakeChatClient().Say("one two three");

        var updates = new List<string>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")]))
            if (!string.IsNullOrEmpty(u.Text)) updates.Add(u.Text);

        // A single-chunk "stream" would hide every interleaving defect.
        updates.Count.Should().BeGreaterThan(1);
        string.Concat(updates).Should().Be("one two three");
        client.LastCall!.Streaming.Should().BeTrue();
    }

    [Fact]
    public async Task Streaming_honours_cancellation_between_chunks()
    {
        var client = new FakeChatClient { ChunkDelay = TimeSpan.FromMilliseconds(20) }
            .Say("alpha beta gamma delta");

        using var cts = new CancellationTokenSource();
        var seen = 0;

        var act = async () =>
        {
            await foreach (var u in client.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "x")], cancellationToken: cts.Token))
            {
                if (!string.IsNullOrEmpty(u.Text) && ++seen == 2) await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().Be(2, "the stream must stop at the cancellation, not run to completion");
    }

    [Fact]
    public async Task Throws_once_when_asked_then_recovers()
    {
        var client = new FakeChatClient().Say("after");
        client.ThrowOnNextCall = new InvalidOperationException("boom");

        var act = async () => await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        (await client.GetResponseAsync([new ChatMessage(ChatRole.User, "y")])).Text.Should().Be("after");
    }

    [Fact]
    public async Task Reports_usage_and_model_id_for_metering_assertions()
    {
        var client = new FakeChatClient { ModelId = "gpt-strong" }.SayWithUsage("x", input: 111, output: 222);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);

        response.ModelId.Should().Be("gpt-strong");
        response.Usage!.InputTokenCount.Should().Be(111);
        response.Usage.OutputTokenCount.Should().Be(222);
    }

    [Fact]
    public async Task Streaming_emits_usage_so_metering_works_on_both_paths()
    {
        var client = new FakeChatClient().SayWithUsage("hi there", input: 7, output: 3);

        UsageDetails? usage = null;
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")]))
            usage ??= u.Contents.OfType<UsageContent>().FirstOrDefault()?.Details;

        usage.Should().NotBeNull("a streamed turn is metered from a trailing UsageContent");
        usage!.InputTokenCount.Should().Be(7);
    }

    // ── FakeEmbeddingGenerator ───────────────────────────────────────────────────

    [Fact]
    public async Task Seeded_vectors_give_exact_similarities()
    {
        var gen = new FakeEmbeddingGenerator()
            .OnAxis("cancel my order", axis: 0)
            .OnAxis("also about orders", axis: 0)
            .OnAxis("what is the weather", axis: 3);

        var e = await gen.GenerateAsync(["cancel my order", "also about orders", "what is the weather"]);

        Cosine(e[0], e[1]).Should().BeApproximately(1f, 0.0001f, "same axis means identical direction");
        Cosine(e[0], e[2]).Should().BeApproximately(0f, 0.0001f, "different axes are orthogonal");
    }

    [Theory]
    [InlineData(0.30f)]
    [InlineData(0.35f)]
    [InlineData(0.80f)]
    public async Task Similarity_can_be_placed_exactly_on_a_threshold(float target)
    {
        var gen = new FakeEmbeddingGenerator()
            .OnAxis("anchor", axis: 0)
            .AtSimilarity("candidate", axis: 0, cosine: target);

        var e = await gen.GenerateAsync(["anchor", "candidate"]);

        // Being able to sit exactly on the boundary is the point: a threshold test that cannot
        // reach the boundary never proves which side is inclusive.
        Cosine(e[0], e[1]).Should().BeApproximately(target, 0.0001f);
    }

    [Fact]
    public async Task Unseeded_text_embeds_identically_every_time()
    {
        var a = await new FakeEmbeddingGenerator().GenerateAsync(["not registered"]);
        var b = await new FakeEmbeddingGenerator().GenerateAsync(["not registered"]);

        // String.GetHashCode is randomised per process, which would make threshold tests flaky
        // across runs. The FNV hash used instead has to be stable.
        a[0].Vector.ToArray().Should().Equal(b[0].Vector.ToArray());
    }

    [Fact]
    public async Task Counts_calls_and_inputs_for_embedding_metering()
    {
        var gen = new FakeEmbeddingGenerator();

        await gen.GenerateAsync(["a", "b", "c"]);
        await gen.GenerateAsync(["d"]);

        gen.CallCount.Should().Be(2);
        gen.InputCount.Should().Be(4, "a batched call still embeds every input");
    }

    // ── RecordingStateService ────────────────────────────────────────────────────

    [Fact]
    public void Records_the_order_of_notifications_not_just_the_final_state()
    {
        var state = new RecordingStateService();

        state.NotifyBusyChanged(true);
        state.NotifyStreamingChunk("Hello ");
        state.NotifyStreamingChunk("world");
        state.NotifyStreamingCompleted();
        state.NotifyBusyChanged(false);

        state.Sequence.Should().Equal(
            nameof(state.NotifyBusyChanged),
            nameof(state.NotifyStreamingChunk),
            nameof(state.NotifyStreamingChunk),
            nameof(state.NotifyStreamingCompleted),
            nameof(state.NotifyBusyChanged));

        state.StreamedText.Should().Be("Hello world");
        state.Count(nameof(state.NotifyStreamingChunk)).Should().Be(2);
    }

    [Fact]
    public void Still_forwards_to_subscribers_while_recording()
    {
        var state = new RecordingStateService();
        var received = new List<string>();
        state.OnStreamingChunk += received.Add;

        state.NotifyStreamingChunk("a");
        state.NotifyStreamingChunk("b");

        // Note the array form: Equal(params T[]) would read a trailing "because" string as one more
        // expected element.
        received.Should().Equal(["a", "b"], "a component under test must behave normally");
    }

    [Fact]
    public async Task Captures_approval_answers_with_their_verdict()
    {
        var state = new RecordingStateService();
        var approve = Guid.NewGuid();
        var reject = Guid.NewGuid();

        await state.ConfirmAsync(approve);
        state.Cancel(reject);

        state.Answers.Should().Equal([(approve, true), (reject, false)]);
    }

    [Fact]
    public async Task Raises_OnApprovalResponse_only_for_a_pending_request()
    {
        var state = new RecordingStateService();
        var request = new ConfirmationRequest
        {
            ActionId = Guid.NewGuid(), ToolName = "cancel_order", Message = "Cancel order 7?",
        };

        var responses = new List<(ConfirmationRequest Request, bool Approved)>();
        state.OnApprovalResponse += (r, ok) => responses.Add((r, ok));

        state.NotifyConfirmationRequired(request);
        await state.ConfirmAsync(request.ActionId);

        // Answering twice — a double-click, or a stale dialog answered after the fact — must not
        // raise a second time, exactly as the real service refuses to resolve an unknown id.
        await state.ConfirmAsync(request.ActionId);
        await state.ConfirmAsync(Guid.NewGuid());

        responses.Should().ContainSingle();
        responses[0].Request.ToolName.Should().Be("cancel_order");
        responses[0].Approved.Should().BeTrue();
    }

    [Fact]
    public void Is_safe_to_notify_from_several_threads()
    {
        var state = new RecordingStateService();

        // Chunks arrive off the SignalR connection, so the recorder must not be the thing that
        // breaks under the concurrency it is meant to observe.
        Parallel.For(0, 500, i => state.NotifyStreamingChunk(i.ToString()));

        state.Count(nameof(state.NotifyStreamingChunk)).Should().Be(500);
    }

    private static float Cosine(Embedding<float> a, Embedding<float> b)
    {
        var x = a.Vector.ToArray();
        var y = b.Vector.ToArray();
        var dot = x.Zip(y, (p, q) => p * q).Sum();
        var len = MathF.Sqrt(x.Sum(p => p * p)) * MathF.Sqrt(y.Sum(q => q * q));
        return len == 0 ? 0 : dot / len;
    }
}
