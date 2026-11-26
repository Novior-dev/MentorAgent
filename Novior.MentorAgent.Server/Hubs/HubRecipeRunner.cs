using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Novior.MentorAgent.Core;

namespace Novior.MentorAgent.Server.Hubs;

/// <summary>
/// Story 1638, slice C — a recipe run by a WebAssembly client of the hub (<c>recipe.hub</c>). The hub asks the client to
/// run the steps (<c>RecipeRequested</c>, with an id of the run) and the model waits for the answer, which the client
/// sends with <c>POST /mentor/recipe-result</c>: over HTTP, because the hub takes a connection's calls one at a time, and
/// the turn waiting for the answer is one of them. One per connection scope, bound by the hub to its connection; the
/// path of the page is the one the client sent with its page context, which a client that cannot run recipes does not
/// send.
/// </summary>
internal sealed class HubRecipeRunner(IHubContext<MentorHub> hub) : IMentorRecipeRunner, IDisposable
{
    /// <summary>How long the model waits for the client's answer.</summary>
    internal TimeSpan Answer { get; set; } = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<Guid, Run> _runs = new();

    /// <summary>A run that waits for the client's answer, and how many steps it has.</summary>
    private sealed record Run(TaskCompletionSource<MentorRecipeOutcome> Answer, int Steps);

    /// <summary>What became of an answer of the client.</summary>
    internal enum Answered
    {
        /// <summary>The run took it: the model reads it.</summary>
        Taken,

        /// <summary>No run of this connection waits for it: unknown, of another connection, or answered already.</summary>
        NoRun,

        /// <summary>A step the run does not have: refused, and the run still waits.</summary>
        Refused,
    }

    /// <summary>The runs that wait for the client, for the tests.</summary>
    internal IReadOnlyCollection<Guid> Waiting => [.. _runs.Keys];
    private string? _connectionId;
    private string? _path;

    /// <summary>Whether a hub connection owns this scope.</summary>
    internal bool IsBound => _connectionId is not null;

    internal void Bind(string connectionId) => _connectionId = connectionId;

    /// <summary>The page the client shows, as it sent it: a path, at most 2,048 characters; anything else is none.</summary>
    internal void Show(string? path)
        => _path = path is { Length: > 0 and <= 2048 } && path[0] == '/' && !path.Contains('?') && !path.Contains('#') ? path : null;

    public string? PagePath => _path;

    public async Task<MentorRecipeOutcome> RunAsync(IReadOnlyList<MentorRecipeStepCall> steps, CancellationToken ct)
    {
        if (_connectionId is not { } connection)
            throw new InvalidOperationException("No client to run the recipe in.");

        // The id is the server's, random, and answered once: the client cannot choose or reuse it.
        var id = Guid.NewGuid();
        var answer = new TaskCompletionSource<MentorRecipeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runs[id] = new Run(answer, steps.Count);
        try
        {
            await hub.Clients.Client(connection).SendAsync("RecipeRequested", id.ToString(), steps, ct).ConfigureAwait(false);
            return await answer.Task.WaitAsync(Answer, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The browser did not answer the recipe within {Answer.TotalSeconds:0} s.");
        }
        finally
        {
            _runs.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// The client's answer to the run <paramref name="id"/>. Done is at the end of the steps, not done at one of them:
    /// any other step is refused, and the run still waits for its answer.
    /// </summary>
    internal Answered Complete(Guid id, MentorRecipeOutcome outcome)
    {
        if (!_runs.TryGetValue(id, out var run)) return Answered.NoRun;
        if (outcome.Done ? outcome.Step != run.Steps : outcome.Step >= run.Steps) return Answered.Refused;
        return _runs.TryRemove(new KeyValuePair<Guid, Run>(id, run)) && run.Answer.TrySetResult(outcome)
            ? Answered.Taken
            : Answered.NoRun;
    }

    /// <summary>
    /// The connection is gone. A run that still waits ends at once all the same, with the turn's token, which the end of the
    /// connection cancels: its call reads <c>session-ended</c>, as any action stopped by a closing connection.
    /// </summary>
    public void Dispose() => _runs.Clear();
}
