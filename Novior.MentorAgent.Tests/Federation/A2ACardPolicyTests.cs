using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// BUG-250 — the A2A agent card listed an action an action policy turns off: it was built once, at startup, without
/// asking the policies, and a task then refused the action it promised. The card has no caller, while a policy decides
/// for one, so the card asks at <see cref="MentorActionStage.Card"/>, with nobody: a policy answers no only for an action
/// off for every caller. A policy that does not know the stage and looks at the user sees nobody and often says no: the
/// card leaves the action out, promising less, never more. The card is built again when it is older than 30 seconds,
/// so a rule changed at run time reaches it. The action catalogue says what the code lets the card list, whether the
/// card was read or not: a bridge that reads it once must not get an answer that depends on that.
/// </summary>
[Trait("Rule", "policy.card")]
public sealed class A2ACardPolicyTests
{
    /// <summary>A clock the test moves.</summary>
    private sealed class ManualTime : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Task<ServerAppFactory> SiteAsync(IMentorActionPolicy policy, TimeProvider? time = null)
        => ServerAppFactory.CreateAsync(
            configure: o => { o.A2AServerEnabled = true; o.AppName = "Acme"; },
            configureServices: s =>
            {
                s.AddMentorActionPolicy(policy);
                if (time is not null) s.AddSingleton(time);
            },
            configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

    private static async Task<List<string>> SkillsAsync(ServerAppFactory app)
    {
        using var card = JsonDocument.Parse(await app.Client.GetStringAsync("/.well-known/agent-card.json"));
        return [.. card.RootElement.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("id").GetString()!)];
    }

    [Fact]
    public async Task An_action_a_policy_turns_off_for_every_caller_is_not_on_the_card()
    {
        var policy = new TestActionPolicy
        {
            Allowed = r => !(r.Stage == MentorActionStage.Card && r.ToolName == "count_open_orders"),
        };
        await using var app = await SiteAsync(policy);

        var skills = await SkillsAsync(app);

        skills.Should().NotContain("count_open_orders").And.Contain("show_order_card");
        var asked = policy.Seen.Where(s => s.Request.Stage == MentorActionStage.Card).Select(s => s.Request).ToList();
        asked.Should().NotBeEmpty();
        asked.Should().OnlyContain(r => r.User == null && r.SessionId == null && r.Arguments == null
                                        && r.Surface == MentorActionSurface.A2A && r.Provenance == MentorActionProvenance.Application,
            "the card has no caller and no call");
        app.Services.GetRequiredService<IMentorActionCatalog>().GetActions()
            .Single(a => a.ToolName == "count_open_orders").OnAgentCard.Should().BeTrue(
                "the catalogue says what the code lets the card list; the policy takes it off the card when the card is read");
    }

    [Fact]
    public async Task A_policy_that_decides_per_caller_and_knows_the_stage_keeps_the_action_on_the_card()
    {
        var policy = new TestActionPolicy
        {
            Allowed = r => r.Stage == MentorActionStage.Card || r.User?.IsInRole("Manager") == true,
        };
        await using var app = await SiteAsync(policy);

        (await SkillsAsync(app)).Should().Contain("count_open_orders", "per caller is decided at the task, with its caller");
    }

    [Fact]
    public async Task A_policy_that_does_not_know_the_stage_and_looks_at_the_user_leaves_the_action_out()
    {
        var policy = new TestActionPolicy { Allowed = r => r.User?.Identity?.IsAuthenticated == true };
        await using var app = await SiteAsync(policy);

        (await SkillsAsync(app)).Should().NotContain("count_open_orders", "nobody calls the card: it promises less, never more");
    }

    [Fact]
    public async Task A_rule_changed_at_run_time_reaches_the_card_once_it_is_older_than_thirty_seconds()
    {
        var off = false;
        var time = new ManualTime();
        var policy = new TestActionPolicy { Allowed = r => !(off && r.ToolName == "count_open_orders") };
        await using var app = await SiteAsync(policy, time);

        (await SkillsAsync(app)).Should().Contain("count_open_orders");
        off = true;
        time.Now += TimeSpan.FromSeconds(10);
        (await SkillsAsync(app)).Should().Contain("count_open_orders", "a card younger than 30 seconds is served as it is");

        time.Now += TimeSpan.FromSeconds(25);
        (await SkillsAsync(app)).Should().NotContain("count_open_orders", "then it is built again, with the rule");
        var response = await app.Client.GetAsync("/.well-known/agent-card.json");
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromSeconds(30));
    }
}
