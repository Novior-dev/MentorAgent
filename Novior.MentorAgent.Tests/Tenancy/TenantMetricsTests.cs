using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Novior.MentorAgent.Tests.Tenancy;

/// <summary>
/// Story 156 — the metrics of two tenants on one instance, in a Blazor circuit: what a tenant's turn records counts in the
/// instance's figures and in that tenant's, never in another's, and every OpenTelemetry series and the turn's span name the
/// tenant. Without a resolver the series carry no tenant. The dashboard is in <c>TenantDashboardTests</c>; the admin
/// endpoint and the MCP server in <c>Server/TenantMetricsSurfaceTests</c>.
/// </summary>
public sealed class TenantMetricsTests
{
    private static MentorHostFixture Host(bool resolver = true, Action<MentorOptions>? configure = null)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            configure?.Invoke(o);
        }, s =>
        {
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
        });

    private static TestTenantResolver Resolver(MentorHostFixture host) => (TestTenantResolver)host.Get<IMentorTenantResolver>();

    /// <summary>Another scope of the same application — another circuit — whose requests belong to <paramref name="tenant"/>.</summary>
    private static AsyncServiceScope ScopeOf(MentorHostFixture host, string tenant)
    {
        Resolver(host).Resolve = _ => tenant;
        return host.Get<IServiceScopeFactory>().CreateAsyncScope();
    }

    /// <summary>Contoso's turn calls an action and costs 1000 + 500 tokens; fabrikam's, in another circuit, 7 + 3.</summary>
    private static async Task TwoTenantsTalkAsync(MentorHostFixture host)
    {
        host.Auth.SignIn("ada");
        Resolver(host).Resolve = _ => "contoso";
        host.Chat!.CallTool("count_open_orders").SayWithUsage("There are two.", 1_000, 500).SayWithUsage("Ok.", 7, 3);
        await host.RunTurnAsync("How many open orders?");

        await using var fabrikam = ScopeOf(host, "fabrikam");
        await fabrikam.ServiceProvider.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("ciao");
    }

    // ── The dashboard's figures ─────────────────────────────────────────────────

    [Fact]
    public async Task A_tenants_turn_counts_in_its_own_figures_and_in_the_instances_never_in_anothers()
    {
        await using var host = Host();
        await TwoTenantsTalkAsync(host);
        var metrics = host.Get<IMentorMetrics>();

        var contoso = metrics.GetSnapshot("contoso")!;
        var fabrikam = metrics.GetSnapshot("fabrikam")!;
        var instance = metrics.GetSnapshot();

        contoso.TurnCount.Should().Be(1);
        contoso.TopActions.Select(a => a.Action).Should().Equal(["count_open_orders"]);
        contoso.TotalInputTokens.Should().BeGreaterThanOrEqualTo(1_000);

        fabrikam.TurnCount.Should().Be(1);
        fabrikam.ServedCount.Should().Be(1);
        fabrikam.TopActions.Should().BeEmpty("the action ran in contoso");
        fabrikam.TotalInputTokens.Should().Be(7, "contoso's tokens are not fabrikam's");
        fabrikam.TotalOutputTokens.Should().Be(3);

        instance.TurnCount.Should().Be(2);
        instance.TotalInputTokens.Should().Be(contoso.TotalInputTokens + fabrikam.TotalInputTokens);
        instance.TopActions.Select(a => a.Action).Should().Equal(["count_open_orders"]);
        metrics.GetSnapshot(null).Should().BeEquivalentTo(instance, o => o.Excluding(s => s.GeneratedAt));
    }

    [Fact]
    public async Task A_tenant_with_no_use_yet_reads_zero_with_the_configured_models_not_the_instances_figures()
    {
        await using var host = Host();
        await TwoTenantsTalkAsync(host);

        var northwind = host.Get<IMentorMetrics>().GetSnapshot("northwind")!;

        northwind.TurnCount.Should().Be(0);
        northwind.TotalInputTokens.Should().Be(0);
        northwind.TopActions.Should().BeEmpty();
        northwind.Models.Should().ContainSingle(m => m.ModelId == "fake-model").Which.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task The_turns_tenant_ends_with_the_turn()
    {
        await using var host = Host();
        await TwoTenantsTalkAsync(host);

        MentorTenantContext.Ambient.Should().BeNull("the turn entered its tenant in its own flow, not in its caller's");
        host.Get<MentorMetrics>().RecordToolCall("after_the_turn");
        host.Get<IMentorMetrics>().GetSnapshot("contoso")!.TopActions.Select(a => a.Action)
            .Should().NotContain("after_the_turn");
    }

    [Fact]
    public async Task Reset_clears_every_tenants_figures_too()
    {
        await using var host = Host();
        await TwoTenantsTalkAsync(host);
        var metrics = host.Get<IMentorMetrics>();

        metrics.Reset();

        metrics.GetSnapshot("contoso")!.TurnCount.Should().Be(0);
        metrics.GetSnapshot()!.TurnCount.Should().Be(0);
    }

    [Fact]
    public async Task Without_a_resolver_a_tenant_reads_nothing_and_the_instance_reads_everything()
    {
        await using var host = Host(resolver: false);
        host.Chat!.SayRepeatedly("Ok.", 2);
        await host.RunTurnAsync("uno");
        await host.RunTurnAsync("due");
        var metrics = host.Get<IMentorMetrics>();

        metrics.GetSnapshot().TurnCount.Should().Be(2);
        metrics.GetSnapshot(null)!.TurnCount.Should().Be(2);
        metrics.GetSnapshot("contoso")!.TurnCount.Should().Be(0, "no turn entered a tenant");
    }

    // ── The defaults of the interfaces ──────────────────────────────────────────

    [Fact]
    public void An_IMentorMetrics_that_keeps_nothing_per_tenant_shows_a_tenant_nothing_rather_than_the_instance()
    {
        IMentorMetrics metrics = new InstanceOnlyMetrics();

        metrics.GetSnapshot(null).Should().BeSameAs(InstanceOnlyMetrics.Instance);
        metrics.GetSnapshot("contoso").Should().BeNull();
    }

    [Fact]
    public async Task An_IMentorMetricsStore_that_queries_the_instance_is_not_asked_for_a_tenant()
    {
        IMentorMetricsStore store = new InstanceOnlyStore();

        (await store.QueryAsync((string?)null)).Should().BeSameAs(InstanceOnlyMetrics.Instance);
        (await store.QueryAsync("contoso")).Should().BeNull("the caller serves the tenant's in-RAM figures instead");
    }

    private sealed class InstanceOnlyMetrics : IMentorMetrics
    {
        internal static readonly MentorMetricsSnapshot Instance = new() { TurnCount = 42 };

        public void RecordTurn() { }
        public void RecordServed() { }
        public void RecordError() { }
        public void RecordGuardrailBlock() { }
        public void RecordToolCall(string toolName) { }
        public MentorMetricsSnapshot GetSnapshot() => Instance;
        public void Reset() { }
    }

    private sealed class InstanceOnlyStore : IMentorMetricsStore
    {
        public Task<MentorMetricsSnapshot?> QueryAsync(CancellationToken ct = default)
            => Task.FromResult<MentorMetricsSnapshot?>(InstanceOnlyMetrics.Instance);
    }

    // ── OpenTelemetry ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_counter_and_the_turns_span_name_the_tenant()
    {
        using var telemetry = new Recorded();
        await using var host = Host(configure: o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = telemetry.Source;
            o.InputGuardrail = (message, _) => Task.FromResult(message != "blocked");
        });
        host.Auth.SignIn("ada");
        Resolver(host).Resolve = _ => "contoso";

        host.Chat!.CallTool("count_open_orders").Say("There are two.");
        await host.RunTurnAsync("How many open orders?");
        await host.RunTurnAsync("blocked");
        host.Chat.Respond((_, _) => throw new InvalidOperationException("The model is down."));
        await host.RunTurnAsync("And now?");

        foreach (var instrument in new[] { "mentoragent.turns", "mentoragent.tool_calls", "mentoragent.errors", "mentoragent.guardrail_blocks" })
            telemetry.Measured.Where(m => m.Instrument == instrument).Should().NotBeEmpty(instrument)
                .And.OnlyContain(m => m.Tenant == "contoso", instrument);
        telemetry.Measured.Should().Contain(m => m.Instrument == "mentoragent.tool_calls" && m.Tool == "count_open_orders",
            "the tool's tag stays beside the tenant's");
        telemetry.Spans.Where(s => s.OperationName == "mentor.turn").Should().HaveCount(2)
            .And.OnlyContain(s => (string?)s.GetTagItem("mentoragent.tenant") == "contoso");
    }

    [Fact]
    public async Task A_refusal_out_of_scope_and_a_reply_withheld_name_the_tenant_too()
    {
        using var telemetry = new Recorded();
        var classifier = new FakeChatClient();
        classifier.Say("SAFE REFUSE").Say("SAFE");
        await using var host = Host(configure: o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = telemetry.Source;
            o.RefuseOutOfScope = true;
            o.EnableSafetyCheck = true;
            o.ClassifierChatClient = classifier;
            o.OutputGuardrail = (reply, _) => Task.FromResult(!reply.Contains("forbidden"));
        });
        host.Auth.SignIn("ada");
        Resolver(host).Resolve = _ => "contoso";

        await host.RunTurnAsync("scrivimi una poesia sul mare");
        host.Chat!.Say("Something forbidden.");
        await host.RunTurnAsync("How many open orders?");

        telemetry.Measured.Where(m => m.Instrument == "mentoragent.guardrail_blocks").Should().HaveCount(2)
            .And.OnlyContain(m => m.Tenant == "contoso");
        host.Get<IMentorMetrics>().GetSnapshot("contoso")!.GuardrailBlockCount.Should().Be(2);
    }

    [Fact]
    public async Task Without_a_resolver_no_series_and_no_span_carries_a_tenant()
    {
        using var telemetry = new Recorded();
        await using var host = Host(resolver: false, configure: o =>
        {
            o.EnableObservability = true;
            o.ObservabilitySourceName = telemetry.Source;
        });

        host.Chat!.CallTool("count_open_orders").Say("There are two.");
        await host.RunTurnAsync("How many open orders?");

        telemetry.Measured.Should().Contain(m => m.Instrument == "mentoragent.turns")
            .And.Contain(m => m.Instrument == "mentoragent.tool_calls")
            .And.OnlyContain(m => !m.Tagged);
        telemetry.Spans.Should().Contain(s => s.OperationName == "mentor.turn")
            .And.OnlyContain(s => s.GetTagItem("mentoragent.tenant") == null);
    }

    /// <summary>The measurements and spans of a source of this test's own, apart from every other test's.</summary>
    internal sealed class Recorded : IDisposable
    {
        private readonly MeterListener _meters;
        private readonly ActivityListener _spans;
        private readonly ConcurrentQueue<(string Instrument, string? Tenant, string? Tool, bool Tagged)> _measured = new();
        private readonly ConcurrentQueue<Activity> _stopped = new();

        internal string Source { get; } = "MentorAgent.Tenancy." + Guid.NewGuid().ToString("N");

        internal IReadOnlyList<(string Instrument, string? Tenant, string? Tool, bool Tagged)> Measured => [.. _measured];

        internal IReadOnlyList<Activity> Spans => [.. _stopped];

        internal Recorded()
        {
            _meters = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == Source) listener.EnableMeasurementEvents(instrument);
                },
            };
            _meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => _measured.Enqueue(Read(instrument.Name, tags)));
            _meters.Start();

            _spans = new ActivityListener
            {
                ShouldListenTo = s => s.Name == Source,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_spans);
        }

        private static (string, string?, string?, bool) Read(string instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? tenant = null, tool = null;
            var tagged = false;
            foreach (var (key, value) in tags)
            {
                if (key == "mentoragent.tenant") (tenant, tagged) = (value as string, true);
                if (key == "mentoragent.tool") tool = value as string;
            }
            return (instrument, tenant, tool, tagged);
        }

        public void Dispose()
        {
            _meters.Dispose();
            _spans.Dispose();
        }
    }
}
