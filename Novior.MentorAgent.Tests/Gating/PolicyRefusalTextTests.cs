using FluentAssertions;
using Microsoft.Extensions.AI;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.App;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Gating;

/// <summary>
/// BUG-248 — an action an action policy took away was refused with "Accesso negato: non sei autorizzato a usare questa
/// azione", in chat and on A2A: a refusal of the user's own where the application has a rule, which may hold for every
/// user. A policy says no without a reason, and it may decide per user, so the text says neither "you" nor "everyone": a
/// rule of the application does not offer the action here. Whether the policy left the action out of the turn (not
/// offered, BUG-233) or said no at the call. A <c>Stop</c>, which has its reason, is unchanged.
/// </summary>
public sealed class PolicyRefusalTextTests
{
    private static MentorHostFixture Host(RecordingActionObserver observer, TestActionPolicy policy)
    {
        var host = MentorHostFixture.Create(o =>
        {
            o.MemoryAutoCapture = false;
            o.Language = MentorLanguage.Italian;
        }, s =>
        {
            s.AddMentorActionObserver(observer);
            s.AddMentorActionPolicy(policy);
        });
        host.Auth.SignIn("ada", "Manager", "Admin");
        return host;
    }

    private static string WhatTheModelRead(FakeChatClient chat) => string.Join("\n", chat.Calls
        .SelectMany(c => c.Messages).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
        .Select(r => System.Text.RegularExpressions.Regex.Unescape(r.Result?.ToString() ?? "")));

    [Fact]
    public async Task An_action_a_policy_did_not_offer_is_refused_as_a_rule_of_the_application_never_as_the_users_fault()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Allowed = r => r.ToolName != "count_open_orders" };
        await using var host = Host(observer, policy);
        host.Chat!.CallTool("count_open_orders").Say("Non posso.");

        await host.RunTurnAsync("quanti ordini aperti ci sono?");
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps("count_open_orders").Should().Equal("Denied/not-offered");
        host.Get<TestOrderService>().Invoked.Should().BeEmpty();
        WhatTheModelRead(host.Chat!).Should().Contain("Non disponibile: una regola dell'applicazione non offre questa azione qui.")
            .And.NotContain("Accesso negato").And.NotContain("non sei autorizzato");
    }

    [Fact]
    public async Task An_action_a_policy_refuses_at_the_call_is_refused_with_the_same_words()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy { Allowed = r => r.Stage == MentorActionStage.Offer };
        await using var host = Host(observer, policy);
        host.Chat!.CallTool("count_open_orders").Say("Non posso.");

        await host.RunTurnAsync("quanti ordini aperti ci sono?");
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps("count_open_orders").Should().Equal("Denied/not-authorized");
        WhatTheModelRead(host.Chat!).Should().Contain("Non disponibile: una regola dell'applicazione non offre questa azione qui.")
            .And.NotContain("Accesso negato");
    }

    [Fact]
    public async Task A_stop_of_a_policy_keeps_its_reason()
    {
        var observer = new RecordingActionObserver();
        var policy = new TestActionPolicy
        {
            Verdict = r => r.ToolName == "count_open_orders" ? MentorActionVerdict.Stop("fuori orario") : MentorActionVerdict.Proceed,
        };
        await using var host = Host(observer, policy);
        host.Chat!.CallTool("count_open_orders").Say("Non posso.");

        await host.RunTurnAsync("quanti ordini aperti ci sono?");
        await host.Get<MentorActionDecisions>().FlushAsync();

        observer.Steps("count_open_orders").Last().Should().Be("Blocked/policy");
        WhatTheModelRead(host.Chat!).Should().Contain("Fermata da una regola: fuori orario")
            .And.NotContain("Non disponibile: una regola");
    }
}
