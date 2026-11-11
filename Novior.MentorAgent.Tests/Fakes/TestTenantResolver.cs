using System.Collections.Concurrent;
using Novior.MentorAgent.Abstractions.Interfaces;
using Novior.MentorAgent.Abstractions.Models;

namespace Novior.MentorAgent.Tests.Fakes;

/// <summary>
/// An <see cref="IMentorTenantResolver"/> that keeps every request it is asked about and answers as the test tells it: by
/// default "ada" belongs to contoso and "bob" to fabrikam, and anybody else to no tenant.
/// </summary>
internal sealed class TestTenantResolver : IMentorTenantResolver
{
    private readonly ConcurrentQueue<MentorTenantRequest> _seen = new();

    /// <summary>The answer; it may throw.</summary>
    internal Func<MentorTenantRequest, string?> Resolve { get; set; } = r => r.User?.Identity?.Name switch
    {
        "ada" => "contoso",
        "bob" => "fabrikam",
        _ => null,
    };

    /// <summary>Every request it was asked about, in order.</summary>
    internal IReadOnlyList<MentorTenantRequest> Seen => [.. _seen];

    public ValueTask<string?> ResolveAsync(MentorTenantRequest request, CancellationToken cancellationToken)
    {
        _seen.Enqueue(request);
        return ValueTask.FromResult(Resolve(request));
    }
}
