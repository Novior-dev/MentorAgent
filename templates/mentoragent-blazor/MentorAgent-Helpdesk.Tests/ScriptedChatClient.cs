using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MentorAgent_Helpdesk.Tests;

/// <summary>
/// A model that answers from a script, so the tests run without a key and give the same result every time. It asks
/// for <c>close_ticket</c> when the person writes "close ticket N" and the tool is offered, as a model would; after
/// the action it repeats what the action returned. Write your own scripts the same way.
/// </summary>
public sealed partial class ScriptedChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        var lastUser = history.FindLastIndex(m => m.Role == ChatRole.User);
        var thisTurn = history.Skip(lastUser + 1).SelectMany(m => m.Contents).ToList();

        // The action already ran, or the person said no: answer with its result, and never ask for it twice.
        if (thisTurn.OfType<FunctionResultContent>().LastOrDefault() is { } result)
            return Answer(new TextContent(result.Result?.ToString() ?? "Done."));
        if (thisTurn.OfType<FunctionCallContent>().Any())
            return Answer(new TextContent("Nothing was changed."));

        var offered = options?.Tools?.Any(t => t.Name == "close_ticket") == true;
        if (lastUser >= 0 && offered && CloseTicket().Match(history[lastUser].Text) is { Success: true } match)
            return Answer(new FunctionCallContent(Guid.NewGuid().ToString("N"), "close_ticket", new Dictionary<string, object?>
            {
                ["ticketId"] = int.Parse(match.Groups["id"].Value),
                ["resolution"] = "Solved by the support agent.",
            }));

        return Answer(new TextContent("I can list, assign and close tickets."));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static Task<ChatResponse> Answer(AIContent content)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [content])));

    [GeneratedRegex(@"close ticket (?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CloseTicket();
}
