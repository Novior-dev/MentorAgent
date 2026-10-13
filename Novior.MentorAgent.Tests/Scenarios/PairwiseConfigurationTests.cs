using FluentAssertions;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Scenarios;

/// <summary>
/// Part C2.3 — every pair of option values appears in at least one booted, working configuration.
///
/// <para>
/// The honest arithmetic first: <c>MentorOptions</c> has 34 booleans, so the exhaustive answer is
/// 2³⁴ configurations before the enums are counted. Anyone promising "all combinations" is either
/// not counting or not testing. What is achievable, and what empirically catches the large majority
/// of interaction defects, is <b>all-pairs</b>: for every two options, every combination of their
/// values occurs somewhere in the set. That collapses the space to a couple of dozen rows.
/// </para>
///
/// <para>
/// The generator is a deterministic greedy set-cover over a fixed factor order — no seed, no
/// randomness, so a failure is reproducible from the configuration index alone rather than being a
/// lottery. Each row must do three things: register without throwing, produce a coherent tool list,
/// and complete one scripted turn.
/// </para>
///
/// <para>
/// This is the sweep that would have caught BUG-028 without anyone suspecting the option: a
/// coordinator that fails to build returns no error to the caller, so the only way to see it is to
/// boot the configuration and check that a turn actually reached the model.
/// </para>
/// </summary>
public sealed class PairwiseConfigurationTests
{
    // ── The factor space ────────────────────────────────────────────────────────

    /// <summary>One option and the values it takes, each with a label that names the row.</summary>
    private sealed record Factor(string Name, (string Label, Action<MentorOptions> Apply)[] Values);

    /// <summary>
    /// Deliberately Path A only. Path B is a different fixture shape (a pre-built agent instead of a
    /// ChatClient) and its eleven documented differences are C2.2's axis, walked explicitly by
    /// <c>PathParityTests</c> rather than sampled here.
    /// </summary>
    private static readonly Factor[] Factors =
    [
        new("ToolFiltering", [("off", o => o.EnableToolFiltering = false),
                              ("on",  o => o.EnableToolFiltering = true)]),
        new("Memory",        [("off", o => o.UseMemoryContext = false),
                              ("on",  o => o.UseMemoryContext = true)]),
        new("AutoCapture",   [("off", o => o.MemoryAutoCapture = false),
                              ("on",  o => o.MemoryAutoCapture = true)]),
        new("Rag",           [("off", o => o.UseRag = false),
                              ("on",  o => { o.UseRag = true; o.RagMinScore = 0.5f; })]),
        new("RagChips",      [("off", o => o.ShowRagSources = false),
                              ("on",  o => o.ShowRagSources = true)]),
        new("Compaction",    [("off", o => o.EnableCompaction = false),
                              ("on",  o => o.EnableCompaction = true)]),
        new("Skills",        [("off", o => o.EnableSkills = false),
                              ("on",  o => o.EnableSkills = true)]),
        new("Cards",         [("off", o => o.EnableGenerativeCards = false),
                              ("on",  o => o.EnableGenerativeCards = true)]),
        new("RichResponses", [("off", o => o.EnableRichResponses = false),
                              ("on",  o => o.EnableRichResponses = true)]),
        new("SafetyCheck",   [("off", o => o.EnableSafetyCheck = false),
                              ("on",  o => o.EnableSafetyCheck = true)]),
        // Routing is inert without a StrongChatClient, so "off" is simply not supplying one — the
        // strategy enum has no None member.
        new("Routing",       [("off",    _ => { }),
                              ("custom", o => { o.RoutingStrategy = MentorRoutingStrategy.Custom;
                                                o.UseStrongModel = _ => true; })]),
        new("Hitl",          [("blocking", o => o.HitlMode = MentorHitlMode.Blocking),
                              ("native",   o => o.HitlMode = MentorHitlMode.Native)]),
        new("Level",         [("minimal",   o => o.MentorshipLevel = MentorshipLevel.Minimal),
                              ("standard",  o => o.MentorshipLevel = MentorshipLevel.Standard),
                              ("proactive", o => o.MentorshipLevel = MentorshipLevel.Proactive)]),
        new("Language",      [("en", o => o.Language = MentorLanguage.English),
                              ("it", o => o.Language = MentorLanguage.Italian),
                              ("ja", o => o.Language = MentorLanguage.Japanese)]),
        new("RateLimit",     [("off", o => o.RateLimitPerUser = 0),
                              ("on",  o => { o.RateLimitPerUser = 5; o.RateLimitWindowSecs = 60; })]),
        new("HistoryCap",    [("default", o => o.MaxSessionMessages = 50),
                              ("tight",   o => o.MaxSessionMessages = 4)]),
        new("Images",        [("off", o => o.EnableImageInput = false),
                              ("on",  o => o.EnableImageInput = true)]),
    ];

