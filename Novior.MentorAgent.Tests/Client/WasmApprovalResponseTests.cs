using System.Net;
using FluentAssertions;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;
using Novior.MentorAgent.Blazor.Hub;
using Novior.MentorAgent.Blazor.Models;
using Novior.MentorAgent.Blazor.Services;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Novior.MentorAgent.Tests.Client;

/// <summary>
/// The WebAssembly client reports an answer to a confirmation only when the server says a turn received it
/// (BUG-129).
///
/// <para>
/// Up to rc.14 <see cref="IMentorStateService.OnApprovalResponse"/> was raised before the request went out. A
/// click that landed after Stop — before the idle state reached the page — was reported as an approval while
/// the server had nothing left to run. The server now answers such a click with a 404
/// (<c>ApprovalLifetimeTests</c>), and the client raises nothing for it.
/// </para>
/// </summary>
public sealed class WasmApprovalResponseTests
{
    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static (WasmMentorStateService State, Answering Server, TestLogCollector Logs) Client(HttpStatusCode status)
    {
        var options = new MentorAgentBlazorOptions { HubUrl = "https://api.example.com/mentor-hub" };
        var server = new Answering(status);
        var logs = new TestLogCollector();
        var state = new WasmMentorStateService(
            new MentorHubClient(options, NullLogger<MentorHubClient>.Instance), new WasmMentorPageContext(),
            new TypedLogger<WasmMentorStateService>(new TestLoggerProvider(logs).CreateLogger("wasm")),
            new HttpClient(server) { BaseAddress = new Uri("https://client.example.com/") }, options);
        return (state, server, logs);
    }

    private static ConfirmationRequest Raise(WasmMentorStateService state)
    {
        var req = new ConfirmationRequest { ActionId = Guid.NewGuid(), ToolName = "issue_refund", Message = "Refund order 1001" };
        state.NotifyConfirmationRequired(req);
        return req;
    }

    private static List<bool> Answers(WasmMentorStateService state)
    {
        var answers = new List<bool>();
        state.OnApprovalResponse += (_, approved) => { lock (answers) answers.Add(approved); };
        return answers;
    }

    [Fact]
    public async Task A_confirm_the_server_answers_with_404_is_no_approval()
    {
        var (state, server, logs) = Client(HttpStatusCode.NotFound);
        var answers = Answers(state);
        var req = Raise(state);

        await state.ConfirmAsync(req.ActionId);

        server.Requests.Should().Be(1, "the server decides whether anything still waits");
        answers.Should().BeEmpty("nothing ran: the click came after the turn stopped");
        logs.Records.Should().NotContain(r => r.Level >= LogLevel.Warning,
            "a late click is not a fault worth an error in the console");
    }

    [Fact]
    public async Task A_confirm_the_server_accepts_is_reported()
    {
        // The control: without it, "never raise the event" satisfies the test above.
        var (state, _, _) = Client(HttpStatusCode.OK);
        var answers = Answers(state);
        var req = Raise(state);

        await state.ConfirmAsync(req.ActionId);

        answers.Should().Equal(true);
    }

    [Fact]
    public async Task A_denial_is_reported_only_once_the_server_has_it()
    {
        var (accepted, acceptedServer, _) = Client(HttpStatusCode.OK);
        var acceptedAnswers = Answers(accepted);
        accepted.Cancel(Raise(accepted).ActionId);

        var (late, lateServer, _) = Client(HttpStatusCode.NotFound);
        var lateAnswers = Answers(late);
        late.Cancel(Raise(late).ActionId);

        // Cancel is fire-and-forget: the answer arrives on the request's continuation.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((acceptedAnswers.Count == 0 || lateServer.Requests == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(100);

        acceptedServer.Requests.Should().Be(1);
        acceptedAnswers.Should().Equal(false);
        lateAnswers.Should().BeEmpty();
    }
}
