using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Tenancy;

/// <summary>
/// Story 157 — the search and the logs of a turn know its tenant. A RAG source is given it in
/// <see cref="MentorRagRequest.TenantId"/>, and one that implements only the text overload, which never sees it, is
/// warned about once. Every log line of the turn, the application's own included, is written in a scope that names the
/// tenant, with the key of the span and metric tag. Without a resolver nothing changes. The MCP server's lines are in
/// <c>Server/TenantMetricsSurfaceTests</c>.
/// </summary>
public sealed class TenantRagAndLogTests
{
    private const string TenantKey = "mentoragent.tenant";

    private static MentorHostFixture Host(IMentorRagSource source, bool resolver = true)
        => MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = false;   // its question would take a reply scripted for a turn
            o.UseRag = true;
        }, s =>
        {
            s.AddSingleton(source);
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
        });

    private static TestTenantResolver Resolver(MentorHostFixture host) => (TestTenantResolver)host.Get<IMentorTenantResolver>();

    /// <summary>The tenants named by the scopes a log record was written in.</summary>
    private static List<string?> Tenants(IReadOnlyList<object> scopes)
        => [.. scopes.OfType<IReadOnlyList<KeyValuePair<string, object?>>>()
               .SelectMany(s => s).Where(p => p.Key == TenantKey).Select(p => p.Value as string)];

    /// <summary>A source that reads the whole request, as one that holds several tenants' documents does.</summary>
    private sealed class RequestSource : IMentorRagSource
    {
        internal List<MentorRagRequest> Requests { get; } = [];

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(MentorRagRequest request, CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult<IReadOnlyList<MentorRagResult>>([]);
        }

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
            => throw new InvalidOperationException("MentorAgent calls the request overload");
    }

    /// <summary>A source written for the text overload only: it never sees the tenant.</summary>
    private sealed class TextOnlySource : IMentorRagSource
    {
        internal int Searches;

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Searches);
            return Task.FromResult<IReadOnlyList<MentorRagResult>>([]);
        }
    }

    // ── The search ───────────────────────────────────────────────────────────────

    /// <summary>The tenant a source is given is the one <see cref="IMentorTenant.Id"/> names in the turn's scope.</summary>
    [Fact]
    public async Task A_rag_source_is_given_the_tenant_of_each_turn()
    {
        var source = new RequestSource();
        await using var host = Host(source);
        host.Chat!.Say("Ok.").Say("Ok.");

        Resolver(host).Resolve = _ => "contoso";
        await host.RunTurnAsync("delivery times?");
        var contoso = host.Get<IMentorTenant>().Id;

        Resolver(host).Resolve = _ => "fabrikam";
        string? fabrikamId;
        await using (var fabrikam = host.Get<IServiceScopeFactory>().CreateAsyncScope())
        {
            await fabrikam.ServiceProvider.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("delivery times?");
            fabrikamId = fabrikam.ServiceProvider.GetRequiredService<IMentorTenant>().Id;
        }

        source.Requests.Select(r => r.TenantId).Should().Equal(["contoso", "fabrikam"], "each circuit's turn, its own tenant");
        source.Requests.Select(r => r.TenantId).Should().Equal([contoso, fabrikamId], "the tenant IMentorTenant names there");
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("is never given the tenant"),
            "the source reads the request");
    }

    [Fact]
    public async Task Without_a_resolver_the_source_is_given_no_tenant()
    {
        var source = new RequestSource();
        await using var host = Host(source, resolver: false);
        host.Chat!.Say("Ok.");

        await host.RunTurnAsync("delivery times?");

        source.Requests.Should().ContainSingle().Which.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task A_source_with_only_the_text_overload_still_searches_and_is_warned_about_once()
    {
        var source = new TextOnlySource();
        await using var host = Host(source);
        Resolver(host).Resolve = _ => "contoso";
        host.Chat!.Say("Ok.").Say("Ok.");

        await host.RunTurnAsync("delivery times?");
        await host.RunTurnAsync("and returns?");

        source.Searches.Should().Be(2, "nothing changes for the search itself");
        host.Logs.Records.Where(r => r.Level == LogLevel.Warning && r.Message.Contains("is never given the tenant"))
            .Should().ContainSingle("once per type of source").Which.Message.Should().Contain(nameof(TextOnlySource));
    }

    [Fact]
    public async Task Without_a_resolver_a_source_with_only_the_text_overload_is_not_warned_about()
    {
        var source = new TextOnlySource();
        await using var host = Host(source, resolver: false);
        host.Chat!.Say("Ok.");

        await host.RunTurnAsync("delivery times?");

        host.Logs.Records.Should().NotContain(r => r.Message.Contains("is never given the tenant"));
    }

    [Fact]
    public void Whether_a_source_reads_the_request_is_read_from_its_type()
    {
        RagContextProvider.ReadsTheRequest(typeof(RequestSource)).Should().BeTrue();
        RagContextProvider.ReadsTheRequest(typeof(TextOnlySource)).Should().BeFalse();
        RagContextProvider.ReadsTheRequest(typeof(ScriptedRagSource)).Should().BeFalse();
    }

    // ── The logs ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_line_of_a_tenants_turn_names_the_tenant_the_applications_own_included()
    {
        var source = new RequestSource();
        await using var host = Host(source);
        var app = host.Get<ILoggerFactory>().CreateLogger("Application");
        Resolver(host).Resolve = _ => "contoso";
        host.Chat!.Respond((_, _) =>
            {
                app.LogInformation("The application's own line, in the turn.");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "count_open_orders")])) { ModelId = host.Chat.ModelId };
            })
            .Say("There are two.");

        app.LogInformation("Before the turn.");
        await host.RunTurnAsync("how many open orders?");
        app.LogInformation("After the turn.");

        var lines = host.Logs.ScopedRecords;
        Line("The application's own line").Should().Equal(["contoso"]);
        Line("[MentorAgent] Tool call: count_open_orders").Should().Equal(["contoso"], "the gate's line");
        Line("[MentorAgent:RAG] Prepared").Should().Equal(["contoso"], "the search's line");
        Line("Before the turn.").Should().BeEmpty();
        Line("After the turn.").Should().BeEmpty("the scope ends with the turn");

        var scope = lines.First(l => l.Message.Contains("Tool call: count_open_orders")).Scopes
            .OfType<IReadOnlyList<KeyValuePair<string, object?>>>().Single(s => s.Any(p => p.Key == TenantKey));
        scope.Should().ContainSingle("one pair, as an exporter writes it");
        scope.ToString().Should().Be("mentoragent.tenant=contoso", "a provider that prints scopes as text");

        List<string?> Line(string fragment) => Tenants(lines.First(l => l.Message.Contains(fragment)).Scopes);
    }

    [Fact]
    public async Task The_lines_of_two_tenants_turns_do_not_mix()
    {
        var source = new RequestSource();
        await using var host = Host(source);
        host.Chat!.Say("Ok.").Say("Ok.");

        Resolver(host).Resolve = _ => "contoso";
        await host.RunTurnAsync("delivery times?");
        Resolver(host).Resolve = _ => "fabrikam";
        await using (var fabrikam = host.Get<IServiceScopeFactory>().CreateAsyncScope())
            await fabrikam.ServiceProvider.GetRequiredService<IMentorOrchestrator>().SendMessageAsync("delivery times?");

        var prepared = host.Logs.ScopedRecords.Where(l => l.Message.Contains("[MentorAgent:RAG] Prepared")).ToList();
        prepared.Select(l => Tenants(l.Scopes)).Should().BeEquivalentTo(
            [new List<string?> { "contoso" }, new List<string?> { "fabrikam" }], o => o.WithStrictOrdering(),
            "each line in its own turn's scope, and in that one only");
    }

    [Fact]
    public async Task Without_a_resolver_no_line_names_a_tenant()
    {
        var source = new RequestSource();
        await using var host = Host(source, resolver: false);
        host.Chat!.CallTool("count_open_orders").Say("There are two.");

        await host.RunTurnAsync("how many open orders?");

        host.Logs.ScopedRecords.Should().NotBeEmpty()
            .And.AllSatisfy(l => Tenants(l.Scopes).Should().BeEmpty());
    }
}