    // ── The generator ───────────────────────────────────────────────────────────

    /// <summary>
    /// Greedy all-pairs cover. For each row, every factor takes the value that covers the most
    /// still-uncovered pairs against the values already fixed in that row; ties break on the first
    /// value, so the whole set is a pure function of <see cref="Factors"/>.
    /// </summary>
    private static List<int[]> BuildCover()
    {
        var uncovered = new HashSet<(int A, int Va, int B, int Vb)>();
        for (var a = 0; a < Factors.Length; a++)
            for (var b = a + 1; b < Factors.Length; b++)
                for (var va = 0; va < Factors[a].Values.Length; va++)
                    for (var vb = 0; vb < Factors[b].Values.Length; vb++)
                        uncovered.Add((a, va, b, vb));

        var rows = new List<int[]>();

        // How often each value has been used, per factor. This is the tie-break, and it is what
        // makes the algorithm converge: scoring on gain alone leaves every factor tied at zero once
        // its own pairs are covered, "first value" wins every time, and the generator then emits the
        // same row forever without ever reaching the pairs that need a later value.
        var used = Factors.Select(f => new int[f.Values.Length]).ToArray();

        // The bound is a safety net, not a target: with these factors the cover closes in well under
        // it, and a runaway generator would otherwise hang the suite rather than fail it.
        while (uncovered.Count > 0 && rows.Count < 200)
        {
            var row = new int[Factors.Length];
            for (var f = 0; f < Factors.Length; f++)
            {
                var best = 0;
                var bestGain = -1;
                var bestUse = int.MaxValue;
                for (var v = 0; v < Factors[f].Values.Length; v++)
                {
                    var gain = 0;
                    for (var g = 0; g < f; g++)
                        if (uncovered.Contains((g, row[g], f, v))) gain++;

                    if (gain > bestGain || (gain == bestGain && used[f][v] < bestUse))
                    {
                        bestGain = gain;
                        bestUse = used[f][v];
                        best = v;
                    }
                }
                row[f] = best;
                used[f][best]++;
            }

            for (var a = 0; a < Factors.Length; a++)
                for (var b = a + 1; b < Factors.Length; b++)
                    uncovered.Remove((a, row[a], b, row[b]));

            rows.Add(row);
        }

        return rows;
    }

    private static readonly List<int[]> Cover = BuildCover();

    private static string Label(int[] row)
        => string.Join(" ", row.Select((v, f) => $"{Factors[f].Name}={Factors[f].Values[v].Label}"));

    public static TheoryData<int> Configurations
    {
        get
        {
            var data = new TheoryData<int>();
            for (var i = 0; i < Cover.Count; i++) data.Add(i);
            return data;
        }
    }

    // ── The cover itself ────────────────────────────────────────────────────────

    [Fact]
    public void Every_pair_of_option_values_occurs_in_some_configuration()
    {
        // Guards the theory below. A generator that silently produced one row would make every
        // configuration test pass while covering almost nothing.
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var row in Cover)
            for (var a = 0; a < Factors.Length; a++)
                for (var b = a + 1; b < Factors.Length; b++)
                    seen.Add((a, row[a], b, row[b]));

