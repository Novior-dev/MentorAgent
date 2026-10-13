using A2A;
using FluentAssertions;
using Novior.MentorAgent.Extensions;
using Novior.MentorAgent.Options;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Xunit;

namespace Novior.MentorAgent.Tests.Federation;

/// <summary>
/// A task from another agent has nobody to confirm an action — so a confirmation-gated tool must be
/// refused at once, not waited on.
///
/// <para>
/// Found while deciding BUG-099's default (23 Sep 2026). The agreed mitigation for "a write action
/// answered a read question" is <c>RequiresConfirmation</c> on destructive actions, and the review of the
/// rc.12 notes had already pointed out the catch: on an A2A task both approval paths — the blocking one in
/// the middleware and the native one in the orchestrator — waited on the turn's token for an answer no
/// one could give. The task hung until the calling agent gave up. The agent card already leaves such
/// actions out ("nobody to confirm"); the runtime now says the same thing.
/// </para>
/// </summary>
public sealed class A2ANobodyToConfirmTests
{
    private static async Task<ServerAppFactory> HostAsync(MentorHitlMode mode) => await ServerAppFactory.CreateAsync(
        configure: o =>
        {
            o.A2AServerEnabled = true;
            o.A2AServerUrl = "http://localhost/a2a";
            o.MemoryAutoCapture = false;
            o.HitlMode = mode;
        },
        configureApp: a => a.UseEndpoints(e => e.MapMentorAgentA2A()));

    [Theory]
    [InlineData(MentorHitlMode.Blocking)]
    [InlineData(MentorHitlMode.Native)]
    public async Task A_confirmation_gated_tool_on_an_a2a_task_is_refused_at_once(MentorHitlMode mode)
    {
        await using var app = await HostAsync(mode);
        app.Chat.CallTool("issue_refund", new { orderId = 7 }).Say("Serve la conferma di una persona.");

        var card = await new A2A.A2ACardResolver(new Uri("http://localhost"), app.Client).GetAgentCardAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await A2A.A2AClientFactory.Create(card, app.Client)
            .SendMessageAsync("rimborsa l'ordine 7", A2A.Role.User, null, deadline.Token);

        response.Should().NotBeNull("the task must end instead of waiting for a confirmation nobody can give");
        var results = app.Chat.Calls
            .SelectMany(c => c.Messages)
            .SelectMany(m => m.Contents)
            .Select(c => c switch
            {
                FunctionResultContent r => r.Result?.ToString() ?? "",
                ToolApprovalResponseContent a => $"approved={a.Approved} {a.Reason}",
                _ => null,
            })
            .Where(t => t is not null)
            .ToList();
        results.Should().Contain(r => r!.Contains("nobody to", StringComparison.OrdinalIgnoreCase),
            "the model is told why the action was not run. Seen: {0}", string.Join(" | ", results));
    }
}
