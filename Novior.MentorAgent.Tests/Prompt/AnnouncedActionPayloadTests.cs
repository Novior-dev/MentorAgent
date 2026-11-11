using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Novior.MentorAgent.Core;
using Novior.MentorAgent.Middleware;
using Novior.MentorAgent.Tests.Fakes;
using Novior.MentorAgent.Tests.Harness;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace Novior.MentorAgent.Tests.Prompt;

/// <summary>
/// Story 526 — what the second request of the announced-action check carries on the wire with Chat Completions, the API
/// of the A/B in which it never called the action it reported: the real OpenAI client over a recording HTTP handler, no
/// network. A user who is not signed in, an action left out for them (so the notice of BUG-224 is there, with its
/// sign-in sentence), and an action they may run, reported as done without being called.
/// </summary>
public sealed class AnnouncedActionPayloadTests
{
    private const string Reported = "The order card for order 3 is now shown.";

    /// <summary>The heading of the notice of an action left out (BUG-200, BUG-224).</summary>
    private const string NoticeHeading = "## Action status for THIS request";

    [Fact]
    public async Task On_chat_completions_the_second_request_has_the_same_tools_and_ends_with_the_instruction()
    {
        var wire = new RecordingHandler(Reported, "I could not show it.");
        var classifier = new FakeChatClient();
        classifier.Say("CLAIMED show_order_card");
        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = ChatCompletions(wire);
            o.ClassifierChatClient = classifier;
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = true;
        }, withChatClient: false);

        (await host.RunTurnAsync("Show order 3 as a card")).Should().BeNull(host.Logs.WarningText);

        var requests = wire.Requests.Where(r => r.TryGetProperty("tools", out _)).ToList();
        requests.Should().HaveCount(2, "the reply that reported the action, and the second request");
        var (first, second) = (requests[0], requests[1]);

        // The tools: the same as the first request's, the one reported among them, the one left out not.
        ToolNames(second).Should().Equal(ToolNames(first)).And.Contain("show_order_card").And.NotContain("export_ledger");
        (second.TryGetProperty("tool_choice", out var choice) ? choice.ToString() : "auto").Should().Be("auto");

        // The messages: the system message that heads the conversation keeps the instructions and the notice of the action
        // left out, with its sign-in sentence. The instruction comes last, a message of its own after the reply that
        // reported the action: the last thing the model reads, and it names the tool. Before the redo it ended the
        // instructions, at the head of the conversation, and offered "it cannot be done here" as the notice does. It is a
        // user message, not a system one: many templates refuse a system message that is not the first (BUG-231).
        var messages = second.GetProperty("messages").EnumerateArray().ToList();
        Role(messages[0]).Should().BeOneOf("system", "developer");
        var instructions = Flat(Text(messages[0]));
        instructions.Should().Contain(NoticeHeading).And.Contain(Flat(MentorPromptTexts.English.SignInMayHelp))
            .And.NotContain(AnnouncedActionChatClient.Heading);
        Role(messages[^2]).Should().Be("assistant");
        Text(messages[^2]).Should().Be(Reported);
        Role(messages[^1]).Should().Be("user");
        Flat(Text(messages[^1])).Should().Be(Flat(AnnouncedActionChatClient.Heading
            + MentorPromptTexts.English.AnnouncedActionCall.Replace("{tool}", "show_order_card")));

        host.Logs.Records.Should().Contain(r => r.Message.Contains("Announced-action check: CLAIMED show_order_card"))
            .And.Contain(r => r.Message.Contains("the second request carries") && r.Message.Contains("show_order_card"));
    }

    [Fact]
    public async Task A_verdict_of_none_is_logged_at_information_too()
    {
        var wire = new RecordingHandler("Order 3 is open.");
        var classifier = new FakeChatClient();
        classifier.Say("NONE");
        await using var host = MentorHostFixture.Create(o =>
        {
            o.ChatClient = ChatCompletions(wire);
            o.ClassifierChatClient = classifier;
            o.MemoryAutoCapture = false;
            o.CheckAnnouncedActions = true;
        }, withChatClient: false);

        await host.RunTurnAsync("Is order 3 open?");

        host.Logs.Records.Should().Contain(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && r.Message.Contains("Announced-action check: NONE"),
            "a reply that still reports an invented \"done\" is told apart from a second request that invented it again");
        wire.Requests.Count(r => r.TryGetProperty("tools", out _)).Should().Be(1, "NONE asks for no second request");
    }

    /// <summary>The OpenAI client of the library's hosts, Chat Completions, over <paramref name="wire"/>.</summary>
    private static IChatClient ChatCompletions(RecordingHandler wire)
        => new OpenAI.Chat.ChatClient("gpt-4.1", new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://chat-completions.invalid/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(wire)),
        }).AsIChatClient();

    private static List<string> ToolNames(JsonElement request)
        => [.. request.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!)];

    private static string Role(JsonElement message) => message.GetProperty("role").GetString()!;

    /// <summary>A message's text, whether the client wrote its content as a string or as parts.</summary>
    private static string Text(JsonElement message)
        => message.TryGetProperty("content", out var content)
            ? content.ValueKind == JsonValueKind.String
                ? content.GetString()!
                : string.Concat(content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : ""))
            : string.Empty;

    /// <summary>A text with its line breaks as one kind: a raw string literal's are those of its source file.</summary>
    private static string Flat(string text) => text.Replace("\r\n", "\n");

    /// <summary>
    /// Answers each request with the next reply, streamed when the request asks for a stream, and keeps the request's
    /// JSON.
    /// </summary>
    private sealed class RecordingHandler(params string[] replies) : HttpMessageHandler
    {
        private readonly Queue<string> _replies = new(replies);

        internal List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
            lock (Requests) Requests.Add(body);
            var text = JsonSerializer.Serialize(_replies.TryDequeue(out var next) ? next : "Ok.");
            var streamed = body.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;
            const string head = "\"id\":\"c1\",\"created\":1,\"model\":\"gpt-4.1\"";

            var payload = streamed
                ? $"data: {{{head},\"object\":\"chat.completion.chunk\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":{text}}},\"finish_reason\":null}}]}}\n\n" +
                  $"data: {{{head},\"object\":\"chat.completion.chunk\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}]}}\n\n" +
                  "data: [DONE]\n\n"
                : $"{{{head},\"object\":\"chat.completion\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":{text}}},\"finish_reason\":\"stop\"}}]," +
                  "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, streamed ? "text/event-stream" : "application/json"),
            };
        }
    }
}
