using System.Net;
using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Rag;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Novior.MentorAgent.Tests.Rag;

/// <summary>
/// Story 196 — a source with state for the conversation was never told the conversation changed: not on New
/// conversation, not when a restored one took its place, not when one the service no longer knew was replaced (BUG-110).
/// <see cref="IMentorRagSource.OnConversationChangedAsync"/> is called in all three, on the session's own instance, so a
/// scoped source clears its state. The replacement also forgets the search's recent messages, which it used to keep. A
/// source that fails here changes nothing else; one that does not implement it works as before.
/// </summary>
public sealed class RagConversationChangeTests
{
    /// <summary>A scoped source with state for the conversation: the searches since it was last told it changed.</summary>
    private sealed class StatefulSource : IMentorRagSource
    {
        internal int SearchesSinceChange;
        internal List<MentorRagRequest> Requests { get; } = [];
        internal List<MentorConversationChange> Changes { get; } = [];
        internal Exception? ThrowOnChange { get; set; }
        internal bool NeverAnswerOnChange { get; set; }

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(MentorRagRequest request, CancellationToken ct = default)
        {
            SearchesSinceChange++;
            Requests.Add(request);
            return Task.FromResult<IReadOnlyList<MentorRagResult>>([]);
        }

        public Task<IReadOnlyList<MentorRagResult>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
            => throw new InvalidOperationException("MentorAgent calls the request overload");

        public Task OnConversationChangedAsync(MentorConversationChange change, CancellationToken ct = default)
        {
            Changes.Add(change);
            if (ThrowOnChange is { } ex) throw ex;
            if (NeverAnswerOnChange) return new TaskCompletionSource().Task;   // ignores the token, too
            SearchesSinceChange = 0;
            return Task.CompletedTask;
        }
    }

    private static MentorHostFixture Host(Action<MentorOptions>? configure = null, bool resolver = false)
        => MentorHostFixture.Create(o =>
        {
            o.UseRag = true;
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = false;   // its question would take a reply scripted for a turn
            configure?.Invoke(o);
        }, s =>
        {
            s.AddScoped<IMentorRagSource, StatefulSource>();
            if (resolver) s.AddMentorTenantResolver<TestTenantResolver>();
        });

    /// <summary>The session's own instance: the one its searches go to.</summary>
    private static StatefulSource Source(MentorHostFixture host) => (StatefulSource)host.Get<IMentorRagSource>();

    [Fact]
    public async Task New_conversation_tells_a_scoped_source_which_clears_its_state()
    {
        await using var host = Host();
        host.Chat!.SayRepeatedly("Ok.", 3);
        await host.RunTurnAsync("what is the return policy?");
        await host.RunTurnAsync("and for opened items?");
        Source(host).SearchesSinceChange.Should().Be(2);

        await host.Orchestrator.ResetSessionAsync();

        var change = Source(host).Changes.Should().ContainSingle().Subject;
        change.Reason.Should().Be(MentorConversationChangeReason.Reset);
        change.SessionId.Should().Be(host.Get<MentorAgentUIMiddleware>().Session?.Id, "the session the decisions name");
        change.TenantId.Should().BeNull("a single tenant");
        Source(host).SearchesSinceChange.Should().Be(0, "the source cleared its state");

        await host.RunTurnAsync("what is the return policy?");
        Source(host).Requests[^1].RecentMessages.Should().BeEmpty("nothing of the conversation before");
    }

    [Fact]
    public async Task A_restored_conversation_in_its_place_tells_the_source()
    {
        await using var host = Host();
        host.Chat!.SayRepeatedly("Ok.", 2);
        await host.RunTurnAsync("what is the return policy?");
        var snapshot = (await host.Orchestrator.SerializeSessionAsync())!.Value;

        await host.Orchestrator.RestoreSessionAsync(snapshot);

        Source(host).Changes.Select(c => c.Reason).Should().Equal([MentorConversationChangeReason.Restored]);
    }

    [Fact]
    public async Task Replacing_a_conversation_the_service_no_longer_knows_tells_the_source_and_forgets_the_recent_messages()
    {
        // A snapshot of a conversation the service keeps (BUG-110), restored where the service no longer knows it.
        JsonElement snapshot;
        await using (var before = MentorHostFixture.Create(o => { o.MemoryAutoCapture = false; o.UseServiceManagedHistory = true; }))
        {
            before.Chat!.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { ConversationId = "resp_1" });
            await before.RunTurnAsync("the first conversation");
            snapshot = (await before.Orchestrator.SerializeSessionAsync())!.Value;
        }

