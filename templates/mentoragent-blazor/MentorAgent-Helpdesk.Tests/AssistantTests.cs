using MentorAgent_Helpdesk.Identity;
using MentorAgent_Helpdesk.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Novior.MentorAgent.Evaluation;

namespace MentorAgent_Helpdesk.Tests;

/// <summary>
/// The assistant of the app, with the app's own registrations and a scripted model: each test is one conversation
/// through MentorAgent, with its role checks and its confirmations, and then a look at the tickets.
/// </summary>
public sealed class AssistantTests
{
    /// <summary>A host like the app's, with its environment and logging, and the app's own registrations.</summary>
    private static IHost CreateApp()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHelpdesk();
        builder.Services.AddHelpdeskAssistant(new ScriptedChatClient());
        return builder.Build();
    }

    /// <summary>One turn, as <paramref name="userId"/>. <paramref name="approve"/> is the answer to every confirmation.</summary>
    private static async Task<MentorEvalResult> AskAsync(IHost app, string userId, bool approve, EvalTurn turn)
    {
        var options = new MentorEvalOptions
        {
            // By default every confirmation is answered no: an evaluation does not change data unless it says so.
            ApproveConfirmations = approve,
            PrepareSession = (session, _) =>
            {
                session.GetRequiredService<TestSignIn>().SignInAs(TestUser.Find(userId));
                return Task.CompletedTask;
            },
        };

        var report = await app.Services.GetRequiredService<MentorEvaluator>()
            .RunConversationsAsync([new EvalConversation("helpdesk", [turn])], options);

        Assert.True(report.Passed, report.FailureReason);
        return Assert.Single(report.Results);
    }

    private static Ticket Ticket(IHost app, int id) => app.Services.GetRequiredService<TicketStore>().Find(id)!;

    [Fact]
    public async Task A_support_agent_who_confirms_closes_the_ticket()
    {
        using var app = CreateApp();

        var turn = await AskAsync(app, "alex", approve: true,
            new EvalTurn("Close ticket 3, the laptop was delivered") { ExpectTools = ["close_ticket"] });

        Assert.Contains("confirmation asked: close_ticket", turn.Notes);
        Assert.Equal("Ticket 3 is closed.", turn.Output);
        Assert.Equal(TicketStatus.Closed, Ticket(app, 3).Status);
    }

    [Fact]
    public async Task Without_a_yes_the_ticket_stays_open()
    {
        using var app = CreateApp();

        var turn = await AskAsync(app, "alex", approve: false,
            new EvalTurn("Close ticket 3, the laptop was delivered") { ExpectTools = ["close_ticket"] });

        Assert.Contains("confirmation asked: close_ticket", turn.Notes);
        Assert.Equal(TicketStatus.Open, Ticket(app, 3).Status);
    }

    [Fact]
    public async Task Someone_without_the_Agent_role_is_not_offered_close_ticket()
    {
        using var app = CreateApp();

        await AskAsync(app, "sam", approve: true,
            new EvalTurn("Close ticket 3, the laptop was delivered") { ForbidTools = ["close_ticket"] });

        Assert.Equal(TicketStatus.Open, Ticket(app, 3).Status);
    }
}
