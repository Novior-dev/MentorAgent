using System.Collections.Concurrent;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// An <see cref="IMentorActionGovernor"/> that keeps every question it is asked, in order, and answers as the test
/// tells it: by default it leaves every action to the code.
/// </summary>
internal sealed class TestActionGovernor : IMentorActionGovernor
{
    private readonly ConcurrentQueue<MentorActionRequest> _seen = new();
    private readonly ConcurrentQueue<MentorActionRequest> _rolesSeen = new();

    /// <summary>The answer of <see cref="IsEnabledAsync"/>: null leaves the code's value.</summary>
    internal Func<MentorActionRequest, bool?> Enabled { get; set; } = _ => null;

    /// <summary>Runs before the answer, with the governor's token: to throw, to wait.</summary>
    internal Func<MentorActionRequest, CancellationToken, Task>? Before { get; set; }

    /// <summary>The answer of <see cref="GetRequiredRolesAsync"/>: null leaves the code's roles.</summary>
    internal Func<MentorActionRequest, IReadOnlyList<string>?> Roles { get; set; } = _ => null;

    /// <summary>Runs before the roles' answer, with the governor's token: to throw, to wait.</summary>
    internal Func<MentorActionRequest, CancellationToken, Task>? BeforeRoles { get; set; }

    /// <summary>The answer of <see cref="GetConfirmationAsync"/>: <see cref="MentorConfirmationChoice.Code"/> leaves the code's.</summary>
    internal Func<MentorActionRequest, MentorConfirmationChoice> Confirmation { get; set; } = _ => MentorConfirmationChoice.Code;

    /// <summary>Runs before the confirmation's answer, with the governor's token: to throw, to wait.</summary>
    internal Func<MentorActionRequest, CancellationToken, Task>? BeforeConfirmation { get; set; }

    /// <summary>Every question of <see cref="IsEnabledAsync"/>, in order.</summary>
    internal IReadOnlyList<MentorActionRequest> Seen => [.. _seen];

    /// <summary>Every question of <see cref="GetRequiredRolesAsync"/>, in order.</summary>
    internal IReadOnlyList<MentorActionRequest> RolesSeen => [.. _rolesSeen];

    /// <summary>Every question of <see cref="GetConfirmationAsync"/>, in order.</summary>
    internal IReadOnlyList<MentorActionRequest> ConfirmationSeen => [.. _confirmationSeen];

    private readonly ConcurrentQueue<MentorActionRequest> _confirmationSeen = new();

    /// <summary>The answer of <see cref="GetDescriptionAsync"/>: null leaves the code's description.</summary>
    internal Func<MentorActionRequest, string?> Description { get; set; } = _ => null;

    /// <summary>Runs before the description's answer, with the governor's token: to throw, to wait.</summary>
    internal Func<MentorActionRequest, CancellationToken, Task>? BeforeDescription { get; set; }

    /// <summary>Every question of <see cref="GetDescriptionAsync"/>, in order.</summary>
    internal IReadOnlyList<MentorActionRequest> DescriptionSeen => [.. _descriptionSeen];

    private readonly ConcurrentQueue<MentorActionRequest> _descriptionSeen = new();

    /// <summary>The questions about <paramref name="tool"/> at <paramref name="stage"/>.</summary>
    internal IReadOnlyList<MentorActionRequest> Asked(string tool, MentorActionStage stage)
        => [.. Seen.Where(r => r.ToolName == tool && r.Stage == stage)];

    public async ValueTask<bool?> IsEnabledAsync(MentorActionRequest request, CancellationToken cancellationToken)
    {
        _seen.Enqueue(request);
        if (Before is not null) await Before(request, cancellationToken);
        return Enabled(request);
    }

    public async ValueTask<IReadOnlyList<string>?> GetRequiredRolesAsync(MentorActionRequest request, CancellationToken cancellationToken)
    {
        _rolesSeen.Enqueue(request);
        if (BeforeRoles is not null) await BeforeRoles(request, cancellationToken);
        return Roles(request);
    }

    public async ValueTask<MentorConfirmationChoice> GetConfirmationAsync(MentorActionRequest request, CancellationToken cancellationToken)
    {
        _confirmationSeen.Enqueue(request);
        if (BeforeConfirmation is not null) await BeforeConfirmation(request, cancellationToken);
        return Confirmation(request);
    }

    public async ValueTask<string?> GetDescriptionAsync(MentorActionRequest request, CancellationToken cancellationToken)
    {
        _descriptionSeen.Enqueue(request);
        if (BeforeDescription is not null) await BeforeDescription(request, cancellationToken);
        return Description(request);
    }
}

/// <summary>A governor that answers nothing of its own: two of them registered is one too many.</summary>
internal sealed class SecondActionGovernor : IMentorActionGovernor;