        await using var host = Host(o => o.UseServiceManagedHistory = true);
        await host.Orchestrator.RestoreSessionAsync(snapshot);
        host.Chat!.ThrowOnNextCall = new HttpRequestException(
            "HTTP 404 (invalid_request_error) Conversation with id 'conv_1' not found.", null, HttpStatusCode.NotFound);
        host.Chat.Respond((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { ConversationId = "resp_2" });

        await host.RunTurnAsync("prima domanda");
        await host.RunTurnAsync("seconda domanda");

        host.Logs.HasWarning("no longer known to the service").Should().BeTrue("the conversation was replaced");
        Source(host).Changes.Select(c => c.Reason).Should().Equal(
            [MentorConversationChangeReason.Restored, MentorConversationChangeReason.Replaced]);
        Source(host).Requests[^1].RecentMessages.Should().BeEmpty(
            "the message of the turn that failed belongs to the conversation that is gone");
    }

    [Fact]
    public async Task A_source_that_fails_when_told_changes_nothing_else()
    {
        await using var host = Host();
        host.Chat!.SayRepeatedly("Ok.", 2);
        await host.RunTurnAsync("what is the return policy?");
        Source(host).ThrowOnChange = new InvalidOperationException("the cache is down");

        await host.Orchestrator.ResetSessionAsync();

        host.Logs.Records.Should().Contain(r => r.Level == LogLevel.Warning
            && r.Message.Contains("failed when told the conversation changed (Reset)")
            && r.Message.Contains(typeof(StatefulSource).FullName!));
        host.Logs.Records.Should().Contain(r => r.Message.Contains("New conversation: the session was reset."));
        Source(host).ThrowOnChange = null;
        (await host.RunTurnAsync("and now?")).Should().BeNull("the next turn works");
    }

    [Fact]
    public async Task A_source_that_never_answers_holds_the_reset_up_no_longer_than_the_timeout()
    {
        await using var host = Host(o => o.SafetyCheckTimeout = TimeSpan.FromMilliseconds(200));
        host.Chat!.Say("Ok.");
        await host.RunTurnAsync("what is the return policy?");
        Source(host).NeverAnswerOnChange = true;

        var reset = host.Orchestrator.ResetSessionAsync();
        (await Task.WhenAny(reset, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(reset, "bounded by SafetyCheckTimeout");
        await reset;

        host.Logs.HasWarning("failed when told the conversation changed (Reset)").Should().BeTrue(host.Logs.WarningText);
    }

    [Fact]
    public async Task With_rag_off_the_source_is_not_told()
    {
        await using var host = Host(o => o.UseRag = false);
        host.Chat!.Say("Ok.");
        await host.RunTurnAsync("hello");

        await host.Orchestrator.ResetSessionAsync();

        Source(host).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_that_does_not_implement_it_works_as_before()
    {
        var source = new ScriptedRagSource();
        await using var host = MentorHostFixture.Create(o =>
        {
            o.UseRag = true;
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = false;
        }, s => s.AddSingleton<IMentorRagSource>(source));
        host.Chat!.SayRepeatedly("Ok.", 2);
        await host.RunTurnAsync("what is the return policy?");

        await host.Orchestrator.ResetSessionAsync();
        await host.RunTurnAsync("and now?");

        source.Searches.Should().HaveCount(2);
        host.Logs.Records.Should().NotContain(r => r.Message.Contains("when told the conversation changed"));
    }

    [Fact]
    public async Task With_a_resolver_the_source_is_told_the_sessions_tenant()
    {
        await using var host = Host(resolver: true);
        ((TestTenantResolver)host.Get<IMentorTenantResolver>()).Resolve = _ => "contoso";
        host.Chat!.Say("Ok.");
        await host.RunTurnAsync("what is the return policy?");

        await host.Orchestrator.ResetSessionAsync();

        Source(host).Changes.Should().ContainSingle().Which.TenantId.Should().Be("contoso");
    }
}
