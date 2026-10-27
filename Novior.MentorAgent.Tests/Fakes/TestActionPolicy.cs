using System.Collections.Concurrent;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// An <see cref="IMentorActionPolicy"/> that keeps every question it is asked, in order, and answers as the test
/// tells it: by default it allows everything and has no objection.
/// </summary>
internal sealed class TestActionPolicy(string name = "policy", ConcurrentQueue<string>? order = null) : IMentorActionPolicy
{
    private readonly ConcurrentQueue<(string Method, MentorActionRequest Request)> _seen = new();

    /// <summary>The answer of <see cref="IsAllowedAsync"/>.</summary>
    internal Func<MentorActionRequest, bool> Allowed { get; set; } = _ => true;

    /// <summary>The answer of <see cref="BeforeActionAsync"/>.</summary>
    internal Func<MentorActionRequest, MentorActionVerdict> Verdict { get; set; } = _ => MentorActionVerdict.Proceed;

    /// <summary>Runs before either answer, with the policy's token: to throw, to wait, to read a service.</summary>
    internal Func<MentorActionRequest, CancellationToken, Task>? Before { get; set; }

    /// <summary>Every question, as (method, request), in order.</summary>
    internal IReadOnlyList<(string Method, MentorActionRequest Request)> Seen => [.. _seen];

    /// <summary>The questions about <paramref name="tool"/> at <paramref name="stage"/>, as their method's name.</summary>
    internal IReadOnlyList<string> Asked(string tool, MentorActionStage stage = MentorActionStage.Call)
        => [.. Seen.Where(s => s.Request.ToolName == tool && s.Request.Stage == stage).Select(s => s.Method)];

    public async ValueTask<bool> IsAllowedAsync(MentorActionRequest request, CancellationToken cancellationToken)
    {
        Record(nameof(IsAllowedAsync), request);
        if (Before is not null) await Before(request, cancellationToken);
        return Allowed(request);
    }

    public async ValueTask<MentorActionVerdict> BeforeActionAsync(MentorActionRequest request, CancellationToken cancellationToken)
    {
        Record(nameof(BeforeActionAsync), request);
        if (Before is not null) await Before(request, cancellationToken);
        return Verdict(request);
    }

    private void Record(string method, MentorActionRequest request)
    {
        _seen.Enqueue((method, request));
        order?.Enqueue($"{name}.{method}.{request.Stage}");
    }
}