        var expected = 0;
        for (var a = 0; a < Factors.Length; a++)
            for (var b = a + 1; b < Factors.Length; b++)
                expected += Factors[a].Values.Length * Factors[b].Values.Length;

        seen.Count.Should().Be(expected, "all-pairs means all pairs");
    }

    [Fact]
    public void The_cover_is_small_enough_to_be_worth_running()
    {
        // 17 factors is 2¹⁵ × 3² configurations exhaustively — 294,912. If the cover ever grew past
        // a few dozen the generator would have stopped being a generator.
        Cover.Should().HaveCountLessThan(40);
        Cover.Should().HaveCountGreaterThan(5);
    }

    // ── Every configuration boots and answers ───────────────────────────────────

    /// <summary>Boots one row of the cover.</summary>
    private static MentorHostFixture Boot(int[] row)
    {
        var host = MentorHostFixture.Create(
            withEmbeddings: true,
            configure: o =>
            {
                foreach (var (f, factor) in Factors.Index())
                    factor.Values[row[f]].Apply(o);

                // Supplied whenever routing is on, so the "custom strategy with no strong client"
                // degradation is not what is being measured here — C2.1 owns that constraint.
                if (o.UseStrongModel is not null)
                    o.StrongChatClient = new FakeChatClient { ModelId = "strong-model" }.Say("ok");
            },
            configureServices: s => s.AddSingleton<IMentorRagSource>(
                new ScriptedRagSource().Returns("A retrieved document.", 0.9f, "Doc")));

        // One fallback response covers every call the configuration makes — the safety check, the
        // post-turn memory extraction and the turn itself all fall through to it.
        host.Chat!.Say("ok");

        return host;
    }

    /// <summary>
    /// Every call either client received, whichever one routing chose.
    ///
    /// <para>
    /// The strong client is read back out of the <b>resolved</b> options rather than captured while
    /// building them: <c>configure</c> runs lazily, on the first read of <c>IOptions.Value</c>, so a
    /// variable assigned inside it is still null when the boot helper returns. Nine rows of this
    /// suite reported "the turn never reached a model" for that reason while the log plainly said
    /// <i>Tokens — model: strong-model</i>.
    /// </para>
    /// </summary>
    private static List<FakeChatClient.RecordedCall> AllCalls(MentorHostFixture host)
        => [.. host.Chat!.Calls, .. (host.Options.StrongChatClient as FakeChatClient)?.Calls ?? []];

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task Every_configuration_registers_and_completes_a_turn(int index)
    {
        var row = Cover[index];
        var label = Label(row);

        var host = Boot(row);
        await using var _ = host;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = await host.RunTurnAsync("how many open orders are there", deadline.Token);

        var log = string.Join('\n', host.Logs.Records.Select(r => $"{r.Level}:{r.Message}"));

        // Checked first: a coordinator that fails to build returns no error to the caller, so this
        // line is the only thing that names the cause (BUG-011, BUG-028).
        log.Should().NotContain("Initialization failed", $"configuration: {label}");

        error.Should().BeNull($"configuration: {label}");
        AllCalls(host).Should().NotBeEmpty($"the turn must reach a model — {label}");
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task Every_configuration_sends_the_model_a_usable_tool_list(int index)
    {
        // "It did not throw" is a low bar. The tool list is what the assistant can actually do, and
        // BUG-013 is the standing proof that it can be empty while everything else looks healthy —
        // a model with no tools just talks.
        var row = Cover[index];
        var label = Label(row);

        var host = Boot(row);
        await using var _ = host;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.RunTurnAsync("how many open orders are there", deadline.Token);

        // The coordinator's own call — with MemoryAutoCapture on, the last call is the tool-less
        // fact extraction rather than the turn.
        var coordinator = AllCalls(host).FirstOrDefault(c => c.Tools.Count > 0);

        coordinator.Should().NotBeNull(
            $"every configuration must offer the model something to call — {label}");
        coordinator!.ToolNames.Should().Contain("navigate_to",
            $"the core tools survive every combination of options — {label}");
    }
}
